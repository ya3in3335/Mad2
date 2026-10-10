package com.veltrix.admin

import com.veltrix.admin.data.security.SessionLockPolicy
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionLockPolicyTest {
    private var now = 0L
    private val policy = SessionLockPolicy(graceMillis = 15_000, clock = { now })

    @Test fun locksAfterGracePeriod() {
        policy.onBackground(); now += 15_000
        assertTrue(policy.onForeground(biometricEnabled = true, signedIn = true))
    }

    @Test fun doesNotLockWithinGracePeriod() {
        policy.onBackground(); now += 14_999
        assertFalse(policy.onForeground(biometricEnabled = true, signedIn = true))
    }

    @Test fun neverLocksWhenDisabledOrSignedOut() {
        policy.onBackground(); now += 60_000
        assertFalse(policy.onForeground(biometricEnabled = false, signedIn = true))
        policy.onBackground(); now += 60_000
        assertFalse(policy.onForeground(biometricEnabled = true, signedIn = false))
    }

    @Test fun systemAuthPromptDoesNotRelock() {
        policy.authenticating = true
        policy.onBackground(); now += 60_000
        policy.authenticating = false
        assertFalse(policy.onForeground(biometricEnabled = true, signedIn = true))
    }

    @Test fun coldStartLocksOnlyWithStoredSession() {
        assertTrue(policy.lockOnColdStart(biometricEnabled = true, signedIn = true))
        assertFalse(policy.lockOnColdStart(biometricEnabled = true, signedIn = false))
    }
}
