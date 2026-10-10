package com.veltrix.admin.ui.common

import com.veltrix.admin.data.api.AppError
import com.veltrix.admin.data.api.Paged
import com.veltrix.admin.data.api.toAppError
import kotlinx.coroutines.CancellationException

/** Async state of a screen backed by the API. */
sealed interface Load<out T> {
    data object Loading : Load<Nothing>
    data class Ok<T>(val value: T, val refreshing: Boolean = false) : Load<T>
    data class Failed(val error: AppError) : Load<Nothing>
}

val <T> Load<T>.valueOrNull: T? get() = (this as? Load.Ok)?.value

suspend fun <T> attempt(block: suspend () -> T): Result<T> = try {
    Result.success(block())
} catch (e: CancellationException) {
    throw e
} catch (e: Exception) {
    Result.failure(e)
}

/** Reloads keeping the previous value visible while refreshing (pull-to-refresh). */
suspend fun <T> reload(previous: Load<T>, set: (Load<T>) -> Unit, block: suspend () -> T) {
    set(if (previous is Load.Ok) previous.copy(refreshing = true) else Load.Loading)
    attempt(block).fold({ set(Load.Ok(it)) }, { set(Load.Failed(it.toAppError())) })
}

/** List state with server-side paging. */
data class PageState<T>(
    val items: List<T> = emptyList(),
    val page: Int = 0,
    val total: Int = 0,
    val loading: Boolean = false,
    val refreshing: Boolean = false,
    val error: AppError? = null,
) {
    val endReached: Boolean get() = page > 0 && items.size >= total
    val isInitialLoading: Boolean get() = loading && items.isEmpty()
    val isEmpty: Boolean get() = !loading && error == null && page > 0 && items.isEmpty()
}

/**
 * Accumulates server pages. [fetch] gets (page, pageSize). Duplicate keys across pages (rows shifting while paging)
 * are dropped so lists never show the same entity twice.
 */
class Paginator<T>(
    private val pageSize: Int = 25,
    private val key: (T) -> String,
    private val fetch: suspend (page: Int, pageSize: Int) -> Paged<T>,
) {
    var state: PageState<T> = PageState()
        private set

    suspend fun refresh(onUpdate: (PageState<T>) -> Unit) {
        state = state.copy(loading = true, refreshing = state.items.isNotEmpty(), error = null)
        onUpdate(state)
        attempt { fetch(1, pageSize) }.fold(
            { p -> state = PageState(items = p.items.distinctBy(key), page = 1, total = p.totalCount) },
            { e -> state = state.copy(loading = false, refreshing = false, error = e.toAppError()) },
        )
        onUpdate(state)
    }

    suspend fun loadMore(onUpdate: (PageState<T>) -> Unit) {
        if (state.loading || state.endReached || state.page == 0) return
        state = state.copy(loading = true, error = null)
        onUpdate(state)
        val next = state.page + 1
        attempt { fetch(next, pageSize) }.fold(
            { p ->
                val seen = state.items.mapTo(HashSet(), key)
                val merged = state.items + p.items.filter { seen.add(key(it)) }
                // A short page means the server has nothing more even if totalCount drifted.
                val total = if (p.items.size < pageSize) merged.size else maxOf(p.totalCount, merged.size)
                state = PageState(items = merged, page = next, total = total)
            },
            { e -> state = state.copy(loading = false, error = e.toAppError()) },
        )
        onUpdate(state)
    }
}
