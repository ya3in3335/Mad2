package com.veltrix.admin.data.security

/**
 * Decides when the biometric / device-credential gate must be shown. Pure logic (clock injected) so it can be tested.
 *
 * - Cold start with a stored session locks when biometric unlock is enabled.
 * - Returning from background locks once the app spent at least [graceMillis] in background.
 * - Transitions caused by the system authentication UI itself (device-credential screen) never re-lock.
 */
class SessionLockPolicy(private val graceMillis: Long = DEFAULT_GRACE_MS, private val clock: () -> Long = System::currentTimeMillis) {
    private var backgroundedAt: Long? = null
    @Volatile var authenticating: Boolean = false

    fun onBackground() {
        if (!authenticating) backgroundedAt = clock()
    }

    /** Returns true when the gate must be shown now. */
    fun onForeground(biometricEnabled: Boolean, signedIn: Boolean): Boolean {
        val since = backgroundedAt
        backgroundedAt = null
        if (!biometricEnabled || !signedIn || authenticating || since == null) return false
        return clock() - since >= graceMillis
    }

    fun lockOnColdStart(biometricEnabled: Boolean, signedIn: Boolean): Boolean = biometricEnabled && signedIn

    companion object {
        const val DEFAULT_GRACE_MS = 15_000L
    }
}
