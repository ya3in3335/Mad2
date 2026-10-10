namespace Veltrix.SharedKernel;

/// <summary>Append-only audit trail. Never stores passwords, tokens, keys or payment card data.</summary>
public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    /// <summary>JSON details (sanitized).</summary>
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public interface IAuditWriter
{
    /// <summary>Stage an audit record in the current unit of work (persisted with the business change).</summary>
    void Write(string action, string entityType, string? entityId, object? details = null);
}
