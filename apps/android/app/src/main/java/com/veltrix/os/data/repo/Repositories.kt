package com.veltrix.os.data.repo

import com.veltrix.os.BuildConfig
import com.veltrix.os.data.api.*
import com.veltrix.os.data.db.*
import com.veltrix.os.data.security.DeviceIdentity
import com.veltrix.os.data.security.LicenseState
import com.veltrix.os.data.security.LicenseVerifier
import com.veltrix.os.data.security.OfflineActivation
import com.veltrix.os.data.security.SecureStore
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Instant
import java.util.Base64
import java.util.UUID
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.serializer

@Singleton
class SessionRepository @Inject constructor(private val api: ApiClient, private val store: SecureStore, private val settings: AppSettings) {
    private val _profile = MutableStateFlow(loadProfile())
    val profile: StateFlow<UserProfile?> = _profile.asStateFlow()

    private fun loadProfile(): UserProfile? = store.get(SecureStore.PROFILE)?.let { runCatching { api.json.decodeFromString(UserProfile.serializer(), it) }.getOrNull() }

    /** Local account of a standalone (offline-licensed) installation; kept in memory only, so every app start asks to sign in. */
    var localUser: com.veltrix.os.data.standalone.LocalUser? = null
        private set

    fun signInLocal(user: com.veltrix.os.data.standalone.LocalUser) {
        localUser = user
        _profile.value = user.toProfile()
    }

    val isSignedIn: Boolean get() = store.get(SecureStore.REFRESH_TOKEN) != null || _profile.value != null && store.get(SecureStore.OFFLINE_VERIFIER) != null

    /** Online sign-in bound to this device. A salted PBKDF2 verifier is kept so the same user can unlock offline later. */
    suspend fun login(email: String, password: String, mfa: String?): LoginResult {
        val result: LoginResult = api.post("/api/v1/auth/login", LoginRequest(email.trim(), password, mfa, settings.deviceId), auth = false)
        if (result.mfaRequired) return result
        store.put(SecureStore.ACCESS_TOKEN, result.accessToken)
        store.put(SecureStore.REFRESH_TOKEN, result.refreshToken)
        store.put(SecureStore.PROFILE, api.json.encodeToString(UserProfile.serializer(), result.user!!))
        store.put(SecureStore.OFFLINE_VERIFIER, verifier(email.trim().lowercase(), password))
        _profile.value = result.user
        return result
    }

    /** Offline unlock for the last verified user only (no new users can sign in offline). */
    fun offlineLogin(email: String, password: String): Boolean {
        val stored = store.get(SecureStore.OFFLINE_VERIFIER) ?: return false
        val parts = stored.split('$')
        if (parts.size != 3 || _profile.value?.email?.lowercase() != email.trim().lowercase()) return false
        val salt = Base64.getDecoder().decode(parts[1])
        return MessageDigest.isEqual(Base64.getDecoder().decode(parts[2]), pbkdf2(email.trim().lowercase(), password, salt))
    }

    private fun verifier(email: String, password: String): String {
        val salt = ByteArray(16).also { SecureRandom().nextBytes(it) }
        return "v1$" + Base64.getEncoder().encodeToString(salt) + "$" + Base64.getEncoder().encodeToString(pbkdf2(email, password, salt))
    }

    private fun pbkdf2(email: String, password: String, salt: ByteArray): ByteArray =
        SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(PBEKeySpec("$email:$password".toCharArray(), salt, 120_000, 256)).encoded

    suspend fun logout() {
        if (localUser != null) { localUser = null; _profile.value = null; return }
        store.get(SecureStore.REFRESH_TOKEN)?.let { runCatching { api.postNoContent("/api/v1/auth/logout", RefreshRequest(it), auth = false) } }
        store.put(SecureStore.ACCESS_TOKEN, null)
        store.put(SecureStore.REFRESH_TOKEN, null)
        store.put(SecureStore.PROFILE, null)
        store.put(SecureStore.OFFLINE_VERIFIER, null)
        _profile.value = null
    }
}

