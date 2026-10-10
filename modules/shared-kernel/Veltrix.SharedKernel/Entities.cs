using System.ComponentModel.DataAnnotations;

namespace Veltrix.SharedKernel;

/// <summary>Base class for all persisted entities. Uses client-generated, stable GUID identifiers
/// so that offline clients can create records that synchronize without identity collisions.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>Optimistic concurrency token (mapped to PostgreSQL xmin).</summary>
    [Timestamp]
    public uint Version { get; set; }
}

/// <summary>Entity owned by a tenant (company). A global query filter isolates rows by tenant.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>Entity bound to a specific branch; branch-level authorization applies.</summary>
public interface IBranchScoped
{
    Guid BranchId { get; set; }
}

public abstract class TenantEntity : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
}

/// <summary>Finalized records (posted journals, completed sales, issued invoices) cannot be deleted.</summary>
public interface IImmutableWhenFinal
{
    bool IsFinal { get; }
}
