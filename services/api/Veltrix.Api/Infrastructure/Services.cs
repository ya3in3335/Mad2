using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Veltrix.SharedKernel;

namespace Veltrix.Api.Infrastructure;

/// <summary>Stages audit rows in the same DbContext so they commit atomically with the business change.</summary>
public sealed class AuditWriter(VeltrixDbContext db, ITenantContext ctx, IClock clock, IHttpContextAccessor http) : IAuditWriter
{
    private static readonly string[] SensitiveKeys = ["password", "token", "secret", "key", "pin", "card", "cvv"];

    public void Write(string action, string entityType, string? entityId, object? details = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = ctx.TenantId, UserId = ctx.UserId, DeviceId = ctx.DeviceId, Action = action, EntityType = entityType,
            EntityId = entityId, Details = details is null ? null : Sanitize(details), OccurredAt = clock.UtcNow,
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(), CorrelationId = ctx.CorrelationId,
        });
    }

    /// <summary>Serialize details while redacting any property whose name suggests a credential.</summary>
    public static string Sanitize(object details)
    {
        var json = JsonSerializer.SerializeToNode(details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Redact(json);
        return json?.ToJsonString() ?? "null";
    }

    private static void Redact(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                if (SensitiveKeys.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase)) && !key.EndsWith("Id", StringComparison.Ordinal))
                    obj[key] = "***";
                else Redact(obj[key]);
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arr)
            foreach (var item in arr) Redact(item);
    }
}
