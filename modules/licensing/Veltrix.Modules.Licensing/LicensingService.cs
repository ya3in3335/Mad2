using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Veltrix.Contracts.Licensing;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Licensing;

/// <summary>
/// Single commercial plan: VELTRIX OS is sold as one plan ("full") that includes every feature. The commercial lever is the
/// per-license device seat count (and the subscription price). "trial" is only used for demo licenses and also includes everything.
/// </summary>
public static class PlanDefaults
{
    public const string Full = "full";
    public const string Trial = "trial";
    public const int DefaultGraceDays = 14;

    public static readonly IReadOnlyDictionary<string, (string[] Features, int MaxDevices, int GraceDays)> Plans =
        new Dictionary<string, (string[], int, int)>
        {
            [Trial] = (Features.All.ToArray(), 2, 3),
            [Full] = (Features.All.ToArray(), 5, DefaultGraceDays),
        };
}

/// <summary>Device-facing activation protocol (anonymous endpoints, authenticated by license key + device key signatures).</summary>
public sealed class ActivationService(
    DbContext db, LicenseSigner signer, LicensingOptions options, IClock clock, IAuditWriter audit,
    TenantAdminService tenantAdmin, IMemoryCache cache, IAlertPublisher alerts, IDiagnosticsRecorder diagnostics,
    Microsoft.AspNetCore.Http.IHttpContextAccessor http)
{
    public const string ActivationFailedAction = "licensing.activation_failed";

    public static readonly string[] Purposes = ["activate", "refresh", "release"];

    public async Task<ChallengeResponse> CreateChallengeAsync(ChallengeRequest req, CancellationToken ct)
    {
        if (req.InstallationId == Guid.Empty || !Purposes.Contains(req.Purpose))
            throw ValidationException.For("purpose", "Invalid challenge request.");
        var ch = new ActivationChallenge
        {
            InstallationId = req.InstallationId, Purpose = req.Purpose,
            Nonce = LicenseSigner.B64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ExpiresAt = clock.UtcNow.AddSeconds(options.ChallengeSeconds),
        };
        db.Set<ActivationChallenge>().Add(ch);
        // Opportunistic cleanup of stale challenges.
        await db.Set<ActivationChallenge>().Where(c => c.ExpiresAt < clock.UtcNow.AddHours(-1)).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return new ChallengeResponse(ch.Id, ch.Nonce, ch.ExpiresAt);
    }

    public static string ChallengeMessage(string nonce, Guid installationId, string purpose) => $"{nonce}|{installationId}|{purpose}";

    private async Task ConsumeChallengeAsync(Guid challengeId, Guid installationId, string purpose, string publicKey, string signature, CancellationToken ct)
    {
        var ch = await db.Set<ActivationChallenge>().SingleOrDefaultAsync(c => c.Id == challengeId, ct);
        var now = clock.UtcNow;
        if (ch is null || ch.UsedAt is not null || ch.ExpiresAt < now || ch.InstallationId != installationId || ch.Purpose != purpose)
            throw new UnauthorizedException("licensing.challenge_invalid", "The activation challenge is invalid or expired. Retry.");
        // Mark used before verifying so a failed attempt cannot be replayed either.
        ch.UsedAt = now;
        await db.SaveChangesAsync(ct);
        if (!LicenseSigner.VerifyDeviceSignature(publicKey, ChallengeMessage(ch.Nonce, installationId, purpose), signature))
            throw new UnauthorizedException("licensing.signature_invalid", "Device signature verification failed.");
    }

    public async Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.LicenseKey, "licenseKey", 64).Require(req.InstallationId != Guid.Empty, "installationId", "Required.")
            .Require(req.Platform is "windows" or "android", "platform", "Unsupported platform.")
            .NotEmpty(req.DeviceName, "deviceName", 120).NotEmpty(req.PublicKey, "publicKey", 500).NotEmpty(req.FingerprintHash, "fingerprintHash", 128)
            .MaxLength(req.AppVersion, "appVersion", 40).ThrowIfInvalid();
        try
        {
            return await ActivateCoreAsync(req, ct);
        }
        catch (DomainException ex)
        {
            await RecordFailedActivationAsync(req, ex.Code, ct);
            throw;
        }
    }

    /// <summary>Failed activation attempts are kept in the audit trail (and the license history when the key is recognized).
    /// The license key itself is never stored.</summary>
    private async Task RecordFailedActivationAsync(ActivateRequest req, string code, CancellationToken ct)
    {
        try
        {
            db.ChangeTracker.Clear();
            var keyHash = LicenseKeys.Hash(req.LicenseKey);
            var license = await db.Set<License>().IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(l => l.KeyHash == keyHash, ct);
            var now = clock.UtcNow;
            db.Set<AuditLog>().Add(new AuditLog
            {
                TenantId = license?.TenantId, Action = ActivationFailedAction, EntityType = nameof(License), EntityId = license?.Id.ToString(),
                Details = System.Text.Json.JsonSerializer.Serialize(new { code, platform = req.Platform, installationId = req.InstallationId, keyRecognized = license is not null }),
                IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(), OccurredAt = now,
            });
            if (license is not null) AddEvent(license, null, "device.activation_failed", code);
            await db.SaveChangesAsync(ct);
            var tenantName = license is null ? null : await db.Set<Company>().Where(c => c.Id == license.TenantId).Select(c => c.Name).SingleOrDefaultAsync(ct);
            await diagnostics.RecordAsync(new ServerDiagnosticEvent(DiagnosticCategories.DeviceRegistration, DiagnosticSeverities.Warning, "activation_failed",
                $"License activation failed: {code}", license?.TenantId), ct);
            await alerts.PublishAsync(new AlertMessage(AlertTypes.LicenseActivationFailed, "License activation failed",
                $"Activation failed ({code}) on a {req.Platform} device" + (tenantName is null ? " with an unrecognized key." : $" for {tenantName}."),
                AlertSeverity.Warning, $"activation-failed:{req.InstallationId}:{code}", license?.TenantId), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Recording a failure must never mask the original error.
            db.ChangeTracker.Clear();
        }
    }

    private async Task<ActivateResponse> ActivateCoreAsync(ActivateRequest req, CancellationToken ct)
    {
        await ConsumeChallengeAsync(req.ChallengeId, req.InstallationId, "activate", req.PublicKey, req.Signature, ct);

        var now = clock.UtcNow;
        var keyHash = LicenseKeys.Hash(req.LicenseKey);
        // Activation is anonymous: query across tenants explicitly by key hash only.
        var license = await db.Set<License>().IgnoreQueryFilters().SingleOrDefaultAsync(l => l.KeyHash == keyHash, ct);
        if (license is null) throw new NotFoundException("licensing.key_invalid", "License key not recognized. Check the key and try again.");
        var eff = license.EffectiveStatus(now);
        if (!license.IsUsable(now))
            throw eff switch
            {
                EffectiveLicenseStatus.Expired => new PaymentRequiredException("licensing.expired", "This license has expired. Renew your subscription to activate devices."),
                EffectiveLicenseStatus.Pending => new DomainException("licensing.not_started", "This license is not active yet.", 409),
                _ => new ForbiddenException("licensing.unavailable", $"This license is {eff.ToString().ToLowerInvariant()}. Contact your vendor."),
            };

        var company = await db.Set<Company>().SingleAsync(c => c.Id == license.TenantId, ct);
        if (company.Status == CompanyStatus.Suspended) throw new ForbiddenException("tenant.suspended", "This company account is suspended.");

        var thumb = LicenseSigner.Thumbprint(req.PublicKey);
        var devices = await db.Set<Device>().IgnoreQueryFilters().Where(d => d.LicenseId == license.Id && d.Status == DeviceStatus.Active).ToListAsync(ct);

        var device = devices.FirstOrDefault(d => d.InstallationId == req.InstallationId && d.PublicKeyThumbprint == thumb);
        var isNewDevice = device is null;
        if (device is null)
        {
            // Same hardware re-installed (new installation/key, same fingerprint): replace the old activation in place.
            var previous = devices.FirstOrDefault(d => d.FingerprintHash == req.FingerprintHash && d.FingerprintVersion == req.FingerprintVersion && d.Platform == req.Platform);
            if (previous is null && devices.Count >= license.MaxDevices)
                throw new ConflictException("licensing.seats_exhausted",
                    $"All {license.MaxDevices} device seats are in use. Release a device from another computer, or ask your administrator to revoke an old device.");
            device = new Device
            {
                TenantId = license.TenantId, LicenseId = license.Id, InstallationId = req.InstallationId, Platform = req.Platform,
                PublicKey = req.PublicKey, PublicKeyThumbprint = thumb, ActivatedAt = now,
            };
            db.Set<Device>().Add(device);
            if (previous is not null)
            {
                previous.Status = DeviceStatus.Replaced; previous.RevokedAt = now; previous.RevokedReason = "replaced_by_reinstall";
                previous.ReplacedByDeviceId = device.Id;
                AddEvent(license, previous.Id, "device.replaced", $"replaced by {device.Id}");
            }
            AddEvent(license, device.Id, "device.activated", req.DeviceName);
        }
        device.DeviceName = req.DeviceName.Trim();
        device.AppVersion = req.AppVersion;
        device.FingerprintHash = req.FingerprintHash;
        device.FingerprintVersion = req.FingerprintVersion;
        device.LastSeenAt = now;
        audit.Write("licensing.device_activated", nameof(Device), device.Id.ToString(), new { license.Id, req.Platform, req.DeviceName });
        await db.SaveChangesAsync(ct);
        cache.Remove(DeviceCacheKey(device.Id));
        var activeCount = devices.Count(d => d.Status == DeviceStatus.Active) + (devices.Contains(device) ? 0 : 1);
        if (isNewDevice)
            await alerts.PublishAsync(new AlertMessage(AlertTypes.DeviceActivated, "New device activated",
                $"{company.Name}: new {req.Platform} device \"{req.DeviceName.Trim()}\" activated ({activeCount}/{license.MaxDevices} seats).",
                AlertSeverity.Info, $"device-activated:{device.Id}", license.TenantId), ct);

        var setup = company.SetupCompleted ? null : await tenantAdmin.IssueSetupTokenIfPendingAsync(company.Id, ct);
        return new ActivateResponse(device.Id, license.TenantId, company.Name, IssueCertificate(license, device, company.Name, now),
            ToSummary(license, activeCount, now), setup?.Token, setup?.Expires, company.SetupCompleted);
    }

    public async Task<RefreshCertificateResponse> RefreshAsync(RefreshCertificateRequest req, CancellationToken ct)
    {
        var device = await db.Set<Device>().IgnoreQueryFilters().SingleOrDefaultAsync(d => d.Id == req.DeviceId, ct)
                     ?? throw new NotFoundException("licensing.device_unknown", "Device not registered. Activate again.");
        if (device.InstallationId != req.InstallationId) throw new UnauthorizedException("licensing.device_mismatch", "Device identity mismatch.");
        await ConsumeChallengeAsync(req.ChallengeId, req.InstallationId, "refresh", device.PublicKey, req.Signature, ct);
        var now = clock.UtcNow;
        if (device.Status != DeviceStatus.Active)
            throw new ForbiddenException("licensing.device_revoked", "This device's activation was revoked. Contact your administrator.");
        var license = await db.Set<License>().IgnoreQueryFilters().SingleAsync(l => l.Id == device.LicenseId, ct);
        if (!license.IsUsable(now))
            throw license.EffectiveStatus(now) == EffectiveLicenseStatus.Expired
                ? new PaymentRequiredException("licensing.expired", "This license has expired. Renew to continue.")
                : new ForbiddenException("licensing.unavailable", "This license is no longer active.");
        var company = await db.Set<Company>().SingleAsync(c => c.Id == license.TenantId, ct);
        if (company.Status == CompanyStatus.Suspended) throw new ForbiddenException("tenant.suspended", "This company account is suspended.");
        device.LastSeenAt = now;
        if (!string.IsNullOrWhiteSpace(req.AppVersion) && req.AppVersion.Length <= 40) device.AppVersion = req.AppVersion;
        await db.SaveChangesAsync(ct);
        var active = await db.Set<Device>().IgnoreQueryFilters().CountAsync(d => d.LicenseId == license.Id && d.Status == DeviceStatus.Active, ct);
        return new RefreshCertificateResponse(IssueCertificate(license, device, company.Name, now), ToSummary(license, active, now));
    }

    public async Task ReleaseAsync(ReleaseDeviceRequest req, CancellationToken ct)
    {
        var device = await db.Set<Device>().IgnoreQueryFilters().SingleOrDefaultAsync(d => d.Id == req.DeviceId, ct)
                     ?? throw new NotFoundException("licensing.device_unknown", "Device not registered.");
        if (device.InstallationId != req.InstallationId) throw new UnauthorizedException("licensing.device_mismatch", "Device identity mismatch.");
        await ConsumeChallengeAsync(req.ChallengeId, req.InstallationId, "release", device.PublicKey, req.Signature, ct);
        if (device.Status != DeviceStatus.Active) return;
        device.Status = DeviceStatus.Released;
        device.RevokedAt = clock.UtcNow;
        device.RevokedReason = "released_by_device";
        var license = await db.Set<License>().IgnoreQueryFilters().SingleAsync(l => l.Id == device.LicenseId, ct);
        AddEvent(license, device.Id, "device.released", null);
        await db.SaveChangesAsync(ct);
        cache.Remove(DeviceCacheKey(device.Id));
    }

    private string IssueCertificate(License license, Device device, string tenantName, DateTimeOffset now)
    {
        var graceDays = Math.Clamp(license.OfflineGraceDays, 0, options.MaxOfflineDays);
        var exp = now.AddDays(graceDays);
        // The offline certificate never outlives the license itself.
        if (license.ExpiresAt is { } le && le < exp) exp = le;
        var payload = new LicenseCertificatePayload(1, license.Id, license.TenantId, tenantName, device.Id, device.InstallationId,
            device.PublicKeyThumbprint, device.FingerprintHash, license.PlanCode, license.Type.ToString(), license.EntitlementList,
            license.MaxDevices, now.ToUnixTimeSeconds(), exp.ToUnixTimeSeconds(), license.ExpiresAt?.ToUnixTimeSeconds(),
            license.EffectiveStatus(now).ToString());
        return signer.Sign(payload);
    }

    private void AddEvent(License license, Guid? deviceId, string type, string? details) =>
        db.Set<LicenseEvent>().Add(new LicenseEvent
        {
            TenantId = license.TenantId, LicenseId = license.Id, DeviceId = deviceId, Type = type, Details = details, OccurredAt = clock.UtcNow,
        });

    public static string DeviceCacheKey(Guid id) => "device-active:" + id;

    public static LicenseSummaryDto ToSummary(License l, int activeDevices, DateTimeOffset now) =>
        new(l.Id, l.KeyHint, l.Type.ToString(), l.Status.ToString(), l.EffectiveStatus(now).ToString(), l.PlanCode, l.MaxDevices,
            activeDevices, l.StartsAt, l.ExpiresAt, l.OfflineGraceDays, l.EntitlementList,
            l.ExpiresAt is { } e ? (int)Math.Ceiling((e - now).TotalDays) : null);
}

