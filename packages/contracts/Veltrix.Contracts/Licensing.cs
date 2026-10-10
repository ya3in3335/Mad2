namespace Veltrix.Contracts.Licensing;

public sealed record ChallengeRequest(Guid InstallationId, string Purpose);
public sealed record ChallengeResponse(Guid ChallengeId, string Nonce, DateTimeOffset ExpiresAt);

/// <summary>Device activation. Signature = ECDSA-P256-SHA256 (DER) over UTF-8 "{nonce}|{installationId}|activate" using the device key.</summary>
public sealed record ActivateRequest(
    string LicenseKey, Guid InstallationId, string Platform, string DeviceName, string AppVersion, string PublicKey,
    string FingerprintHash, int FingerprintVersion, Guid ChallengeId, string Signature);

public sealed record ActivateResponse(
    Guid DeviceId, Guid TenantId, string TenantName, string Certificate, LicenseSummaryDto License, string? SetupToken,
    DateTimeOffset? SetupTokenExpiresAt, bool SetupCompleted);

/// <summary>Heartbeat / certificate renewal. Signature over "{nonce}|{installationId}|refresh".</summary>
public sealed record RefreshCertificateRequest(Guid DeviceId, Guid InstallationId, string AppVersion, Guid ChallengeId, string Signature);
public sealed record RefreshCertificateResponse(string Certificate, LicenseSummaryDto License);

/// <summary>Voluntary seat release. Signature over "{nonce}|{installationId}|release".</summary>
public sealed record ReleaseDeviceRequest(Guid DeviceId, Guid InstallationId, Guid ChallengeId, string Signature);

public sealed record LicenseSummaryDto(
    Guid Id, string KeyHint, string Type, string Status, string EffectiveStatus, string PlanCode, int MaxDevices, int ActiveDevices,
    DateTimeOffset StartsAt, DateTimeOffset? ExpiresAt, int OfflineGraceDays, IReadOnlyList<string> Entitlements, int? DaysRemaining);

/// <summary>Decoded license certificate payload (signed by the vendor; verified offline by clients).</summary>
public sealed record LicenseCertificatePayload(
    int V, Guid Lid, Guid Tid, string TenantName, Guid Did, Guid Iid, string Kth, string Fp, string Plan, string Type,
    IReadOnlyList<string> Ent, int MaxDev, long Iat, long Exp, long? Lexp, string Status);

public sealed record DeviceDto(
    Guid Id, Guid LicenseId, Guid TenantId, string? TenantName, Guid InstallationId, string Platform, string DeviceName, string AppVersion,
    string Status, DateTimeOffset ActivatedAt, DateTimeOffset LastSeenAt, DateTimeOffset? RevokedAt, string? RevokedReason, string KeyThumbprint);

public sealed record LicenseEventDto(Guid Id, Guid LicenseId, Guid? DeviceId, string Type, string? Details, DateTimeOffset OccurredAt);

// ---- Platform administration ----
public sealed record LicenseDto(
    Guid Id, Guid TenantId, string? TenantName, string KeyHint, string Type, string Status, string EffectiveStatus, string PlanCode,
    int MaxDevices, int ActiveDevices, DateTimeOffset StartsAt, DateTimeOffset? ExpiresAt, int OfflineGraceDays,
    IReadOnlyList<string> Entitlements, string? Notes, DateTimeOffset CreatedAt);

public sealed record CreateLicenseRequest(Guid TenantId, string Type, string PlanCode, int MaxDevices, DateTimeOffset? StartsAt,
    DateTimeOffset? ExpiresAt, int? OfflineGraceDays, IReadOnlyList<string>? Entitlements, string? Notes);
public sealed record CreateLicenseResponse(LicenseDto License, string LicenseKey);
public sealed record UpdateLicenseRequest(int MaxDevices, DateTimeOffset? ExpiresAt, int OfflineGraceDays, IReadOnlyList<string> Entitlements, string? Notes);
public sealed record ExtendLicenseRequest(int Days, string? Reason);
public sealed record LicenseStatusRequest(string Reason);
public sealed record TransferLicenseRequest(Guid TargetTenantId, string Reason);
public sealed record RevokeDeviceRequest(string Reason);
