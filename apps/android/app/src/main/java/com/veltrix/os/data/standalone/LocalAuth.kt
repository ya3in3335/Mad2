package com.veltrix.os.data.standalone

import com.veltrix.os.data.api.UserProfile
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import java.util.UUID
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.builtins.MapSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.json.Json

/** Minimal encrypted key/value contract (implemented by [com.veltrix.os.data.security.SecureStore]; in-memory in tests). */
interface SecretStore {
    fun get(name: String): String?
    fun put(name: String, value: String?)
}

object LocalRoles {
    const val OWNER = "owner"
    const val CASHIER = "cashier"
    /** Supervisor: cashier rights + workforce management (employees, shifts, attendance, leave, reports). */
    const val SUPERVISOR = "supervisor"
    val ALL = listOf(OWNER, SUPERVISOR, CASHIER)
    val CASHIER_PERMISSIONS = listOf("pos.sell", "catalog.view", "customers.view", "customers.manage", "sales.view", "inventory.view", WorkforcePermissions.KIOSK)
    val SUPERVISOR_PERMISSIONS = CASHIER_PERMISSIONS + WorkforcePermissions.SUPERVISOR

    fun permissionsFor(role: String): List<String> = when (role) {
        OWNER -> listOf("*")
        SUPERVISOR -> SUPERVISOR_PERMISSIONS
        else -> CASHIER_PERMISSIONS
    }
}

/** Workforce permissions for local roles: owner + supervisor manage, cashier can only use the attendance kiosk. */
object WorkforcePermissions {
    const val KIOSK = "workforce.kiosk"
    const val VIEW = "workforce.view"
    const val MANAGE = "workforce.manage"
    const val MANUAL_PUNCH = "workforce.punch.manual"
    const val CORRECTIONS_REQUEST = "workforce.corrections.request"
    /** Owner only (as on Windows, supervisors cannot approve attendance corrections). */
    const val CORRECTIONS_APPROVE = "workforce.corrections.approve"
    const val LEAVE_APPROVE = "workforce.leave.approve"
    const val REPORTS = "workforce.reports"
    val SUPERVISOR = listOf(VIEW, MANAGE, MANUAL_PUNCH, CORRECTIONS_REQUEST, LEAVE_APPROVE, REPORTS)

    fun has(user: LocalUser?, permission: String): Boolean = user != null && user.active &&
        LocalRoles.permissionsFor(user.role).let { "*" in it || permission in it }
}

/** A user of a standalone (offline-licensed) installation. Only a salted PBKDF2 hash of the secret is kept. */
@Serializable
data class LocalUser(val id: String, val name: String, val role: String, val secretHash: String, val createdAt: Long, val active: Boolean = true) {
    val isOwner: Boolean get() = role == LocalRoles.OWNER
    fun toProfile() = UserProfile(id, name, name, null, false, listOf(role), LocalRoles.permissionsFor(role))
}

/** Salted PBKDF2-HMAC-SHA256: `pbkdf2-sha256$iterations$salt(b64)$hash(b64)`. */
object PasswordHasher {
    const val DEFAULT_ITERATIONS = 210_000
    const val MIN_ITERATIONS = 10_000
    private const val PREFIX = "pbkdf2-sha256"

    fun hash(secret: String, iterations: Int = DEFAULT_ITERATIONS): String {
        val salt = ByteArray(16).also { SecureRandom().nextBytes(it) }
        val b64 = Base64.getEncoder()
        return listOf(PREFIX, iterations.toString(), b64.encodeToString(salt), b64.encodeToString(derive(secret, salt, iterations))).joinToString("$")
    }

    fun verify(secret: String, stored: String): Boolean {
        val p = stored.split('$')
        val it = p.getOrNull(1)?.toIntOrNull() ?: return false
        if (p.size != 4 || p[0] != PREFIX || it < MIN_ITERATIONS) return false
        return try {
            MessageDigest.isEqual(derive(secret, Base64.getDecoder().decode(p[2]), it), Base64.getDecoder().decode(p[3]))
        } catch (e: IllegalArgumentException) { false }
    }

    fun derive(secret: String, salt: ByteArray, iterations: Int, bits: Int = 256): ByteArray {
        val spec = PBEKeySpec(secret.toCharArray(), salt, iterations, bits)
        try { return SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded } finally { spec.clearPassword() }
    }
}

enum class LocalSignInStatus { SignedIn, Invalid, LockedOut, NoUsers }
data class LocalSignInResult(val status: LocalSignInStatus, val user: LocalUser? = null, val retryAfterMs: Long? = null)

/** Thrown with a string-resource-like key the UI maps to a localized message. */
class LocalAuthException(val key: String) : IllegalStateException(key)

/**
 * Local accounts for standalone mode: the owner created at first run plus optional cashiers. Users, hashes and failed-attempt
 * counters are stored in the encrypted store. After [MAX_FAILURES] consecutive failures a name is locked with exponential
 * backoff (30 s, 60 s, 120 s … capped at 15 min); the counters survive restarts. There is no default account.
 */
