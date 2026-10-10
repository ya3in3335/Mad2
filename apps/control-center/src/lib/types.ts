/** DTOs mirrored from packages/contracts/Veltrix.Contracts (JSON is camelCase; dates are ISO strings). */
export type Guid = string;
export type IsoDate = string;

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface Problem {
  type?: string;
  title?: string;
  status?: number;
  detail?: string | null;
  code?: string | null;
  correlationId?: string | null;
  errors?: Record<string, string[]> | null;
}

// ---- Identity ----
export interface UserProfile {
  id: Guid;
  email: string;
  fullName: string;
  tenantId: Guid | null;
  isPlatformAdmin: boolean;
  roles: string[];
  permissions: string[];
  branches: Guid[] | null;
  preferredLanguage: string | null;
  mfaEnabled: boolean;
}

export interface TokenResponse {
  accessToken: string;
  accessTokenExpiresAt: IsoDate;
  refreshToken: string;
  refreshTokenExpiresAt: IsoDate;
  user: UserProfile;
}

export interface MfaRequiredResponse {
  mfaRequired: boolean;
}

// ---- Platform ----
export interface MonthValue {
  month: string;
  value: number;
}

export interface PlatformDashboard {
  tenants: number;
  activeTenants: number;
  suspendedTenants: number;
  pendingSetup: number;
  activeLicenses: number;
  expiringLicenses14d: number;
  activeDevices: number;
  activations30d: number;
  monthlyRecurringRevenue: number;
  revenueThisMonth: number;
  revenueByMonth: MonthValue[];
  activationsByMonth: MonthValue[];
  openSupportCases: number;
  securityAlerts7d: number;
}

export interface PlatformAdmin {
  id: Guid;
  email: string;
  fullName: string;
  isActive: boolean;
  mfaEnabled: boolean;
  lastLoginAt: IsoDate | null;
}

export interface SupportMessage {
  id: Guid;
  authorId: Guid;
  fromVendor: boolean;
  body: string;
  createdAt: IsoDate;
}

export interface SupportCase {
  id: Guid;
  tenantId: Guid | null;
  tenantName: string | null;
  subject: string;
  status: string;
  priority: string;
  createdBy: Guid;
  createdAt: IsoDate;
  updatedAt: IsoDate;
  messages: SupportMessage[];
}

export interface AuditLog {
  id: Guid;
  tenantId: Guid | null;
  userId: Guid | null;
  deviceId: Guid | null;
  action: string;
  entityType: string;
  entityId: string | null;
  details: string | null;
  ipAddress: string | null;
  correlationId: string | null;
  occurredAt: IsoDate;
}

export interface SecurityAlert {
  kind: string;
  severity: string;
  tenantId: Guid | null;
  userId: Guid | null;
  description: string;
  occurredAt: IsoDate;
}

export interface WorkerStatus {
  name: string;
  lastRunAt: IsoDate;
  lastStatus: string;
  lastError: string | null;
  stale: boolean;
}

export interface BackupRecord {
  id: Guid;
  fileName: string;
  sizeBytes: number;
  sha256: string;
  startedAt: IsoDate;
  completedAt: IsoDate | null;
  status: string;
  error: string | null;
  verified: boolean;
  verifiedAt: IsoDate | null;
  verificationDetails: string | null;
}

export interface SystemHealth {
  status: string;
  databaseReachable: boolean;
  pendingMigrations: string[];
  workers: WorkerStatus[];
  lastBackup: BackupRecord | null;
  version: string;
  serverTime: IsoDate;
}

export interface AppRelease {
  id: Guid;
  platform: string;
  version: string;
  channel: string;
  minSupportedVersion: string;
  releaseNotes: string | null;
  downloadUrl: string | null;
  sha256: string | null;
  isActive: boolean;
  publishedAt: IsoDate;
}

export interface Announcement {
  id: Guid;
  title: string;
  body: string;
  severity: string;
  tenantId: Guid | null;
  startsAt: IsoDate;
  endsAt: IsoDate | null;
  published: boolean;
}

export interface PlatformSetting {
  key: string;
  value: string;
}

// ---- Tenants ----
export interface TenantSummary {
  id: Guid;
  name: string;
  status: string;
  setupCompleted: boolean;
  email: string | null;
  phone: string | null;
  wilaya: string | null;
  createdAt: IsoDate;
  planCode: string | null;
  subscriptionStatus: string | null;
  subscriptionEndsAt: IsoDate | null;
  activeDevices: number;
  users: number;
}