/// <summary>License administration for the vendor (Control Center) and read access for company administrators.</summary>
public sealed class LicenseAdminService(DbContext db, ITenantContext ctx, IClock clock, IAuditWriter audit, IMemoryCache cache, ITenantStatusProvider status)
{
    public async Task<CreateLicenseResponse> CreateAsync(CreateLicenseRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<LicenseType>(req.Type, true, out var type)) throw ValidationException.For("type", "Unknown license type.");
        var plan = req.PlanCode?.Trim().ToLowerInvariant() ?? "";
        new Validator().Require(PlanDefaults.Plans.ContainsKey(plan), "planCode", "Unknown plan.")
            .Require(req.MaxDevices is >= 1 and <= 1000, "maxDevices", "Must be between 1 and 1000.")
            .Require(req.OfflineGraceDays is null or (>= 0 and <= 30), "offlineGraceDays", "Must be between 0 and 30.")
            .Require(req.Entitlements is null || req.Entitlements.All(Features.All.Contains), "entitlements", "Unknown feature.")
            .Require(type == LicenseType.Perpetual || req.ExpiresAt is not null || type == LicenseType.Trial || type is LicenseType.Monthly or LicenseType.Quarterly or LicenseType.Annual,
                "expiresAt", "Expiry is required for this license type.")
            .ThrowIfInvalid();
        if (!await db.Set<Company>().AnyAsync(c => c.Id == req.TenantId, ct)) throw ValidationException.For("tenantId", "Unknown tenant.");

