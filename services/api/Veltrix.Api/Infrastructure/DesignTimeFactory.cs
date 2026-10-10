using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Veltrix.SharedKernel;

namespace Veltrix.Api.Infrastructure;

/// <summary>Used by `dotnet ef` to build the model for migrations.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<VeltrixDbContext>
{
    public VeltrixDbContext CreateDbContext(string[] args)
    {
        VeltrixDbContext.ModuleAssemblies.Clear();
        VeltrixDbContext.ModuleAssemblies.AddRange(ModuleCatalog.All.Select(m => m.GetType().Assembly));
        var cs = Environment.GetEnvironmentVariable("VELTRIX_DESIGN_CONNECTION") ?? "Host=localhost;Database=veltrix_design;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<VeltrixDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "public")).Options;
        return new VeltrixDbContext(options, new TenantContext(), new SystemClock());
    }
}
