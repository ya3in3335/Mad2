package com.veltrix.os.di

import android.content.Context
import androidx.room.Room
import com.veltrix.os.data.db.VeltrixDatabase
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
object AppModule {
    @Provides
    @Singleton
    fun database(@ApplicationContext context: Context): VeltrixDatabase =
        Room.databaseBuilder(context, VeltrixDatabase::class.java, "veltrix.db").build()
}