@Singleton
class LicenseRepository @Inject constructor(private val api: ApiClient, private val store: SecureStore, private val settings: AppSettings,
    private val identity: DeviceIdentity) {
    private val _state = MutableStateFlow<LicenseState>(LicenseState.Missing)
    val state: StateFlow<LicenseState> = _state.asStateFlow()

    val summary: LicenseSummary? get() = settings.licenseSummaryJson?.let { runCatching { api.json.decodeFromString(LicenseSummary.serializer(), it) }.getOrNull() }

    /** Key compiled into the build: explicit CI pin, else licensing/vendor-public-key.pem. Required for offline activation. */
    private val embeddedVendorKey: String get() = BuildConfig.VENDOR_PUBLIC_KEY.ifBlank { BuildConfig.VENDOR_PUBLIC_KEY_SPKI }
    val offlineActivationConfigured: Boolean get() = embeddedVendorKey.isNotBlank()

    /** This device's code for offline activation, grouped for display. */
    val deviceCode: String get() = OfflineActivation.formatDeviceCode(
        OfflineActivation.encodeDeviceCode(identity.installationId, identity.publicKeyThumbprint()))

    /** True when the stored certificate is a vendor-issued offline license: it is never refreshed online. */
    val isOfflineLicense: Boolean get() {
        val cert = store.get(SecureStore.CERTIFICATE) ?: return false
        val key = embeddedVendorKey.ifBlank { return false }
        return LicenseVerifier.decodeAndVerify(cert, key)?.type == OfflineActivation.OFFLINE_TYPE
    }

    enum class OfflineResult { Activated, NotConfigured, Invalid, WrongDevice, Expired }

    /** Server-less activation: verifies an owner-issued code against the embedded key and this device, then stores it like an online activation. */
    fun activateOffline(text: String): OfflineResult {
        val key = embeddedVendorKey.ifBlank { return OfflineResult.NotConfigured }
        val cert = OfflineActivation.extractActivationCode(text) ?: return OfflineResult.Invalid
        val (state, _) = LicenseVerifier.evaluate(cert, key, identity.installationId, identity.publicKeyThumbprint(), Instant.now().epochSecond, 0)
        val c = when (state) {
            is LicenseState.Valid -> state.cert
            is LicenseState.Expired -> return OfflineResult.Expired
            LicenseState.WrongDevice -> return OfflineResult.WrongDevice
            else -> return OfflineResult.Invalid
        }
        if (c.status != "Active") return OfflineResult.Invalid
        store.put(SecureStore.CERTIFICATE, cert)
        settings.deviceId = null // no server seat: never send heartbeats for this license
        settings.tenantId = c.tid
        settings.tenantName = c.tenantName
        settings.setupCompleted = true
        settings.setupToken = null
        val expires = Instant.ofEpochSecond(c.lexp ?: c.exp).toString()
        settings.licenseSummaryJson = api.json.encodeToString(LicenseSummary.serializer(), LicenseSummary(c.lid, "OFFLINE", c.type, c.status, c.status,
            c.plan, c.maxDev, 1, Instant.ofEpochSecond(c.iat).toString(), expires, 0, c.ent,
            ((c.exp - Instant.now().epochSecond + 86399) / 86400).toInt()))
        evaluate()
        return OfflineResult.Activated
    }

    fun evaluate(): LicenseState {
        val watermark = store.get(SecureStore.CLOCK_WATERMARK)?.toLongOrNull() ?: 0L
        val vendorKey = embeddedVendorKey.ifBlank { store.get(SecureStore.VENDOR_KEY) ?: "" }
        val (state, newMark) = LicenseVerifier.evaluate(store.get(SecureStore.CERTIFICATE), vendorKey, identity.installationId,
            identity.publicKeyThumbprint(), Instant.now().epochSecond, watermark)
        if (newMark != watermark) store.put(SecureStore.CLOCK_WATERMARK, newMark.toString())
        _state.value = state
        return state
    }

    private suspend fun challenge(purpose: String): ChallengeResponse =
        api.post("/api/v1/licensing/challenge", ChallengeRequest(identity.installationId, purpose), auth = false)

    suspend fun activate(licenseKey: String): ActivateResponse {
        // Pin the vendor key on first activation unless one is compiled into the build (recommended for production).
        if (embeddedVendorKey.isBlank() && store.get(SecureStore.VENDOR_KEY) == null) {
            val key: PublicKeyResponse = api.get("/api/v1/licensing/public-key", auth = false)
            store.put(SecureStore.VENDOR_KEY, key.spki)
        }
        val ch = challenge("activate")
        val res: ActivateResponse = api.post("/api/v1/licensing/activate", ActivateRequest(licenseKey.trim(), identity.installationId, "android",
            identity.deviceName, BuildConfig.VERSION_NAME, identity.publicKeySpki(), identity.fingerprintHash(), DeviceIdentity.FINGERPRINT_VERSION,
            ch.challengeId, identity.signChallenge(ch.nonce, "activate")), auth = false)
        store.put(SecureStore.CERTIFICATE, res.certificate)
        settings.deviceId = res.deviceId
        settings.tenantId = res.tenantId
        settings.tenantName = res.tenantName
        settings.setupCompleted = res.setupCompleted
        settings.setupToken = res.setupToken
        settings.licenseSummaryJson = api.json.encodeToString(LicenseSummary.serializer(), res.license)
        evaluate()
        return res
    }

    /** Heartbeat: renews the offline certificate and learns about revocation/renewal. */
    suspend fun refresh(): LicenseState {
        // Offline (vendor-issued) licenses are valid until their expiry without any server contact.
        if (isOfflineLicense) return evaluate()
        val deviceId = settings.deviceId ?: return evaluate()
        try {
            val ch = challenge("refresh")
            val res: RefreshCertificateResponse = api.post("/api/v1/licensing/refresh", RefreshCertificateRequest(deviceId, identity.installationId,
                BuildConfig.VERSION_NAME, ch.challengeId, identity.signChallenge(ch.nonce, "refresh")), auth = false)
            store.put(SecureStore.CERTIFICATE, res.certificate)
            settings.licenseSummaryJson = api.json.encodeToString(LicenseSummary.serializer(), res.license)
        } catch (e: ApiException) {
            if (e.code == "licensing.device_revoked" || e.code == "licensing.device_unknown") {
                store.put(SecureStore.CERTIFICATE, null)
                _state.value = LicenseState.WrongDevice
                return _state.value
            }
            if (e.code == "licensing.expired") { evaluate(); return _state.value }
            throw e
        }
        return evaluate()
    }

    suspend fun release() {
        val deviceId = settings.deviceId ?: return
        val ch = challenge("release")
        api.postNoContent("/api/v1/licensing/release", ReleaseDeviceRequest(deviceId, identity.installationId, ch.challengeId, identity.signChallenge(ch.nonce, "release")), auth = false)
        store.put(SecureStore.CERTIFICATE, null)
        settings.deviceId = null
        identity.resetKeys()
        evaluate()
    }

    /** Offline (vendor-issued) license → standalone mode: local accounts and data only, no server and no sync. */
    val standalone: Boolean get() = when (val s = state.value) {
        is LicenseState.Valid -> com.veltrix.os.data.standalone.ModePolicy.isStandalone(s.cert.type)
        is LicenseState.Expired -> com.veltrix.os.data.standalone.ModePolicy.isStandalone(s.cert.type)
        else -> false
    }

    fun hasFeature(f: String): Boolean = (state.value as? LicenseState.Valid)?.cert?.ent?.contains(f) == true
}

