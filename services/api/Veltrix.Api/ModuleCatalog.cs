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
        new Veltrix.Modules.Notifications.NotificationsModule(),
        new Veltrix.Modules.Catalog.CatalogModule(),
        new Veltrix.Modules.Accounting.AccountingModule(),
        new Veltrix.Modules.Inventory.InventoryModule(),
        new Veltrix.Modules.Crm.CrmModule(),
        new Veltrix.Modules.Pos.PosModule(),
        new Veltrix.Modules.Purchasing.PurchasingModule(),
        new Veltrix.Modules.Sales.SalesModule(),
        new Veltrix.Modules.Hr.HrModule(),
        new Veltrix.Modules.Reporting.ReportingModule(),
        new Veltrix.Modules.Synchronization.SynchronizationModule(),
        new Veltrix.Modules.Subscriptions.SubscriptionsModule(),
        new Veltrix.Modules.Platform.PlatformModule(),
        new Veltrix.Modules.Operations.OperationsModule(),
        new Veltrix.Modules.Telegram.TelegramModule(),
    ];
}
