package com.veltrix.os.data.api

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement

// Wire contracts mirroring packages/contracts (camelCase JSON). Only the fields the mobile app uses are declared;
// unknown fields are ignored so the server can evolve compatibly.

@Serializable data class ProblemResponse(val title: String? = null, val status: Int? = null, val code: String? = null, val correlationId: String? = null,
    val errors: Map<String, List<String>>? = null)

@Serializable data class LoginRequest(val email: String, val password: String, val mfaCode: String? = null, val deviceId: String? = null)
@Serializable data class RefreshRequest(val refreshToken: String)
@Serializable data class UserProfile(val id: String, val email: String, val fullName: String, val tenantId: String? = null, val isPlatformAdmin: Boolean = false,
    val roles: List<String> = emptyList(), val permissions: List<String> = emptyList(), val branches: List<String>? = null, val preferredLanguage: String? = null,
    val mfaEnabled: Boolean = false) {
    fun can(p: String) = permissions.contains(p) || permissions.contains("*")
}
@Serializable data class TokenResponse(val accessToken: String, val accessTokenExpiresAt: String, val refreshToken: String, val refreshTokenExpiresAt: String, val user: UserProfile)
@Serializable data class LoginResult(val accessToken: String? = null, val refreshToken: String? = null, val user: UserProfile? = null, val mfaRequired: Boolean = false)

@Serializable data class ChallengeRequest(val installationId: String, val purpose: String)
@Serializable data class ChallengeResponse(val challengeId: String, val nonce: String, val expiresAt: String)
@Serializable data class ActivateRequest(val licenseKey: String, val installationId: String, val platform: String, val deviceName: String, val appVersion: String,
    val publicKey: String, val fingerprintHash: String, val fingerprintVersion: Int, val challengeId: String, val signature: String)
@Serializable data class LicenseSummary(val id: String, val keyHint: String, val type: String, val status: String, val effectiveStatus: String, val planCode: String,
    val maxDevices: Int, val activeDevices: Int, val startsAt: String, val expiresAt: String? = null, val offlineGraceDays: Int, val entitlements: List<String>,
    val daysRemaining: Int? = null)
@Serializable data class ActivateResponse(val deviceId: String, val tenantId: String, val tenantName: String, val certificate: String, val license: LicenseSummary,
    val setupToken: String? = null, val setupTokenExpiresAt: String? = null, val setupCompleted: Boolean)
@Serializable data class RefreshCertificateRequest(val deviceId: String, val installationId: String, val appVersion: String, val challengeId: String, val signature: String)
@Serializable data class RefreshCertificateResponse(val certificate: String, val license: LicenseSummary)
@Serializable data class ReleaseDeviceRequest(val deviceId: String, val installationId: String, val challengeId: String, val signature: String)
@Serializable data class PublicKeyResponse(val algorithm: String, val spki: String)

@Serializable data class CompleteOnboardingRequest(val setupToken: String, val companyName: String, val legalName: String?, val taxId: String?, val tradeRegisterNo: String?,
    val address: String?, val city: String?, val wilaya: String?, val phone: String?, val currency: String, val locale: String, val ownerEmail: String,
    val ownerFullName: String, val ownerPassword: String, val ownerLanguage: String?, val headOfficeName: String, val applyAlgeriaTaxTemplate: Boolean)
@Serializable data class OnboardingResult(val tenantId: String, val branchId: String, val warehouseId: String)

@Serializable data class Paged<T>(val items: List<T>, val page: Int, val pageSize: Int, val totalCount: Int)

@Serializable data class Product(val id: String, val sku: String, val name: String, val nameAr: String? = null, val type: String, val status: String,
    val categoryId: String? = null, val taxRatePercent: Double = 0.0, val salePrice: Double, val costPrice: Double = 0.0, val trackStock: Boolean = true,
    val lowStockThreshold: Double = 0.0, val barcodes: List<String> = emptyList(), val updatedAt: String)
@Serializable data class Category(val id: String, val parentId: String? = null, val name: String, val nameAr: String? = null)
@Serializable data class Customer(val id: String, val code: String, val type: String, val name: String, val phone: String? = null, val email: String? = null,
    val creditLimit: Double = 0.0, val loyaltyPoints: Double = 0.0, val balance: Double = 0.0, val isActive: Boolean = true, val updatedAt: String)
@Serializable data class UpsertCustomerRequest(val type: String, val name: String, val phone: String?, val email: String?, val address: String? = null,
    val city: String? = null, val wilaya: String? = null, val taxId: String? = null, val tradeRegisterNo: String? = null, val groupId: String? = null,
    val priceListId: String? = null, val creditLimit: Double = 0.0, val marketingConsent: Boolean = false, val notes: String? = null, val code: String? = null, val id: String? = null)
