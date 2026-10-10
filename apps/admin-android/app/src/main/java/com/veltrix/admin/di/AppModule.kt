package com.veltrix.admin.di

import android.content.Context
import com.veltrix.admin.data.security.KeystoreTokenCipher
import com.veltrix.admin.data.security.SessionLockPolicy
import com.veltrix.admin.data.security.TokenVault
import com.veltrix.admin.data.settings.SharedPrefsKeyValueStore
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
object AppModule {
    /** Session secrets: AES-256-GCM with a non-exportable Android Keystore key; only ciphertext touches disk. */
    @Provides
    @Singleton
    fun tokenVault(@ApplicationContext context: Context): TokenVault =
        TokenVault(SharedPrefsKeyValueStore(context, "veltrix_admin_vault"), KeystoreTokenCipher())

    @Provides
    @Singleton
    fun lockPolicy(): SessionLockPolicy = SessionLockPolicy()
}