@Singleton
class SyncRepository @Inject constructor(private val api: ApiClient, private val db: VeltrixDatabase, private val settings: AppSettings) {
    val pendingCount = db.outbox().pendingCount()
    val attentionCount = db.outbox().attentionCount()
    val needingAttention = db.outbox().needingAttention()

    suspend fun <T> enqueue(type: String, payload: T, ser: kotlinx.serialization.KSerializer<T>, opId: String = UUID.randomUUID().toString(), baseVersion: String? = null): String {
        db.outbox().insert(OutboxEntity(opId, type, api.json.encodeToString(ser, payload), Instant.now().toString(), baseVersion, seq = db.outbox().nextSeq()))
        return opId
    }

    /** Push pending operations in order. Returns number applied; transient failures keep ops pending for retry with backoff. */
    suspend fun push(): Int {
        var applied = 0
        while (true) {
            val batch = db.outbox().pending(100)
            if (batch.isEmpty()) break
            val req = SyncPushRequest(batch.map { SyncOperation(it.opId, it.type, api.json.parseToJsonElement(it.payload), it.clientTime, it.baseVersion) })
            val res: SyncPushResponse = api.post("/api/v1/sync/push", req)
            var progressed = false
            for (r in res.results) {
                when (r.status) {
                    "applied", "duplicate" -> { db.outbox().mark(r.opId, OutboxEntity.STATUS_SYNCED, null); db.sales().markSynced(r.opId); applied++; progressed = true }
                    "conflict" -> { db.outbox().mark(r.opId, OutboxEntity.STATUS_CONFLICT, r.message); progressed = true }
                    "rejected" -> { db.outbox().mark(r.opId, OutboxEntity.STATUS_REJECTED, "${r.code}: ${r.message}"); progressed = true }
                    else -> db.outbox().retryLater(r.opId, r.message)
                }
            }
            if (!progressed) break
        }
        db.outbox().purgeSynced(System.currentTimeMillis() - 30L * 24 * 3600 * 1000)
        return applied
    }

