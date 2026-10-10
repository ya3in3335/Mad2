using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.SharedKernel;

namespace Veltrix.Api.Infrastructure;

internal sealed class AuditLogConfig : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_logs", "audit");
        b.HasIndex(x => new { x.TenantId, x.OccurredAt });
        b.HasIndex(x => new { x.EntityType, x.EntityId });
        b.Property(x => x.Action).HasMaxLength(100);
        b.Property(x => x.EntityType).HasMaxLength(100);
        b.Property(x => x.EntityId).HasMaxLength(100);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.Property(x => x.Details).HasColumnType("jsonb");
    }
}

internal sealed class IdempotencyConfig : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("idempotency_records", "sync");
        b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Operation).HasMaxLength(64);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.ResultEntityId).HasMaxLength(64);
    }
}
