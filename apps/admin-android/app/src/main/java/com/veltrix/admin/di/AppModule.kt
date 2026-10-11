package com.veltrix.admin.di

import com.veltrix.admin.data.security.SessionLockPolicy
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
object AppModule {
    @Provides
    @Singleton
    fun lockPolicy(): SessionLockPolicy = SessionLockPolicy()
}
