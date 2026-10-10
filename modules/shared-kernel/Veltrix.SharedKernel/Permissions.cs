namespace Veltrix.SharedKernel;

/// <summary>Fine-grained permission catalog. Permissions are enforced on the server for every endpoint,
/// and mirrored on clients to hide unavailable actions and to bound offline operations.</summary>
public static class Permissions
{
    public const string All = "*";

    // Platform (vendor) scope
    public const string PlatformTenants = "platform.tenants";
    public const string PlatformLicenses = "platform.licenses";
    public const string PlatformSubscriptions = "platform.subscriptions";
    public const string PlatformAdmins = "platform.admins";
    public const string PlatformAudit = "platform.audit";
    public const string PlatformReleases = "platform.releases";
    public const string PlatformSupport = "platform.support";
    public const string PlatformConfig = "platform.config";

    // Company scope
    public const string CompanyManage = "company.manage";
    public const string BranchesManage = "branches.manage";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string DevicesManage = "devices.manage";
    public const string SettingsManage = "settings.manage";
    public const string AuditView = "audit.view";
    public const string BackupManage = "backup.manage";

    public const string CatalogView = "catalog.view";
    public const string CatalogManage = "catalog.manage";
    public const string InventoryView = "inventory.view";
    public const string InventoryAdjust = "inventory.adjust";
    public const string InventoryTransfer = "inventory.transfer";
    public const string InventoryCount = "inventory.count";

    public const string PosSell = "pos.sell";
    public const string PosDiscount = "pos.discount";
    public const string PosRefund = "pos.refund";
    public const string PosVoid = "pos.void";
    public const string PosShiftManage = "pos.shift";
    public const string PosCashDrawer = "pos.drawer";

    public const string PurchasingView = "purchasing.view";
    public const string PurchasingManage = "purchasing.manage";
    public const string PurchasingApprove = "purchasing.approve";
    public const string SuppliersManage = "suppliers.manage";

    public const string CustomersView = "customers.view";
    public const string CustomersManage = "customers.manage";
    public const string CustomersExport = "customers.export";

    public const string SalesView = "sales.view";
    public const string SalesManage = "sales.manage";
    public const string SalesApprove = "sales.approve";

    public const string AccountingView = "accounting.view";
    public const string AccountingPost = "accounting.post";
    public const string AccountingClose = "accounting.close";
    public const string ExpensesManage = "expenses.manage";

    public const string HrView = "hr.view";
    public const string HrManage = "hr.manage";
    public const string HrSensitive = "hr.sensitive";
    public const string HrPayroll = "hr.payroll";
    public const string LeaveRequest = "hr.leave.request";
    public const string LeaveApprove = "hr.leave.approve";

    public const string ReportsSales = "reports.sales";
    public const string ReportsFinancial = "reports.financial";
    public const string ReportsInventory = "reports.inventory";
    public const string ReportsHr = "reports.hr";

    public const string NotificationsManage = "notifications.manage";
    public const string SyncUse = "sync.use";

    public static readonly IReadOnlyList<string> CompanyPermissions =
    [
        CompanyManage, BranchesManage, UsersManage, RolesManage, DevicesManage, SettingsManage, AuditView, BackupManage,
        CatalogView, CatalogManage, InventoryView, InventoryAdjust, InventoryTransfer, InventoryCount,
        PosSell, PosDiscount, PosRefund, PosVoid, PosShiftManage, PosCashDrawer,
        PurchasingView, PurchasingManage, PurchasingApprove, SuppliersManage,
        CustomersView, CustomersManage, CustomersExport,
        SalesView, SalesManage, SalesApprove,
        AccountingView, AccountingPost, AccountingClose, ExpensesManage,
        HrView, HrManage, HrSensitive, HrPayroll, LeaveRequest, LeaveApprove,
        ReportsSales, ReportsFinancial, ReportsInventory, ReportsHr,
        NotificationsManage, SyncUse,
    ];

