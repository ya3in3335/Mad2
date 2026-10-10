package com.veltrix.admin.ui.releases

import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Add
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.veltrix.admin.R
import com.veltrix.admin.data.api.AppRelease
import com.veltrix.admin.data.api.UpsertReleaseRequest
import com.veltrix.admin.data.api.toAppError
import com.veltrix.admin.data.repo.AdminRepository
import com.veltrix.admin.ui.common.*
import com.veltrix.admin.ui.licenses.FlowRowChips
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Release form rules (pure, unit-tested). */
object ReleaseForm {
    enum class Problem { Version, MinVersion, Url, Sha }
    private val VERSION = Regex("""^\d+\.\d+\.\d+([-.+][0-9A-Za-z.\-]+)?$""")
    private val SHA256 = Regex("^[0-9a-fA-F]{64}$")

    fun validate(version: String, minVersion: String, url: String, sha: String): Problem? = when {
        !VERSION.matches(version.trim()) -> Problem.Version
        minVersion.isNotBlank() && !VERSION.matches(minVersion.trim()) -> Problem.MinVersion
        url.isNotBlank() && !url.trim().startsWith("https://") -> Problem.Url
        sha.isNotBlank() && !SHA256.matches(sha.trim()) -> Problem.Sha
        else -> null
    }
}

@HiltViewModel
class ReleasesViewModel @Inject constructor(private val repo: AdminRepository) : ViewModel() {
    private val _state = MutableStateFlow<Load<List<AppRelease>>>(Load.Loading)
    val state = _state.asStateFlow()
    val action = MutableStateFlow<ActionState>(ActionState.Idle)
    val messages = MutableSharedFlow<Int>(extraBufferCapacity = 4)

    init { refresh() }

    fun refresh() = viewModelScope.launch {
        reload(_state.value, { _state.value = it }) { repo.releases().sortedByDescending { Formatters.parseInstant(it.publishedAt) } }
    }
    fun resetAction() { action.value = ActionState.Idle }

    fun save(existing: AppRelease?, r: UpsertReleaseRequest, onDone: () -> Unit) = viewModelScope.launch {
        action.value = ActionState.Busy
        attempt { if (existing == null) repo.createRelease(r) else repo.updateRelease(existing.id, r) }.fold(
            { action.value = ActionState.Idle; messages.tryEmit(R.string.msg_release_saved); onDone(); refresh() },
            { action.value = ActionState.Failed(it.toAppError()) },
        )
    }
}

@Composable
fun ReleasesScreen(vm: ReleasesViewModel = hiltViewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    var editing by remember { mutableStateOf<AppRelease?>(null) }
    var creating by remember { mutableStateOf(false) }
    CollectMessages(vm.messages)
    Box(Modifier.fillMaxSize()) {
        LoadScreen(state, onRefresh = { vm.refresh() }) { list ->
            if (list.isEmpty()) item { EmptyState() }
            list.groupBy { it.platform.lowercase() }.forEach { (platform, items) ->
                item(key = "h-$platform") { SectionHeader(stringResource(platformLabel(platform))) }
                items.forEach { r ->
                    item(key = r.id) {
                        RowCard("${r.version} · ${r.channel}", stringResource(R.string.release_min, r.minSupportedVersion) + (r.releaseNotes?.let { " · $it" } ?: ""),
                            status = if (r.isActive) "active" else "void", meta = Formatters.date(r.publishedAt),
                            onClick = { vm.resetAction(); editing = r })
                    }
                }
            }
        }
        ExtendedFloatingActionButton(onClick = { vm.resetAction(); creating = true }, icon = { Icon(Icons.Outlined.Add, null) },
            text = { Text(stringResource(R.string.release_new)) }, modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp))
    }
    if (creating || editing != null) ReleaseDialog(editing, vm) { creating = false; editing = null }
}

@Composable
private fun ReleaseDialog(existing: AppRelease?, vm: ReleasesViewModel, onDismiss: () -> Unit) {
    val action by vm.action.collectAsStateWithLifecycle()
    var platform by remember { mutableStateOf(existing?.platform ?: "android") }
    var version by remember { mutableStateOf(existing?.version.orEmpty()) }
    var channel by remember { mutableStateOf(existing?.channel ?: "stable") }
    var minVersion by remember { mutableStateOf(existing?.minSupportedVersion.orEmpty()) }
    var notes by remember { mutableStateOf(existing?.releaseNotes.orEmpty()) }
    var url by remember { mutableStateOf(existing?.downloadUrl.orEmpty()) }
    var sha by remember { mutableStateOf(existing?.sha256.orEmpty()) }
    var active by remember { mutableStateOf(existing?.isActive ?: true) }
    val problem = ReleaseForm.validate(version, minVersion, url, sha)

    FormDialog(stringResource(if (existing == null) R.string.release_new else R.string.release_edit), action, onDismiss, confirmEnabled = problem == null,
        onConfirm = {
            vm.save(existing, UpsertReleaseRequest(platform, version.trim(), channel, minVersion.trim().ifBlank { version.trim() }, notes.ifBlank { null },
                url.trim().ifBlank { null }, sha.trim().lowercase().ifBlank { null }, active), onDismiss)
        }, confirmLabel = stringResource(R.string.action_save)) {
        FlowRowChips(listOf("android" to stringResource(R.string.platform_android), "windows" to stringResource(R.string.platform_windows)), platform) { platform = it }
        FlowRowChips(listOf("stable" to "stable", "beta" to "beta"), channel) { channel = it }
        OutlinedTextField(version, { version = it.take(40) }, label = { Text(stringResource(R.string.field_version)) }, singleLine = true, modifier = Modifier.fillMaxWidth(),
            isError = problem == ReleaseForm.Problem.Version && version.isNotEmpty())
        OutlinedTextField(minVersion, { minVersion = it.take(40) }, label = { Text(stringResource(R.string.field_min_version)) }, singleLine = true,
            modifier = Modifier.fillMaxWidth(), isError = problem == ReleaseForm.Problem.MinVersion)
        OutlinedTextField(url, { url = it.take(500) }, label = { Text(stringResource(R.string.field_download_url)) }, singleLine = true,
            modifier = Modifier.fillMaxWidth(), isError = problem == ReleaseForm.Problem.Url)
        OutlinedTextField(sha, { sha = it.take(64) }, label = { Text("SHA-256") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
            isError = problem == ReleaseForm.Problem.Sha)
        OutlinedTextField(notes, { notes = it.take(2000) }, label = { Text(stringResource(R.string.field_notes)) }, modifier = Modifier.fillMaxWidth())
        Row(verticalAlignment = Alignment.CenterVertically) {
            Switch(active, { active = it }); Spacer(Modifier.width(8.dp)); Text(stringResource(R.string.release_active))
        }
    }
}