    /** Incremental pull of server-authoritative data (catalog, customers, stock, registers). */
    suspend fun pull() {
        do {
            val since = settings.syncCursor?.let { "&since=" + java.net.URLEncoder.encode(it, "UTF-8") } ?: ""
            val wh = settings.warehouseId?.let { "&warehouseId=$it" } ?: ""
            val res: SyncPullResponse = api.get("/api/v1/sync/pull?limit=1000$since$wh")
            db.catalog().upsertProducts(res.products.map { ProductEntity(it.id, it.sku, it.name, it.nameAr, it.type, it.status, it.categoryId, it.taxRatePercent,
                it.salePrice, it.trackStock, it.barcodes.joinToString(","), it.updatedAt) })
            db.catalog().upsertCustomers(res.customers.map { CustomerEntity(it.id, it.code, it.name, it.phone, it.balance, it.loyaltyPoints, it.creditLimit, it.updatedAt) })
            db.catalog().upsertStock(res.stockBalances.map { StockEntity(it.productId, it.warehouseId, it.quantity, it.available) })
            db.catalog().upsertRegisters(res.registers.map { RegisterEntity(it.id, it.branchId, it.warehouseId, it.code, it.name, it.openShiftId) })
            settings.syncCursor = res.cursor
            if (settings.warehouseId == null) res.registers.firstOrNull()?.let { settings.warehouseId = it.warehouseId; settings.registerId = settings.registerId ?: it.id }
        } while (res.hasMore)
        settings.lastSyncAt = System.currentTimeMillis()
    }
}

data class CartLine(val product: ProductEntity, val quantity: Double, val discountPercent: Double = 0.0) {
    val gross get() = product.salePrice * quantity
    val discount get() = gross * discountPercent / 100.0
    val total get() = gross - discount
}

