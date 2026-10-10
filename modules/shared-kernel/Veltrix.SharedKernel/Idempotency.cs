namespace Veltrix.SharedKernel;

/// <summary>Stores the outcome of an idempotent operation so that retries (e.g. offline sync replays)
/// return the original result instead of executing twice.</summary>
public sealed class IdempotencyRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public string Key { get; set; } = "";
    public string Operation { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int StatusCode { get; set; }
    public string? ResponseJson { get; set; }
    public string? ResultEntityId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