@Serializable data class StockBalance(val warehouseId: String, val warehouseName: String, val productId: String, val sku: String, val productName: String,
    val quantity: Double, val reservedQuantity: Double = 0.0, val available: Double, val averageCost: Double = 0.0, val value: Double = 0.0, val lowStockThreshold: Double = 0.0)
@Serializable data class Register(val id: String, val branchId: String, val warehouseId: String, val code: String, val name: String, val isActive: Boolean, val openShiftId: String? = null)
@Serializable data class Branch(val id: String, val code: String, val name: String, val address: String? = null, val isHeadOffice: Boolean = false, val isActive: Boolean = true)

@Serializable data class SaleLineInput(val productId: String, val quantity: Double, val unitPrice: Double? = null, val discountPercent: Double = 0.0, val discountAmount: Double = 0.0)
@Serializable data class PaymentInput(val method: String, val amount: Double, val reference: String? = null)
@Serializable data class CompleteSaleRequest(val idempotencyKey: String, val registerId: String, val shiftId: String, val customerId: String?, val lines: List<SaleLineInput>,
    val payments: List<PaymentInput>, val cartDiscountAmount: Double = 0.0, val notes: String? = null, val id: String? = null, val clientCreatedAt: String? = null,
    val offline: Boolean = true, val clientNumber: String? = null)
@Serializable data class OpenShiftRequest(val registerId: String, val openingFloat: Double, val id: String? = null, val openedAt: String? = null)
@Serializable data class CloseShiftRequest(val countedCash: Double, val notes: String? = null, val closedAt: String? = null)
@Serializable data class ShiftPayload<T>(val shiftId: String, val request: T)
@Serializable data class CheckRequest(val employeeId: String, val source: String, val at: String? = null, val id: String? = null)
@Serializable data class CreateExpenseRequest(val date: String, val expenseAccountId: String, val amount: Double, val taxAmount: Double, val paymentAccountId: String,
    val payee: String?, val description: String?, val branchId: String?, val id: String? = null)

@Serializable data class SyncOperation(val opId: String, val type: String, val payload: JsonElement, val clientTime: String, val baseVersion: String? = null)
@Serializable data class SyncPushRequest(val operations: List<SyncOperation>)
@Serializable data class SyncOperationResult(val opId: String, val status: String, val code: String? = null, val message: String? = null, val entityId: String? = null)
@Serializable data class SyncPushResponse(val results: List<SyncOperationResult>, val serverTime: String)
@Serializable data class SyncPullResponse(val cursor: String, val hasMore: Boolean, val products: List<Product>, val categories: List<Category>, val customers: List<Customer>,
    val stockBalances: List<StockBalance>, val registers: List<Register>, val settings: Map<String, String> = emptyMap())

@Serializable data class SeriesPoint(val date: String, val value: Double)
@Serializable data class NamedValue(val name: String, val value: Double)
@Serializable data class Dashboard(val salesToday: Double, val salesMonth: Double, val salesPreviousMonth: Double, val transactionsToday: Int, val averageBasket: Double,
    val grossMarginMonth: Double, val lowStockItems: Int, val receivablesOutstanding: Double, val payablesOutstanding: Double, val pendingApprovals: Int,
    val last30Days: List<SeriesPoint>, val topProducts: List<NamedValue>, val salesByBranch: List<NamedValue>)
@Serializable data class ReportColumn(val key: String, val title: String, val type: String)
@Serializable data class ReportTable(val key: String, val title: String, val columns: List<ReportColumn>, val rows: List<List<JsonElement>>, val summary: Map<String, JsonElement> = emptyMap())
@Serializable data class ReportInfo(val key: String, val title: String)
@Serializable data class AppNotification(val id: String, val type: String, val title: String, val body: String, val severity: String, val link: String? = null,
    val createdAt: String, val read: Boolean)
@Serializable data class Employee(val id: String, val code: String, val fullName: String, val phone: String? = null, val branchId: String, val status: String,
    val userId: String? = null)
@Serializable data class SalesDocument(val id: String, val type: String, val number: String, val status: String, val customerName: String, val date: String,
    val total: Double, val outstanding: Double = 0.0)
@Serializable data class Expense(val id: String, val number: String, val date: String, val expenseAccountName: String, val amount: Double, val payee: String? = null)
@Serializable data class Account(val id: String, val code: String, val name: String, val type: String, val isPostable: Boolean, val systemRole: String? = null)
@Serializable data class Company(val id: String, val name: String, val currency: String, val locale: String, val status: String, val setupCompleted: Boolean)
