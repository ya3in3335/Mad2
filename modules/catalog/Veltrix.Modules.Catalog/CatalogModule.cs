using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Catalog;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Catalog;

public sealed class CatalogModule : IModule
{
    public string Name => "catalog";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<CatalogService>();
        services.AddScoped<PricingService>();
        services.AddScoped<IOnboardingContributor, CatalogOnboardingContributor>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapModuleGroup("catalog", "Catalog").RequirePermission(Permissions.CatalogView);
        g.MapGet("/products", ([AsParameters] PageQuery q, Guid? categoryId, string? status, CatalogService s, CancellationToken ct) =>
            s.ListAsync(q, categoryId, status, null, ct));
        g.MapGet("/products/{id:guid}", (Guid id, CatalogService s, CancellationToken ct) => s.GetAsync(id, ct));
        g.MapGet("/products/by-code/{code}", async (string code, CatalogService s, CancellationToken ct) =>
            await s.FindByCodeAsync(code, ct) is { } p ? Results.Ok(p) : Results.NotFound());
        g.MapPost("/products", (UpsertProductRequest r, CatalogService s, CancellationToken ct) => s.UpsertAsync(null, r, ct)).RequirePermission(Permissions.CatalogManage);
        g.MapPut("/products/{id:guid}", (Guid id, UpsertProductRequest r, CatalogService s, CancellationToken ct) => s.UpsertAsync(id, r, ct)).RequirePermission(Permissions.CatalogManage);
        g.MapPost("/products/bulk", (BulkEditRequest r, CatalogService s, CancellationToken ct) => s.BulkEditAsync(r, ct)).RequirePermission(Permissions.CatalogManage);
        g.MapGet("/products/export", async (CatalogService s, CancellationToken ct) =>
            Results.File(await s.ExportCsvAsync(ct), "text/csv; charset=utf-8", "products.csv"));
        g.MapPost("/products/import", async (HttpRequest req, CatalogService s, CancellationToken ct) =>
        {
            if (!req.HasFormContentType) throw ValidationException.For("file", "Upload a CSV file as multipart/form-data.");
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw ValidationException.For("file", "Missing file.");
            if (file.Length > 10 * 1024 * 1024) throw ValidationException.For("file", "File exceeds 10 MB.");
            await using var stream = file.OpenReadStream();
            return await s.ImportCsvAsync(stream, ct);
        }).RequirePermission(Permissions.CatalogManage).DisableAntiforgery();
        g.MapPut("/products/{id:guid}/image", async (Guid id, HttpRequest req, CatalogService s, CancellationToken ct) =>
        {
            if (!req.HasFormContentType) throw ValidationException.For("file", "Upload an image as multipart/form-data.");
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw ValidationException.For("file", "Missing file.");
            await using var stream = file.OpenReadStream();
            await s.SetImageAsync(id, stream, file.ContentType, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.CatalogManage).DisableAntiforgery();
        g.MapGet("/products/{id:guid}/image", async (Guid id, CatalogService s, CancellationToken ct) =>
            await s.GetImageAsync(id, ct) is { } img ? Results.Stream(img.Stream, img.ContentType) : Results.NotFound());

        g.MapGet("/categories", (CatalogService s, CancellationToken ct) => s.CategoriesAsync(ct));
        g.MapPost("/categories", (UpsertCategoryRequest r, CatalogService s, CancellationToken ct) => s.UpsertCategoryAsync(null, r, ct)).RequirePermission(Permissions.CatalogManage);
        g.MapPut("/categories/{id:guid}", (Guid id, UpsertCategoryRequest r, CatalogService s, CancellationToken ct) => s.UpsertCategoryAsync(id, r, ct)).RequirePermission(Permissions.CatalogManage);
        g.MapGet("/brands", (CatalogService s, CancellationToken ct) => s.BrandsAsync(ct));
        g.MapPost("/brands", (UpsertBrandRequest r, CatalogService s, CancellationToken ct) => s.UpsertBrandAsync(null, r, ct)).RequirePermission(Permissions.CatalogManage);
        g.MapPut("/brands/{id:guid}", (Guid id, UpsertBrandRequest r, CatalogService s, CancellationToken ct) => s.UpsertBrandAsync(id, r, ct)).RequirePermission(Permissions.CatalogManage);
        g.MapGet("/units", (CatalogService s, CancellationToken ct) => s.UnitsAsync(ct));
        g.MapPost("/units", (UpsertUnitRequest r, CatalogService s, CancellationToken ct) => s.UpsertUnitAsync(null, r, ct)).RequirePermission(Permissions.CatalogManage);
        g.MapPut("/units/{id:guid}", (Guid id, UpsertUnitRequest r, CatalogService s, CancellationToken ct) => s.UpsertUnitAsync(id, r, ct)).RequirePermission(Permissions.CatalogManage);
        g.MapGet("/tax-categories", (CatalogService s, CancellationToken ct) => s.TaxCategoriesAsync(ct));
        g.MapPost("/tax-categories", (UpsertTaxCategoryRequest r, CatalogService s, CancellationToken ct) => s.UpsertTaxCategoryAsync(null, r, ct)).RequirePermission(Permissions.SettingsManage);
        g.MapPut("/tax-categories/{id:guid}", (Guid id, UpsertTaxCategoryRequest r, CatalogService s, CancellationToken ct) => s.UpsertTaxCategoryAsync(id, r, ct)).RequirePermission(Permissions.SettingsManage);
        g.MapGet("/price-lists", (CatalogService s, CancellationToken ct) => s.PriceListsAsync(ct));
        g.MapPost("/price-lists", (UpsertPriceListRequest r, CatalogService s, CancellationToken ct) => s.UpsertPriceListAsync(null, r, ct)).RequirePermission(Permissions.CatalogManage);
        g.MapPut("/price-lists/{id:guid}", (Guid id, UpsertPriceListRequest r, CatalogService s, CancellationToken ct) => s.UpsertPriceListAsync(id, r, ct)).RequirePermission(Permissions.CatalogManage);
    }
}