        var now = clock.UtcNow;
        var starts = req.StartsAt ?? now;
        var expires = req.ExpiresAt ?? type switch
        {
            LicenseType.Trial => starts.AddDays(14),
            LicenseType.Monthly => starts.AddMonths(1),
            LicenseType.Quarterly => starts.AddMonths(3),
            LicenseType.Annual => starts.AddYears(1),
            _ => (DateTimeOffset?)null,
        };
        var defaults = PlanDefaults.Plans[plan];
        var key = LicenseKeys.New();
        var license = new License
        {
            TenantId = req.TenantId, KeyHash = LicenseKeys.Hash(key), KeyHint = LicenseKeys.Hint(key), Type = type, PlanCode = plan,
            MaxDevices = req.MaxDevices, StartsAt = starts, ExpiresAt = type == LicenseType.Perpetual ? null : expires,
            OfflineGraceDays = req.OfflineGraceDays ?? defaults.GraceDays,
            Entitlements = string.Join(',', defaults.Features), // single plan: every license includes every feature
            Notes = req.Notes, Status = LicenseStatus.Active,
        };
        db.Set<License>().Add(license);
        AddEvent(license, null, "license.created", $"{type} {plan} x{req.MaxDevices}");
        audit.Write("platform.license_created", nameof(License), license.Id.ToString(), new { req.TenantId, type, plan, req.MaxDevices });
        await db.SaveChangesAsync(ct);
        status.Invalidate(license.TenantId);
        return new CreateLicenseResponse(await ToDtoAsync(license, ct), key);
    }

    public async Task<PagedResult<LicenseDto>> ListAsync(PageQuery q, Guid? tenantId, CancellationToken ct)
    {
        var query = db.Set<License>().IgnoreQueryFilters().AsQueryable();
        if (tenantId is { } t) query = query.Where(l => l.TenantId == t);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToUpperInvariant();
            var tenants = db.Set<Company>().Where(c => c.Name.ToUpper().Contains(s)).Select(c => c.Id);
            query = query.Where(l => l.KeyHint.Contains(s) || tenants.Contains(l.TenantId) || l.PlanCode.ToUpper().Contains(s));
        }
        var page = await query.OrderByDescending(l => l.CreatedAt).ToPagedAsync(q, ct);
        var dtos = new List<LicenseDto>();
        foreach (var l in page.Items) dtos.Add(await ToDtoAsync(l, ct));
        return new PagedResult<LicenseDto>(dtos, page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<LicenseDto> GetAsync(Guid id, CancellationToken ct) => await ToDtoAsync(await LoadAsync(id, ct), ct);

    public async Task<LicenseDto> UpdateAsync(Guid id, UpdateLicenseRequest req, CancellationToken ct)
    {
        var l = await LoadAsync(id, ct);
        new Validator().Require(req.MaxDevices is >= 1 and <= 1000, "maxDevices", "Must be between 1 and 1000.")
            .Require(req.OfflineGraceDays is >= 0 and <= 30, "offlineGraceDays", "Must be between 0 and 30.")
            .Require(req.Entitlements.All(Features.All.Contains), "entitlements", "Unknown feature.").ThrowIfInvalid();
        l.MaxDevices = req.MaxDevices; l.ExpiresAt = l.Type == LicenseType.Perpetual ? null : req.ExpiresAt;
        l.OfflineGraceDays = req.OfflineGraceDays; l.Entitlements = string.Join(',', Features.All); l.Notes = req.Notes;
        AddEvent(l, null, "license.updated", $"devices={req.MaxDevices} expires={req.ExpiresAt:O}");
        audit.Write("platform.license_updated", nameof(License), l.Id.ToString(), req);
        await db.SaveChangesAsync(ct);
        status.Invalidate(l.TenantId);
        return await ToDtoAsync(l, ct);
    }

    /// <summary>Extend a trial or renew a subscription license by N days from the later of now or current expiry.</summary>
    public async Task<LicenseDto> ExtendAsync(Guid id, int days, string? reason, CancellationToken ct)
    {
        if (days is < 1 or > 3660) throw ValidationException.For("days", "Must be between 1 and 3660.");
        var l = await LoadAsync(id, ct);
        if (l.Type == LicenseType.Perpetual) throw new DomainException("licensing.perpetual", "Perpetual licenses do not expire.");
        if (l.Status is LicenseStatus.Revoked or LicenseStatus.Transferred) throw new DomainException("licensing.closed", "This license is closed.");
        var now = clock.UtcNow;
        var from = l.ExpiresAt is { } e && e > now ? e : now;
        l.ExpiresAt = from.AddDays(days);
        AddEvent(l, null, l.Type == LicenseType.Trial ? "license.trial_extended" : "license.renewed", $"+{days}d {reason}");
        audit.Write("platform.license_extended", nameof(License), l.Id.ToString(), new { days, reason });
        await db.SaveChangesAsync(ct);
        status.Invalidate(l.TenantId);
        return await ToDtoAsync(l, ct);
    }

    public async Task<LicenseDto> SetStatusAsync(Guid id, LicenseStatus newStatus, string reason, CancellationToken ct)
    {
        new Validator().NotEmpty(reason, "reason", 500).ThrowIfInvalid();
        var l = await LoadAsync(id, ct);
        if (l.Status is LicenseStatus.Revoked or LicenseStatus.Transferred)
            throw new DomainException("licensing.closed", "This license is closed and cannot change status.");
        l.Status = newStatus;
        l.StatusReason = reason;
        if (newStatus == LicenseStatus.Revoked) await RevokeAllDevicesAsync(l, "license_revoked", ct);
        AddEvent(l, null, "license." + newStatus.ToString().ToLowerInvariant(), reason);
        audit.Write("platform.license_status", nameof(License), l.Id.ToString(), new { newStatus, reason });
        await db.SaveChangesAsync(ct);
        status.Invalidate(l.TenantId);
        return await ToDtoAsync(l, ct);
    }

    /// <summary>Transfer: closes this license (history preserved), revokes its devices and issues an equivalent license to the target tenant.</summary>
    public async Task<CreateLicenseResponse> TransferAsync(Guid id, TransferLicenseRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Reason, "reason", 500).ThrowIfInvalid();
        var l = await LoadAsync(id, ct);
        if (l.Status is LicenseStatus.Revoked or LicenseStatus.Transferred) throw new DomainException("licensing.closed", "This license is closed.");
        if (l.TenantId == req.TargetTenantId) throw ValidationException.For("targetTenantId", "Target must be a different tenant.");
        if (!await db.Set<Company>().AnyAsync(c => c.Id == req.TargetTenantId, ct)) throw ValidationException.For("targetTenantId", "Unknown tenant.");
        var key = LicenseKeys.New();
        var copy = new License
        {
            TenantId = req.TargetTenantId, KeyHash = LicenseKeys.Hash(key), KeyHint = LicenseKeys.Hint(key), Type = l.Type, PlanCode = l.PlanCode,
            MaxDevices = l.MaxDevices, StartsAt = clock.UtcNow, ExpiresAt = l.ExpiresAt, OfflineGraceDays = l.OfflineGraceDays,
            Entitlements = l.Entitlements, Notes = $"Transferred from license {l.Id}", Status = LicenseStatus.Active,
        };
        db.Set<License>().Add(copy);
        l.Status = LicenseStatus.Transferred; l.StatusReason = req.Reason; l.TransferredToLicenseId = copy.Id;
        await RevokeAllDevicesAsync(l, "license_transferred", ct);
        AddEvent(l, null, "license.transferred_out", $"to {req.TargetTenantId}: {req.Reason}");
        AddEvent(copy, null, "license.transferred_in", $"from {l.TenantId}");
        audit.Write("platform.license_transferred", nameof(License), l.Id.ToString(), new { req.TargetTenantId, req.Reason, newLicense = copy.Id });
        await db.SaveChangesAsync(ct);
        status.Invalidate(l.TenantId);
        status.Invalidate(copy.TenantId);
        return new CreateLicenseResponse(await ToDtoAsync(copy, ct), key);
    }

    public async Task<PagedResult<DeviceDto>> ListDevicesAsync(PageQuery q, Guid? tenantId, Guid? licenseId, CancellationToken ct)
    {
        var query = ctx.IsPlatformAdmin ? db.Set<Device>().IgnoreQueryFilters() : db.Set<Device>();
        if (tenantId is { } t) query = query.Where(d => d.TenantId == t);
        if (licenseId is { } lid) query = query.Where(d => d.LicenseId == lid);
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(d => d.DeviceName.ToUpper().Contains(q.Search.ToUpper()));
        var page = await query.OrderByDescending(d => d.Status == DeviceStatus.Active).ThenByDescending(d => d.LastSeenAt).ToPagedAsync(q, ct);
        var tenantIds = page.Items.Select(d => d.TenantId).Distinct().ToList();
        var names = await db.Set<Company>().Where(c => tenantIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return new PagedResult<DeviceDto>(page.Items.Select(d => ToDto(d, names.GetValueOrDefault(d.TenantId))).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    /// <summary>Deactivates a device and frees its seat. <paramref name="asReplaced"/> marks it as replaced (customer swaps hardware)
    /// instead of revoked; either way the device must activate again to obtain a new certificate.</summary>
    public async Task RevokeDeviceAsync(Guid deviceId, string reason, CancellationToken ct, bool asReplaced = false)
    {
        new Validator().NotEmpty(reason, "reason", 500).ThrowIfInvalid();
        var query = ctx.IsPlatformAdmin ? db.Set<Device>().IgnoreQueryFilters() : db.Set<Device>();
        var d = await query.SingleOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new NotFoundException("device.not_found", "Device not found.");
        if (d.Status != DeviceStatus.Active) return;
        d.Status = asReplaced ? DeviceStatus.Replaced : DeviceStatus.Revoked; d.RevokedAt = clock.UtcNow; d.RevokedReason = reason;
        var type = asReplaced ? "device.replaced" : "device.revoked";
        db.Set<LicenseEvent>().Add(new LicenseEvent { TenantId = d.TenantId, LicenseId = d.LicenseId, DeviceId = d.Id, Type = type, Details = reason, OccurredAt = clock.UtcNow });
        audit.Write(asReplaced ? "licensing.device_replaced" : "licensing.device_revoked", nameof(Device), d.Id.ToString(), new { reason });
        await db.SaveChangesAsync(ct);
        cache.Remove(ActivationService.DeviceCacheKey(d.Id));
    }

    /// <summary>Moves an active device to another usable license of the same tenant (seat check applies). The device picks up
    /// the new license on its next certificate refresh.</summary>
    public async Task ReassignDeviceAsync(Guid deviceId, Guid targetLicenseId, string reason, CancellationToken ct)
    {
        new Validator().NotEmpty(reason, "reason", 500).ThrowIfInvalid();
        var d = await db.Set<Device>().IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new NotFoundException("device.not_found", "Device not found.");
        if (d.Status != DeviceStatus.Active) throw new DomainException("device.inactive", "Only active devices can be reassigned.", 409);
        if (d.LicenseId == targetLicenseId) throw ValidationException.For("targetLicenseId", "The device already uses this license.");
        var target = await LoadAsync(targetLicenseId, ct);
        if (target.TenantId != d.TenantId) throw ValidationException.For("targetLicenseId", "The target license belongs to another tenant. Use a license transfer instead.");
        if (!target.IsUsable(clock.UtcNow)) throw new DomainException("licensing.unavailable", "The target license is not usable.", 409);
        var used = await db.Set<Device>().IgnoreQueryFilters().CountAsync(x => x.LicenseId == target.Id && x.Status == DeviceStatus.Active, ct);
        if (used >= target.MaxDevices) throw new ConflictException("licensing.seats_exhausted", "The target license has no free seat.");
        var from = d.LicenseId;
        d.LicenseId = target.Id;
        db.Set<LicenseEvent>().Add(new LicenseEvent { TenantId = d.TenantId, LicenseId = from, DeviceId = d.Id, Type = "device.reassigned_out", Details = $"to {target.Id}: {reason}", OccurredAt = clock.UtcNow });
        AddEvent(target, d.Id, "device.reassigned_in", $"from {from}: {reason}");
        audit.Write("licensing.device_reassigned", nameof(Device), d.Id.ToString(), new { fromLicense = from, toLicense = target.Id, reason });
        await db.SaveChangesAsync(ct);
        cache.Remove(ActivationService.DeviceCacheKey(d.Id));
        status.Invalidate(d.TenantId);
    }

    /// <summary>Demo license: trial plan, all features, short fixed duration.</summary>
    public Task<CreateLicenseResponse> IssueDemoAsync(Guid tenantId, int days, int maxDevices, string? notes, CancellationToken ct)
    {
        new Validator().Require(days is >= 1 and <= 60, "days", "Must be between 1 and 60.").Require(maxDevices is >= 1 and <= 10, "maxDevices", "Must be between 1 and 10.")
            .MaxLength(notes, "notes", 2000).ThrowIfInvalid();
        var now = clock.UtcNow;
        return CreateAsync(new CreateLicenseRequest(tenantId, nameof(LicenseType.Trial), "trial", maxDevices, now, now.AddDays(days), null, null,
            string.IsNullOrWhiteSpace(notes) ? "Demo license" : "Demo license — " + notes), ct);
    }

    /// <summary>Change the seat count; refuses to go below the number of active devices.</summary>
    public async Task<LicenseDto> ChangeSeatsAsync(Guid id, int maxDevices, string reason, CancellationToken ct)
    {
        new Validator().Require(maxDevices is >= 1 and <= 1000, "maxDevices", "Must be between 1 and 1000.").NotEmpty(reason, "reason", 500).ThrowIfInvalid();
        var l = await LoadAsync(id, ct);
        if (l.Status is LicenseStatus.Revoked or LicenseStatus.Transferred) throw new DomainException("licensing.closed", "This license is closed.");
        var active = await db.Set<Device>().IgnoreQueryFilters().CountAsync(d => d.LicenseId == l.Id && d.Status == DeviceStatus.Active, ct);
        if (maxDevices < active) throw new DomainException("licensing.seats_in_use", $"{active} devices are active; deactivate devices before reducing seats.", 409);
        var old = l.MaxDevices;
        l.MaxDevices = maxDevices;
        AddEvent(l, null, "license.seats_changed", $"{old} -> {maxDevices}: {reason}");
        audit.Write("platform.license_seats_changed", nameof(License), l.Id.ToString(), new { old, maxDevices, reason });
        await db.SaveChangesAsync(ct);
        status.Invalidate(l.TenantId);
        return await ToDtoAsync(l, ct);
    }

    /// <summary>Failed activation attempts recorded by <see cref="ActivationService"/> (audit trail; no key material).</summary>
    public async Task<PagedResult<Veltrix.Contracts.Operations.ActivationFailureDto>> ActivationFailuresAsync(PageQuery q, Guid? tenantId, Guid? licenseId, CancellationToken ct)
    {
        var query = db.Set<AuditLog>().AsNoTracking().Where(a => a.Action == ActivationService.ActivationFailedAction);
        if (tenantId is { } t) query = query.Where(a => a.TenantId == t);
        if (licenseId is { } lid) { var s = lid.ToString(); query = query.Where(a => a.EntityId == s); }
        var page = await query.OrderByDescending(a => a.OccurredAt).ToPagedAsync(q, ct);
        return new PagedResult<Veltrix.Contracts.Operations.ActivationFailureDto>(page.Items.Select(a =>
        {
            string code = "", platform = ""; Guid? installation = null;
            if (a.Details is not null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(a.Details);
                var r = doc.RootElement;
                if (r.TryGetProperty("code", out var c)) code = c.GetString() ?? "";
                if (r.TryGetProperty("platform", out var p)) platform = p.GetString() ?? "";
                if (r.TryGetProperty("installationId", out var i) && i.TryGetGuid(out var g)) installation = g;
            }
            return new Veltrix.Contracts.Operations.ActivationFailureDto(a.Id, a.TenantId, Guid.TryParse(a.EntityId, out var l) ? l : null, code, platform, installation,
                a.IpAddress, a.OccurredAt);
        }).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<IReadOnlyList<LicenseEventDto>> EventsAsync(Guid licenseId, CancellationToken ct)
    {
        var query = ctx.IsPlatformAdmin ? db.Set<LicenseEvent>().IgnoreQueryFilters() : db.Set<LicenseEvent>();
        return await query.Where(e => e.LicenseId == licenseId).OrderByDescending(e => e.OccurredAt).Take(500)
            .Select(e => new LicenseEventDto(e.Id, e.LicenseId, e.DeviceId, e.Type, e.Details, e.OccurredAt)).ToListAsync(ct);
    }

    /// <summary>Company view: own licenses with seat usage.</summary>
    public async Task<IReadOnlyList<LicenseSummaryDto>> MyLicensesAsync(CancellationToken ct)
    {
        ctx.RequireTenant();
        var now = clock.UtcNow;
        var licenses = await db.Set<License>().OrderByDescending(l => l.CreatedAt).ToListAsync(ct);
        var counts = await db.Set<Device>().Where(d => d.Status == DeviceStatus.Active).GroupBy(d => d.LicenseId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return licenses.Select(l => ActivationService.ToSummary(l, counts.GetValueOrDefault(l.Id), now)).ToList();
    }

    private async Task RevokeAllDevicesAsync(License l, string reason, CancellationToken ct)
    {
        var devices = await db.Set<Device>().IgnoreQueryFilters().Where(d => d.LicenseId == l.Id && d.Status == DeviceStatus.Active).ToListAsync(ct);
        foreach (var d in devices)
        {
            d.Status = DeviceStatus.Revoked; d.RevokedAt = clock.UtcNow; d.RevokedReason = reason;
            cache.Remove(ActivationService.DeviceCacheKey(d.Id));
        }
    }

    private async Task<License> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Set<License>().IgnoreQueryFilters().SingleOrDefaultAsync(l => l.Id == id, ct)
        ?? throw new NotFoundException("license.not_found", "License not found.");

    private void AddEvent(License license, Guid? deviceId, string type, string? details) =>
        db.Set<LicenseEvent>().Add(new LicenseEvent
        {
            TenantId = license.TenantId, LicenseId = license.Id, DeviceId = deviceId, Type = type, Details = details, OccurredAt = clock.UtcNow,
        });

    private async Task<LicenseDto> ToDtoAsync(License l, CancellationToken ct)
    {
        var active = await db.Set<Device>().IgnoreQueryFilters().CountAsync(d => d.LicenseId == l.Id && d.Status == DeviceStatus.Active, ct);
        var name = await db.Set<Company>().Where(c => c.Id == l.TenantId).Select(c => c.Name).SingleOrDefaultAsync(ct);
        var now = clock.UtcNow;
        return new LicenseDto(l.Id, l.TenantId, name, l.KeyHint, l.Type.ToString(), l.Status.ToString(), l.EffectiveStatus(now).ToString(),
            l.PlanCode, l.MaxDevices, active, l.StartsAt, l.ExpiresAt, l.OfflineGraceDays, l.EntitlementList, l.Notes, l.CreatedAt);
    }

    public static DeviceDto ToDto(Device d, string? tenantName) => new(d.Id, d.LicenseId, d.TenantId, tenantName, d.InstallationId, d.Platform,
        d.DeviceName, d.AppVersion, d.Status.ToString(), d.ActivatedAt, d.LastSeenAt, d.RevokedAt, d.RevokedReason, d.PublicKeyThumbprint);
}

/// <summary>Tenant access state derived from company status and license validity, cached briefly.
/// Expired licenses keep read-only access so business data is never locked away or deleted.</summary>
public sealed class LicenseTenantStatusProvider(DbContext db, IClock clock, IMemoryCache cache) : ITenantStatusProvider, IEntitlementChecker, IDeviceAuthorization
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    /// <summary>Days after expiry during which the tenant keeps full access (renewal grace).</summary>
    public const int RenewalGraceDays = 3;

    private sealed record Snapshot(TenantAccessStatus Status, HashSet<string> Features, int MaxDevices);

    private async Task<Snapshot> LoadAsync(Guid tenantId, CancellationToken ct) =>
        (await cache.GetOrCreateAsync("tenant-status:" + tenantId, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheTtl;
            var company = await db.Set<Company>().Where(c => c.Id == tenantId).Select(c => new { c.Status }).SingleOrDefaultAsync(ct);
            if (company is null) return new Snapshot(TenantAccessStatus.NotFound, [], 0);
            if (company.Status is CompanyStatus.Suspended or CompanyStatus.Closed) return new Snapshot(TenantAccessStatus.Suspended, [], 0);
            var now = clock.UtcNow;
            var licenses = await db.Set<License>().IgnoreQueryFilters().Where(l => l.TenantId == tenantId).ToListAsync(ct);
            var usable = licenses.Where(l => l.IsUsable(now)).ToList();
            if (usable.Count > 0)
            {
                var st = usable.All(l => l.Type == LicenseType.Trial) ? TenantAccessStatus.Trial : TenantAccessStatus.Active;
                return new Snapshot(st, AllFeatures(), usable.Sum(l => l.MaxDevices));
            }
            var grace = licenses.Where(l => l.Status == LicenseStatus.Active && l.ExpiresAt is { } x && x <= now && now < x.AddDays(RenewalGraceDays)).ToList();
            if (grace.Count > 0) return new Snapshot(TenantAccessStatus.GracePeriod, AllFeatures(), grace.Sum(l => l.MaxDevices));
            if (licenses.Any(l => l.Status == LicenseStatus.Suspended)) return new Snapshot(TenantAccessStatus.Suspended, [], 0);
            return new Snapshot(TenantAccessStatus.Expired, licenses.Count > 0 ? AllFeatures() : [], 0);
        }))!;

    /// <summary>Single plan: any licensed tenant is entitled to every feature (read-only enforcement is driven by status, not features).</summary>
    private static HashSet<string> AllFeatures() => [.. Features.All];

    public async Task<TenantAccessStatus> GetStatusAsync(Guid tenantId, CancellationToken ct = default) => (await LoadAsync(tenantId, ct)).Status;

    public void Invalidate(Guid tenantId) => cache.Remove("tenant-status:" + tenantId);

    public async Task<bool> HasFeatureAsync(Guid tenantId, string feature, CancellationToken ct = default) =>
        (await LoadAsync(tenantId, ct)).Features.Contains(feature);

    public Task<int?> GetLimitAsync(Guid tenantId, string limit, CancellationToken ct = default) => Task.FromResult<int?>(null);

    public async Task<bool> IsDeviceActiveAsync(Guid tenantId, Guid deviceId, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync(ActivationService.DeviceCacheKey(deviceId), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await db.Set<Device>().IgnoreQueryFilters().AnyAsync(d => d.Id == deviceId && d.TenantId == tenantId && d.Status == DeviceStatus.Active, ct);
        });
}