    public static readonly IReadOnlyList<string> PlatformPermissions =
    [
        PlatformTenants, PlatformLicenses, PlatformSubscriptions, PlatformAdmins, PlatformAudit,
        PlatformReleases, PlatformSupport, PlatformConfig,
    ];
}

/// <summary>Built-in role templates. Companies may define custom roles in addition to these.</summary>
public static class SystemRoles
{
    public const string PlatformSuperAdmin = "platform_super_admin";
    public const string CompanyOwner = "company_owner";
    public const string CompanyAdmin = "company_admin";
    public const string BranchManager = "branch_manager";
    public const string Accountant = "accountant";
    public const string Cashier = "cashier";
    public const string WarehouseManager = "warehouse_manager";
    public const string SalesRep = "sales_rep";
    public const string HrManager = "hr_manager";
    public const string Auditor = "auditor";
    public const string ReadOnly = "read_only";

    private static readonly string[] ViewAll =
    [
        Permissions.CatalogView, Permissions.InventoryView, Permissions.PurchasingView, Permissions.CustomersView,
        Permissions.SalesView, Permissions.AccountingView, Permissions.ReportsSales, Permissions.ReportsInventory,
    ];

    public static readonly IReadOnlyDictionary<string, string[]> Templates = new Dictionary<string, string[]>
    {
        [CompanyOwner] = [Permissions.All],
        [CompanyAdmin] = Permissions.CompanyPermissions.Where(p => p != Permissions.HrSensitive && p != Permissions.HrPayroll).ToArray(),
        [BranchManager] =
        [
            .. ViewAll, Permissions.CatalogManage, Permissions.InventoryAdjust, Permissions.InventoryTransfer, Permissions.InventoryCount,
            Permissions.PosSell, Permissions.PosDiscount, Permissions.PosRefund, Permissions.PosVoid, Permissions.PosShiftManage,
            Permissions.PosCashDrawer, Permissions.PurchasingManage, Permissions.CustomersManage, Permissions.SalesManage,
            Permissions.SalesApprove, Permissions.ExpensesManage, Permissions.HrView, Permissions.LeaveApprove, Permissions.ReportsHr,
            Permissions.SyncUse, Permissions.LeaveRequest,
        ],
        [Accountant] =
        [
            .. ViewAll, Permissions.AccountingPost, Permissions.AccountingClose, Permissions.ExpensesManage,
            Permissions.ReportsFinancial, Permissions.PurchasingManage, Permissions.SuppliersManage, Permissions.SyncUse,
            Permissions.LeaveRequest,
        ],
        [Cashier] =
        [
            Permissions.CatalogView, Permissions.CustomersView, Permissions.PosSell, Permissions.PosShiftManage,
            Permissions.PosCashDrawer, Permissions.SyncUse, Permissions.LeaveRequest,
        ],
        [WarehouseManager] =
        [
            Permissions.CatalogView, Permissions.CatalogManage, Permissions.InventoryView, Permissions.InventoryAdjust,
            Permissions.InventoryTransfer, Permissions.InventoryCount, Permissions.PurchasingView, Permissions.PurchasingManage,
            Permissions.ReportsInventory, Permissions.SyncUse, Permissions.LeaveRequest,
        ],
        [SalesRep] =
        [
            Permissions.CatalogView, Permissions.InventoryView, Permissions.CustomersView, Permissions.CustomersManage,
            Permissions.SalesView, Permissions.SalesManage, Permissions.PosSell, Permissions.ReportsSales, Permissions.SyncUse,
            Permissions.LeaveRequest,
        ],
        [HrManager] =
        [
            Permissions.HrView, Permissions.HrManage, Permissions.HrSensitive, Permissions.HrPayroll, Permissions.LeaveApprove,
            Permissions.LeaveRequest, Permissions.ReportsHr,
        ],
        [Auditor] = [.. ViewAll, Permissions.AuditView, Permissions.ReportsFinancial, Permissions.ReportsHr, Permissions.HrView],
        [ReadOnly] = ViewAll,
    };
}