@Singleton
class PosRepository @Inject constructor(private val db: VeltrixDatabase, private val sync: SyncRepository, private val settings: AppSettings, private val api: ApiClient,
    private val license: LicenseRepository, private val local: StandaloneRepository, private val session: SessionRepository) {
    /**
     * Records a sale locally (atomic: stock decrement, receipt, outbox op) — the server is authoritative for prices, tax and
     * accounting when the op syncs. Offline stock policy: the local available quantity must cover the sale unless the
     * company allows negative stock; conflicts beyond that are flagged by the server, never silently dropped.
     */
    suspend fun completeSale(lines: List<CartLine>, payments: List<PaymentInput>, customerId: String?, allowNegative: Boolean): LocalSaleEntity {
        // Standalone: recorded locally with stock decrement and sale lines; never queued for a server.
        if (license.standalone) return local.completeSale(lines, payments, session.profile.value?.fullName)
        val registerId = settings.registerId ?: error("Select a register first.")
        val shiftId = settings.shiftId ?: error("Open a shift first.")
        val warehouseId = settings.warehouseId ?: error("No warehouse configured.")
        if (!allowNegative) for (l in lines.filter { it.product.trackStock }) {
            val s = db.catalog().stockOf(l.product.id, warehouseId)
            if ((s?.available ?: 0.0) < l.quantity) error("Insufficient local stock for ${l.product.name}.")
        }
        val id = UUID.randomUUID().toString()
        val counter = settings.receiptCounter + 1
        settings.receiptCounter = counter
        val number = "M-${settings.deviceId?.take(4) ?: "LOCAL"}-$counter"
        val req = CompleteSaleRequest(id, registerId, shiftId, customerId, lines.map { SaleLineInput(it.product.id, it.quantity, null, it.discountPercent) },
            payments, id = id, clientCreatedAt = Instant.now().toString(), offline = true, clientNumber = number)
        val payload = api.json.encodeToString(CompleteSaleRequest.serializer(), req)
        val sale = LocalSaleEntity(id, number, lines.sumOf { it.total }, lines.size, System.currentTimeMillis(), payload)
        db.posLocal().recordSale(sale, OutboxEntity(id, "pos.sale", payload, req.clientCreatedAt!!, null, seq = db.outbox().nextSeq()),
            lines.filter { it.product.trackStock }.map { it.product.id to it.quantity }, warehouseId)
        return sale
    }

    suspend fun openShift(openingFloat: Double): String {
        if (license.standalone) local.ensureRegister()
        val registerId = settings.registerId ?: error("Select a register first.")
        val id = UUID.randomUUID().toString()
        if (license.standalone) { settings.shiftId = id; return id }
        sync.enqueue("pos.shift.open", OpenShiftRequest(registerId, openingFloat, id, Instant.now().toString()), OpenShiftRequest.serializer(), "shift-open-$id")
        settings.shiftId = id
        return id
    }

    suspend fun closeShift(counted: Double, notes: String?) {
        val shiftId = settings.shiftId ?: return
        if (license.standalone) { settings.shiftId = null; return }
        sync.enqueue("pos.shift.close", ShiftPayload(shiftId, CloseShiftRequest(counted, notes, Instant.now().toString())),
            ShiftPayload.serializer(CloseShiftRequest.serializer()), "shift-close-$shiftId")
        settings.shiftId = null
    }
}

/** Online-only views (reports, notifications, documents). They fail with OfflineException when disconnected. */
@Singleton
class RemoteRepository @Inject constructor(private val api: ApiClient) {
    suspend fun dashboard(): Dashboard = api.get("/api/v1/reports/dashboard")
    suspend fun reports(): List<ReportInfo> = api.get("/api/v1/reports")
    suspend fun report(key: String, from: String, to: String): ReportTable = api.get("/api/v1/reports/run/$key?from=$from&to=$to")
    suspend fun notifications(): List<AppNotification> = api.get("/api/v1/notifications?take=100")
    suspend fun markAllRead() = api.postNoContent("/api/v1/notifications/read", emptyList<String>())
    suspend fun employees(): Paged<Employee> = api.get("/api/v1/hr/employees?pageSize=200")
    suspend fun checkIn(employeeId: String, inOut: Boolean) =
        api.post<CheckRequest, JsonElement>(if (inOut) "/api/v1/hr/attendance/check-in" else "/api/v1/hr/attendance/check-out", CheckRequest(employeeId, "mobile"))
    suspend fun salesDocuments(): Paged<SalesDocument> = api.get("/api/v1/sales/documents?pageSize=100")
    suspend fun expenses(): Paged<Expense> = api.get("/api/v1/expenses?pageSize=100")
    suspend fun accounts(): List<Account> = api.get("/api/v1/accounting/accounts")
    suspend fun branches(): List<Branch> = api.get("/api/v1/branches")
    suspend fun stock(warehouseId: String?): Paged<StockBalance> = api.get("/api/v1/inventory/balances?pageSize=500" + (warehouseId?.let { "&warehouseId=$it" } ?: ""))
    suspend fun completeOnboarding(req: CompleteOnboardingRequest): OnboardingResult = api.post("/api/v1/onboarding/complete", req, auth = false)
    suspend fun createCustomer(req: UpsertCustomerRequest): Customer = api.post("/api/v1/customers", req)
}
