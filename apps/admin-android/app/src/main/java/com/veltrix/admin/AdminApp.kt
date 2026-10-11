package com.veltrix.admin

import android.app.Application
import dagger.hilt.android.HiltAndroidApp

@HiltAndroidApp
class AdminApp : Application() {
    override fun onCreate() {
        super.onCreate()
        // Leftover from the former server-connected console (encrypted session tokens): nothing uses it any more.
        runCatching { deleteSharedPreferences("veltrix_admin_vault") }
    }
}
