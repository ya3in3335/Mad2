using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Operations;

public static class IncidentStatuses
{
    public const string Open = "open";
    public const string Acknowledged = "acknowledged";
    public const string Resolved = "resolved";
    public static readonly IReadOnlyList<string> All = [Open, Acknowledged, Resolved];
}

/// <summary>A group of identical errors (same fingerprint) for one tenant (or the platform itself when TenantId is null).
/// Vendor-only data: deliberately NOT tenant-owned, so tenant users can never query it.</summary>
public sealed class ErrorIncident : Entity
{
    public string Fingerprint { get; set; } = "";
    public Guid? TenantId { get; set; }
    /// <summary>client or server.</summary>
    public string Source { get; set; } = "client";
    public string App { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Category { get; set; } = "";
    public string ErrorType { get; set; } = "";
    public string Severity { get; set; } = DiagnosticSeverities.Error;
    public string Status { get; set; } = IncidentStatuses.Open;
    public string Title { get; set; } = "";
    public string? SampleStackTrace { get; set; }
    public int OccurrenceCount { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public Guid? LastDeviceId { get; set; }
    public string? StatusNote { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    /// <summary>Highest repeated-crash alert threshold already raised (avoids re-alerting).</summary>
    public int AlertedThreshold { get; set; }
}

/// <summary>One sanitized occurrence of an incident.</summary>
public sealed class ErrorEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid IncidentId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? UserId { get; set; }
    public string? OsVersion { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Sanitized JSON object of client-provided context.</summary>
    public string? Context { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public static class DiagnosticRequestStatuses
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Uploaded = "uploaded";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
}

/// <summary>Vendor request for a sanitized diagnostic report from one device. Delivered on the heartbeat response only when the
/// tenant granted consent; it never triggers remote command execution.</summary>
public sealed class DiagnosticRequest : Entity
{
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string Status { get; set; } = DiagnosticRequestStatuses.Pending;
    public string? Reason { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
    public string? AppVersion { get; set; }
    public string? OsVersion { get; set; }
    /// <summary>Sanitized JSON: { facts: {..}, lines: [..] }.</summary>
    public string? ReportJson { get; set; }
}

internal sealed class OperationsConfigs : IEntityTypeConfiguration<ErrorIncident>, IEntityTypeConfiguration<ErrorEvent>, IEntityTypeConfiguration<DiagnosticRequest>
{
    public void Configure(EntityTypeBuilder<ErrorIncident> b)
    {
        b.ToTable("incidents", "diagnostics");
        b.HasIndex(x => new { x.Fingerprint, x.TenantId }).IsUnique();
        b.HasIndex(x => new { x.Status, x.LastSeenAt });
        b.HasIndex(x => x.TenantId);
        b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.Source).HasMaxLength(10);
        b.Property(x => x.App).HasMaxLength(40);
        b.Property(x => x.AppVersion).HasMaxLength(40);
        b.Property(x => x.Platform).HasMaxLength(20);
        b.Property(x => x.Category).HasMaxLength(30);
        b.Property(x => x.ErrorType).HasMaxLength(120);
        b.Property(x => x.Severity).HasMaxLength(10);
        b.Property(x => x.Status).HasMaxLength(15);
        b.Property(x => x.Title).HasMaxLength(500);
        b.Property(x => x.SampleStackTrace).HasMaxLength(16000);
        b.Property(x => x.StatusNote).HasMaxLength(1000);
    }

    public void Configure(EntityTypeBuilder<ErrorEvent> b)
    {
        b.ToTable("error_events", "diagnostics");
        b.HasIndex(x => new { x.IncidentId, x.OccurredAt });
        b.HasIndex(x => x.DeviceId);
        b.Property(x => x.OsVersion).HasMaxLength(60);
        b.Property(x => x.Message).HasMaxLength(2000);
        b.Property(x => x.Context).HasMaxLength(8000);
        b.HasOne<ErrorIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<DiagnosticRequest> b)
    {
        b.ToTable("diagnostic_requests", "diagnostics");
        b.HasIndex(x => new { x.DeviceId, x.Status });
        b.Property(x => x.Status).HasMaxLength(15);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.AppVersion).HasMaxLength(40);
        b.Property(x => x.OsVersion).HasMaxLength(60);
        b.Property(x => x.ReportJson).HasColumnType("text");
    }
}
