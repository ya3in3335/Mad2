package com.veltrix.admin.ui.auth

import android.content.Context
import android.os.Build
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricManager.Authenticators.BIOMETRIC_STRONG
import androidx.biometric.BiometricManager.Authenticators.BIOMETRIC_WEAK
import androidx.biometric.BiometricManager.Authenticators.DEVICE_CREDENTIAL
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity

/** BiometricPrompt wrapper used for app unlock and for confirming security-setting changes. */
object Biometrics {
    /**
     * BIOMETRIC_STRONG | DEVICE_CREDENTIAL on Android 11+. That combination is not supported by the framework on
     * Android 9–10, where the closest equivalent (BIOMETRIC_WEAK | DEVICE_CREDENTIAL) is used instead.
     */
    fun authenticators(sdk: Int = Build.VERSION.SDK_INT): Int =
        if (sdk >= Build.VERSION_CODES.R) BIOMETRIC_STRONG or DEVICE_CREDENTIAL else BIOMETRIC_WEAK or DEVICE_CREDENTIAL

    fun available(context: Context): Boolean =
        BiometricManager.from(context).canAuthenticate(authenticators()) == BiometricManager.BIOMETRIC_SUCCESS

    fun prompt(
        activity: FragmentActivity,
        title: String,
        subtitle: String?,
        onSuccess: () -> Unit,
        onFailure: (code: Int, message: CharSequence) -> Unit = { _, _ -> },
    ) {
        val prompt = BiometricPrompt(activity, ContextCompat.getMainExecutor(activity), object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) = onSuccess()
            override fun onAuthenticationError(errorCode: Int, errString: CharSequence) = onFailure(errorCode, errString)
        })
        val info = BiometricPrompt.PromptInfo.Builder().setTitle(title).apply { if (subtitle != null) setSubtitle(subtitle) }
            .setAllowedAuthenticators(authenticators())
            .setConfirmationRequired(false)
            .build()
        prompt.authenticate(info)
    }
}