export interface Company {
  id: Guid;
  name: string;
  legalName: string | null;
  tradeRegisterNo: string | null;
  taxId: string | null;
  statisticalId: string | null;
  articleNo: string | null;
  address: string | null;
  city: string | null;
  wilaya: string | null;
  country: string;
  phone: string | null;
  email: string | null;
  currency: string;
  locale: string;
  timeZone: string;
  brandColor: string | null;
  status: string;
  setupCompleted: boolean;
  fiscalYearStartMonth: number;
}

export interface CreateTenantResponse {
  tenantId: Guid;
  setupToken: string;
  setupTokenExpiresAt: IsoDate;
}

// ---- Licensing ----
export interface License {
  id: Guid;
  tenantId: Guid;
  tenantName: string | null;
  keyHint: string;
  type: string;
  status: string;
  effectiveStatus: string;
  planCode: string;
  maxDevices: number;
  activeDevices: number;
  startsAt: IsoDate;
  expiresAt: IsoDate | null;
  offlineGraceDays: number;
  entitlements: string[];
  notes: string | null;
  createdAt: IsoDate;
}

export interface CreateLicenseResponse {
  license: License;
  licenseKey: string;
}

export interface Device {
  id: Guid;
  licenseId: Guid;
  tenantId: Guid;
  tenantName: string | null;
  installationId: Guid;
  platform: string;
  deviceName: string;
  appVersion: string;
  status: string;
  activatedAt: IsoDate;
  lastSeenAt: IsoDate;
  revokedAt: IsoDate | null;
  revokedReason: string | null;
  keyThumbprint: string;
}

export interface LicenseEvent {
  id: Guid;
  licenseId: Guid;
  deviceId: Guid | null;
  type: string;
  details: string | null;
  occurredAt: IsoDate;
}

// ---- Subscriptions ----
export interface Plan {
  id: Guid;
  code: string;
  name: string;
  description: string | null;
  monthlyPrice: number;
  annualPrice: number;
  currency: string;
  features: string[];
  maxDevices: number;
  maxUsers: number;
  maxBranches: number;
  trialDays: number;
  offlineGraceDays: number;
  isActive: boolean;
  isPublic: boolean;
}

export interface Subscription {
  id: Guid;
  tenantId: Guid;
  tenantName: string | null;
  planCode: string;
  billingCycle: string;
  status: string;
  currentPeriodStart: IsoDate;
  currentPeriodEnd: IsoDate;
  seats: number;
  cancelAtPeriodEnd: boolean;
  licenseId: Guid | null;
  periodPrice: number;
  currency: string;
  createdAt: IsoDate;
}

export interface BillingInvoice {
  id: Guid;
  number: string;
  tenantId: Guid;
  tenantName: string | null;
  subscriptionId: Guid;
  periodStart: IsoDate;
  periodEnd: IsoDate;
  amount: number;
  tax: number;
  total: number;
  currency: string;
  status: string;
  issuedAt: IsoDate;
  dueDate: string;
  paidAt: IsoDate | null;
  paymentMethod: string | null;
  paymentReference: string | null;
  description: string | null;
}

export interface EntitlementChange {
  id: Guid;
  subscriptionId: Guid;
  change: string;
  details: string | null;
  at: IsoDate;
  by: Guid | null;
}

// ---- Static enumerations (mirrors of backend enums / constants) ----
export const FEATURES = [
  "pos",
  "inventory",
  "purchasing",
  "crm",
  "sales",
  "accounting",
  "hr",
  "reports",
  "intelligence",
  "multi_branch",
  "mobile",
  "api",
] as const;
export const LICENSE_TYPES = ["Trial", "Monthly", "Quarterly", "Annual", "Perpetual", "Enterprise", "Custom"] as const;
export const LICENSE_PLAN_CODES = ["trial", "starter", "professional", "enterprise", "custom"] as const;
export const TENANT_STATUSES = ["Pending", "Active", "Suspended", "Closed"] as const;
export const SUBSCRIPTION_STATUSES = ["Trialing", "Active", "PastDue", "Cancelled", "Expired"] as const;
export const BILLING_CYCLES = ["Monthly", "Annual", "Custom"] as const;
export const INVOICE_STATUSES = ["Issued", "Paid", "Void"] as const;
export const SUPPORT_STATUSES = ["Open", "Pending", "Resolved", "Closed"] as const;
export const ANNOUNCEMENT_SEVERITIES = ["Info", "Success", "Warning", "Critical"] as const;
export const PAYMENT_METHODS = ["bank_transfer", "cash", "cheque", "ccp", "baridimob", "other"] as const;
export const JOB_NAMES = [
  "backup",
  "subscription-status",
  "license-expiry-reminders",
  "email-notifications",
  "scheduled-reports",
  "cleanup",
] as const;
