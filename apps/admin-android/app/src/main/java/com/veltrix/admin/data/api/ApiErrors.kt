package com.veltrix.admin.data.api

import java.io.IOException

/** HTTP error returned by the API (RFC 7807 problem details). */
class ApiException(val status: Int, val code: String?, override val message: String, val correlationId: String? = null) : Exception(message)

/** The server could not be reached (DNS, TLS, timeout, no network). */
class OfflineException(cause: Throwable) : IOException("Server unreachable", cause)

/** The session can no longer be refreshed; the user must sign in again. */
class SessionExpiredException : IOException("Session expired")

/** UI-facing error categories; each maps to one localized message. */
sealed interface AppError {
    data object Offline : AppError
    data object SessionExpired : AppError
    /** 403 — the account lacks the required platform permission (or the tenant/action is forbidden). */
    data object Forbidden : AppError
    /** 403 with auth.step_up_required — the password confirmation was missing or wrong. */
    data object StepUpFailed : AppError
    data object NotFound : AppError
    /** 404/405 on an endpoint the connected server does not provide (older backend). */
    data object NotSupported : AppError
    data object RateLimited : AppError
    data object InvalidCredentials : AppError
    data object InvalidMfa : AppError
    data object LockedOut : AppError
    data class Rejected(val message: String) : AppError
    data class Server(val status: Int, val correlationId: String?) : AppError
    data class Unknown(val message: String?) : AppError
}

fun Throwable.toAppError(): AppError = when (this) {
    is SessionExpiredException -> AppError.SessionExpired
    is OfflineException -> AppError.Offline
    is ApiException -> when {
        code == "auth.step_up_required" -> AppError.StepUpFailed
        code == "auth.invalid_credentials" -> AppError.InvalidCredentials
        code == "auth.invalid_mfa" -> AppError.InvalidMfa
        code == "auth.locked_out" || status == 423 -> AppError.LockedOut
        status == 401 -> AppError.SessionExpired
        status == 403 -> AppError.Forbidden
        status == 404 && code == null -> AppError.NotSupported
        status == 404 -> AppError.NotFound
        status == 405 -> AppError.NotSupported
        status == 429 -> AppError.RateLimited
        status >= 500 -> AppError.Server(status, correlationId)
        else -> AppError.Rejected(message)
    }
    is IOException -> AppError.Offline
    else -> AppError.Unknown(message)
}
