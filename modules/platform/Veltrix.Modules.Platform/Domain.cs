using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Platform;

public enum SupportStatus { Open, Pending, Resolved, Closed }
public enum SupportPriority { Low, Normal, High, Urgent }

/// <summary>Support case between a tenant and the vendor. Not tenant-filtered: access is checked explicitly.</summary>
public sealed class SupportCase : Entity
{
    public Guid? TenantId { get; set; }
    public string Subject { get; set; } = "";
    public SupportStatus Status { get; set; }
    public SupportPriority Priority { get; set; }
    public List<SupportMessage> Messages { get; set; } = new();
}

public sealed class SupportMessage : Entity
{
    public Guid CaseId { get; set; }
    public Guid AuthorId { get; set; }
    public bool FromVendor { get; set; }
    public string Body { get; set; } = "";
}

public sealed class AppRelease : Entity
{
    public string Platform { get; set; } = "";
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public string MinSupportedVersion { get; set; } = "0.0.0";
    public string? ReleaseNotes { get; set; }
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset PublishedAt { get; set; }
}

public sealed class Announcement : Entity
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public NotificationSeverity Severity { get; set; }
    public Guid? TenantId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public bool Published { get; set; }
}

public sealed class PlatformSetting : Entity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class BackupRecord : Entity
{
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public bool Verified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? VerificationDetails { get; set; }
}

public sealed class WorkerHeartbeat
{
    public string Name { get; set; } = "";
    public DateTimeOffset LastRunAt { get; set; }
    public string LastStatus { get; set; } = "";
    public string? LastError { get; set; }
}

internal sealed class PlatformConfigs : IEntityTypeConfiguration<SupportCase>, IEntityTypeConfiguration<SupportMessage>, IEntityTypeConfiguration<AppRelease>,
    IEntityTypeConfiguration<Announcement>, IEntityTypeConfiguration<PlatformSetting>, IEntityTypeConfiguration<BackupRecord>, IEntityTypeConfiguration<WorkerHeartbeat>
{
    public void Configure(EntityTypeBuilder<SupportCase> b)
    {
        b.ToTable("support_cases", "platform");
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.Property(x => x.Subject).HasMaxLength(200);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
        b.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<SupportMessage> b) { b.ToTable("support_messages", "platform"); b.Property(x => x.Body).HasMaxLength(8000); }

    public void Configure(EntityTypeBuilder<AppRelease> b)
    {
        b.ToTable("releases", "platform");
        b.HasIndex(x => new { x.Platform, x.Channel, x.Version }).IsUnique();
        b.Property(x => x.Platform).HasMaxLength(20);
        b.Property(x => x.Version).HasMaxLength(30);
        b.Property(x => x.Channel).HasMaxLength(20);
        b.Property(x => x.MinSupportedVersion).HasMaxLength(30);
        b.Property(x => x.ReleaseNotes).HasMaxLength(8000);
        b.Property(x => x.DownloadUrl).HasMaxLength(500);
        b.Property(x => x.Sha256).HasMaxLength(64);
    }

    public void Configure(EntityTypeBuilder<Announcement> b)
    {
        b.ToTable("announcements", "platform");
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Body).HasMaxLength(4000);
        b.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
    }

    public void Configure(EntityTypeBuilder<PlatformSetting> b)
    {
        b.ToTable("settings", "platform");
        b.HasIndex(x => x.Key).IsUnique();
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Value).HasMaxLength(4000);
    }

    public void Configure(EntityTypeBuilder<BackupRecord> b)
    {
        b.ToTable("backups", "platform");
        b.Property(x => x.FileName).HasMaxLength(300);
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.Status).HasMaxLength(20);
        b.Property(x => x.Error).HasMaxLength(2000);
        b.Property(x => x.VerificationDetails).HasMaxLength(2000);
    }

    public void Configure(EntityTypeBuilder<WorkerHeartbeat> b)
    {
        b.ToTable("worker_heartbeats", "platform");
        b.HasKey(x => x.Name);
        b.Property(x => x.Name).HasMaxLength(60);
        b.Property(x => x.LastStatus).HasMaxLength(20);
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}
