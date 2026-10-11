package com.veltrix.os.data.work

import android.content.Context
import androidx.hilt.work.HiltWorker
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.veltrix.os.data.api.ApiException
import com.veltrix.os.data.api.OfflineException
import com.veltrix.os.data.repo.LicenseRepository
import com.veltrix.os.data.repo.SyncRepository
import dagger.assisted.Assisted
import dagger.assisted.AssistedInject
import java.util.concurrent.TimeUnit

/** Pushes the outbox, pulls master data and renews the license certificate. Retries with exponential backoff. */
@HiltWorker
class SyncWorker @AssistedInject constructor(
    @Assisted context: Context, @Assisted params: WorkerParameters,
    private val sync: SyncRepository, private val license: LicenseRepository,
) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        // Standalone (device-bound): there is no server — nothing to push, pull or renew.
        if (license.standalone) return Result.success()
        return sync()
    }

    private suspend fun sync(): Result = try {
        runCatching { license.refresh() }
        sync.push()
        sync.pull()
        Result.success()
    } catch (e: OfflineException) {
        Result.retry()
    } catch (e: ApiException) {
        if (e.status in 500..599 || e.status == 429) Result.retry() else Result.failure()
    }

    companion object {
        private val constraints = Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()

        fun schedule(context: Context) {
            WorkManager.getInstance(context).enqueueUniquePeriodicWork("veltrix-sync", ExistingPeriodicWorkPolicy.KEEP,
                PeriodicWorkRequestBuilder<SyncWorker>(15, TimeUnit.MINUTES).setConstraints(constraints)
                    .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS).build())
        }

        fun syncNow(context: Context) {
            WorkManager.getInstance(context).enqueueUniqueWork("veltrix-sync-now", ExistingWorkPolicy.REPLACE,
                OneTimeWorkRequestBuilder<SyncWorker>().setConstraints(constraints).setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 15, TimeUnit.SECONDS).build())
        }
    }
}