class LocalAuth(private val store: SecretStore, private val clock: () -> Long = System::currentTimeMillis,
    private val iterations: Int = PasswordHasher.DEFAULT_ITERATIONS) {
    @Serializable private data class Failure(val count: Int, val lockedUntil: Long? = null)

    private val json = Json { ignoreUnknownKeys = true }
    private val usersSer = ListSerializer(LocalUser.serializer())
    private val failSer = MapSerializer(String.serializer(), Failure.serializer())
    private val dummy by lazy { PasswordHasher.hash(UUID.randomUUID().toString(), iterations) }

    val users: List<LocalUser> get() = load()
    val hasOwner: Boolean get() = load().any { it.isOwner && it.active }

    @Synchronized fun createOwner(name: String, secret: String): LocalUser {
        if (hasOwner) throw LocalAuthException("owner_exists")
        return add(name, secret, LocalRoles.OWNER)
    }

    @Synchronized fun addUser(name: String, secret: String, role: String): LocalUser {
        require(role in LocalRoles.ALL) { "Unknown role" }
        if (!hasOwner) throw LocalAuthException("owner_missing")
        return add(name, secret, role)
    }

    private fun add(name: String, secret: String, role: String): LocalUser {
        validateName(name)?.let { throw LocalAuthException(it) }
        validateSecret(secret)?.let { throw LocalAuthException(it) }
        val list = load().toMutableList()
        if (list.any { same(it.name, name) }) throw LocalAuthException("name_taken")
        val u = LocalUser(UUID.randomUUID().toString(), name.trim(), role, PasswordHasher.hash(secret, iterations), clock())
        list += u
        save(list)
        return u
    }

    @Synchronized fun removeUser(id: String) {
        val list = load().toMutableList()
        val u = list.firstOrNull { it.id == id } ?: return
        if (u.isOwner && list.count { it.isOwner && it.active } <= 1) throw LocalAuthException("last_owner")
        list.remove(u)
        save(list)
    }

    /** Checks a secret without touching the lockout counters (owner confirmation before a backup). */
    /** Owner/supervisor authorization for a manual punch: the named account must hold [permission] and the secret must match. */
    fun authorize(name: String, secret: String, permission: String): LocalUser? =
        load().firstOrNull { it.active && same(it.name, name) }?.takeIf { WorkforcePermissions.has(it, permission) && PasswordHasher.verify(secret, it.secretHash) }

    fun verify(id: String, secret: String): Boolean = load().firstOrNull { it.id == id }?.let { PasswordHasher.verify(secret, it.secretHash) } == true

    @Synchronized fun signIn(name: String, secret: String): LocalSignInResult {
        val list = load()
        if (list.isEmpty()) return LocalSignInResult(LocalSignInStatus.NoUsers)
        val key = normalize(name)
        val failures = loadFailures().toMutableMap()
        val now = clock()
        val f = failures[key]
        if (f?.lockedUntil != null && f.lockedUntil > now) return LocalSignInResult(LocalSignInStatus.LockedOut, retryAfterMs = f.lockedUntil - now)
        val user = list.firstOrNull { it.active && same(it.name, name) }
        // Always run the KDF so an unknown name cannot be detected by timing.
        val ok = PasswordHasher.verify(secret, user?.secretHash ?: dummy)
        if (user != null && ok) {
            if (failures.remove(key) != null) saveFailures(failures)
            return LocalSignInResult(LocalSignInStatus.SignedIn, user)
        }
        val count = (f?.count ?: 0) + 1
        val locked = if (count >= MAX_FAILURES) now + minOf(BASE_LOCKOUT_MS shl minOf(count - MAX_FAILURES, 10), MAX_LOCKOUT_MS) else null
        failures[key] = Failure(count, locked)
        saveFailures(failures)
        return if (locked != null) LocalSignInResult(LocalSignInStatus.LockedOut, retryAfterMs = locked - now) else LocalSignInResult(LocalSignInStatus.Invalid)
    }

    fun exportUsers(): String = store.get(USERS_KEY) ?: "[]"

    @Synchronized fun importUsers(text: String) {
        val list = json.decodeFromString(usersSer, text)
        if (list.none { it.isOwner }) throw LocalAuthException("backup_file")
        save(list)
        store.put(LOCKOUT_KEY, null)
    }

    private fun load(): List<LocalUser> = store.get(USERS_KEY)?.let { runCatching { json.decodeFromString(usersSer, it) }.getOrNull() } ?: emptyList()
    private fun save(list: List<LocalUser>) = store.put(USERS_KEY, json.encodeToString(usersSer, list))
    private fun loadFailures(): Map<String, Failure> = store.get(LOCKOUT_KEY)?.let { runCatching { json.decodeFromString(failSer, it) }.getOrNull() } ?: emptyMap()
    private fun saveFailures(m: Map<String, Failure>) = store.put(LOCKOUT_KEY, json.encodeToString(failSer, m))

    companion object {
        const val USERS_KEY = "standalone_users"
        const val LOCKOUT_KEY = "standalone_lockout"
        const val MAX_FAILURES = 5
        const val BASE_LOCKOUT_MS = 30_000L
        const val MAX_LOCKOUT_MS = 15 * 60_000L

        /** Password: ≥ 8 characters. PIN (digits only): ≥ 6 digits. Returns an error key or null. */
        fun validateSecret(secret: String): String? = when {
            secret.isEmpty() -> "secret_required"
            secret.all { it in '0'..'9' } -> if (secret.length >= 6) null else "pin_short"
            else -> if (secret.length >= 8) null else "password_short"
        }

        fun validateName(name: String): String? = if (name.trim().length !in 2..60) "name_invalid" else null
        private fun normalize(n: String) = n.trim().lowercase()
        private fun same(a: String, b: String) = normalize(a) == normalize(b)
    }
}
