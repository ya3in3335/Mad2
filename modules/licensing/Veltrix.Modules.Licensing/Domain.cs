using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Licensing;

public enum LicenseType { Trial, Monthly, Quarterly, Annual, Perpetual, Enterprise, Custom }
/// <summary>Stored status. "Expiring" and "Expired" are derived from dates (see <see cref="License.EffectiveStatus"/>).</summary>
public enum LicenseStatus { Pending, Active, Suspended, Revoked, Transferred }
public enum EffectiveLicenseStatus { Pending, Active, Expiring, Expired, Suspended, Revoked, Transferred }
public enum DeviceStatus { Active, Revoked, Replaced, Released }

public sealed class License : TenantEntity
{
    public string KeyHash { get; set; } = "";
    public string KeyHint { get; set; } = "";
    public LicenseType Type { get; set; }
    public LicenseStatus Status { get; set; } = LicenseStatus.Active;
    public string PlanCode { get; set; } = "starter";
    public int MaxDevices { get; set; } = 1;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int OfflineGraceDays { get; set; } = 7;
    /// <summary>Comma separated feature codes.</summary>
    public string Entitlements { get; set; } = "";
    public string? Notes { get; set; }
    public string? StatusReason { get; set; }
    public Guid? TransferredToLicenseId { get; set; }

    public IReadOnlyList<string> EntitlementList => Entitlements.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public const int ExpiringWindowDays = 14;

    public EffectiveLicenseStatus EffectiveStatus(DateTimeOffset now) => Status switch
    {
        LicenseStatus.Suspended => EffectiveLicenseStatus.Suspended,
        LicenseStatus.Revoked => EffectiveLicenseStatus.Revoked,
        LicenseStatus.Transferred => EffectiveLicenseStatus.Transferred,
        LicenseStatus.Pending => EffectiveLicenseStatus.Pending,
        _ when now < StartsAt => EffectiveLicenseStatus.Pending,
        _ when ExpiresAt is { } e && now >= e => EffectiveLicenseStatus.Expired,
        _ when ExpiresAt is { } e && now >= e.AddDays(-ExpiringWindowDays) => EffectiveLicenseStatus.Expiring,
        _ => EffectiveLicenseStatus.Active,
    };

    public bool IsUsable(DateTimeOffset now) => EffectiveStatus(now) is EffectiveLicenseStatus.Active or EffectiveLicenseStatus.Expiring;
}

public sealed class Device : TenantEntity
{
    public Guid LicenseId { get; set; }
    public Guid InstallationId { get; set; }
    public string Platform { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string AppVersion { get; set; } = "";
    /// <summary>SubjectPublicKeyInfo (base64) of the device's ECDSA P-256 key. The private key never leaves the device.</summary>
    public string PublicKey { get; set; } = "";
    public string PublicKeyThumbprint { get; set; } = "";
    /// <summary>Pseudonymous, salted hash of permitted device characteristics. Never raw identifiers.</summary>
    public string FingerprintHash { get; set; } = "";
    public int FingerprintVersion { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Active;
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public Guid? ReplacedByDeviceId { get; set; }
}

/// <summary>Single-use server nonce for challenge-response device authentication (prevents replay).</summary>
public sealed class ActivationChallenge
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid InstallationId { get; set; }
    public string Purpose { get; set; } = "";
    public string Nonce { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class LicenseEvent : TenantEntity
{
    public Guid LicenseId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Type { get; set; } = "";
    public string? Details { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class LicenseConfig : IEntityTypeConfiguration<License>
{
    public void Configure(EntityTypeBuilder<License> b)
    {
        b.ToTable("licenses", "licensing");
        b.HasIndex(x => x.KeyHash).IsUnique();
        b.Property(x => x.KeyHash).HasMaxLength(128);
        b.Property(x => x.KeyHint).HasMaxLength(10);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PlanCode).HasMaxLength(40);
        b.Property(x => x.Entitlements).HasMaxLength(1000);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.StatusReason).HasMaxLength(500);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.EntitlementList);
    }
}

internal sealed class DeviceConfig : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.ToTable("devices", "licensing");
        b.HasIndex(x => new { x.LicenseId, x.Status });
        b.HasIndex(x => x.InstallationId);
        b.HasIndex(x => x.PublicKeyThumbprint);
        b.Property(x => x.Platform).HasMaxLength(20);
        b.Property(x => x.DeviceName).HasMaxLength(120);
        b.Property(x => x.AppVersion).HasMaxLength(40);
        b.Property(x => x.PublicKey).HasMaxLength(500);
        b.Property(x => x.PublicKeyThumbprint).HasMaxLength(64);
        b.Property(x => x.FingerprintHash).HasMaxLength(128);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RevokedReason).HasMaxLength(500);
        b.HasOne<License>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ActivationChallengeConfig : IEntityTypeConfiguration<ActivationChallenge>
{
    public void Configure(EntityTypeBuilder<ActivationChallenge> b)
    {
        b.ToTable("activation_challenges", "licensing");
        b.HasIndex(x => x.ExpiresAt);
        b.Property(x => x.Purpose).HasMaxLength(20);
        b.Property(x => x.Nonce).HasMaxLength(64);
    }
}

internal sealed class LicenseEventConfig : IEntityTypeConfiguration<LicenseEvent>
{
    public void Configure(EntityTypeBuilder<LicenseEvent> b)
    {
        b.ToTable("license_events", "licensing");
        b.HasIndex(x => new { x.LicenseId, x.OccurredAt });
        b.Property(x => x.Type).HasMaxLength(60);
        b.Property(x => x.Details).HasMaxLength(2000);
    }
}
