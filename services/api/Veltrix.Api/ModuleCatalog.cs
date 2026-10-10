using Veltrix.SharedKernel;

namespace Veltrix.Api;

/// <summary>All modules composed into the API host, in dependency order.</summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<IModule> All { get; } =
    [
        new Veltrix.Modules.Tenants.TenantsModule(),
        new Veltrix.Modules.Identity.IdentityModule(),
        new Veltrix.Modules.Licensing.LicensingModule(),
    ];
}
