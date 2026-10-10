using Microsoft.EntityFrameworkCore;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Accounting;

/// <summary>Default chart of accounts inspired by the Algerian SCF numbering. It is a starting template that the
/// company's accountant must review; it does not constitute a claim of regulatory compliance.</summary>
public static class DefaultChart
{
    public sealed record Def(string Code, string Fr, string Ar, string En, AccountType Type, string? Role, string? Parent, bool Postable = true);

    public static readonly IReadOnlyList<Def> Accounts =
    [
        new("1", "Capitaux propres", "رؤوس الأموال", "Equity", AccountType.Equity, null, null, false),
        new("101", "Capital social", "رأس المال", "Share capital", AccountType.Equity, AccountRoles.Equity, "1"),
        new("106", "Réserves", "الاحتياطات", "Reserves", AccountType.Equity, null, "1"),
        new("110", "Report à nouveau", "الترحيل من جديد", "Retained earnings", AccountType.Equity, AccountRoles.RetainedEarnings, "1"),
        new("3", "Stocks", "المخزونات", "Inventories", AccountType.Asset, null, null, false),
        new("30", "Stocks de marchandises", "مخزون البضائع", "Merchandise inventory", AccountType.Asset, AccountRoles.Inventory, "3"),
        new("4", "Comptes de tiers", "حسابات الغير", "Third parties", AccountType.Asset, null, null, false),
        new("401", "Fournisseurs", "الموردون", "Accounts payable", AccountType.Liability, AccountRoles.AccountsPayable, "4"),
        new("408", "Fournisseurs - factures non parvenues", "موردون - فواتير لم تصل", "Goods received not invoiced", AccountType.Liability, AccountRoles.GoodsReceivedNotInvoiced, "4"),
        new("409", "Fournisseurs débiteurs (avances)", "موردون مدينون", "Supplier advances", AccountType.Asset, AccountRoles.SupplierAdvances, "4"),
        new("411", "Clients", "الزبائن", "Accounts receivable", AccountType.Asset, AccountRoles.AccountsReceivable, "4"),
        new("419", "Clients créditeurs (avances)", "زبائن دائنون", "Customer advances", AccountType.Liability, AccountRoles.CustomerAdvances, "4"),
        new("421", "Personnel - rémunérations dues", "المستخدمون - أجور مستحقة", "Salaries payable", AccountType.Liability, AccountRoles.SalariesPayable, "4"),
        new("431", "Organismes sociaux", "الهيئات الاجتماعية", "Payroll deductions payable", AccountType.Liability, AccountRoles.PayrollDeductionsPayable, "4"),
        new("4456", "TVA déductible", "الرسم على القيمة المضافة القابل للخصم", "Input VAT", AccountType.Asset, AccountRoles.VatDeductible, "4"),
        new("4457", "TVA collectée", "الرسم على القيمة المضافة المحصل", "Output VAT", AccountType.Liability, AccountRoles.VatCollected, "4"),
        new("5", "Comptes financiers", "الحسابات المالية", "Financial accounts", AccountType.Asset, null, null, false),
        new("512", "Banque", "البنك", "Bank", AccountType.Asset, AccountRoles.Bank, "5"),
        new("530", "Caisse", "الصندوق", "Cash", AccountType.Asset, AccountRoles.Cash, "5"),
        new("581", "Paiements par carte en cours", "مدفوعات البطاقات قيد التحصيل", "Card payments clearing", AccountType.Asset, AccountRoles.CardClearing, "5"),
        new("6", "Charges", "الأعباء", "Expenses", AccountType.Expense, null, null, false),
        new("600", "Achats de marchandises vendues", "مشتريات البضائع المبيعة", "Cost of goods sold", AccountType.Expense, AccountRoles.CostOfGoodsSold, "6"),
        new("613", "Locations", "الإيجارات", "Rent", AccountType.Expense, null, "6"),
        new("615", "Entretien et réparations", "الصيانة والإصلاحات", "Maintenance", AccountType.Expense, null, "6"),
        new("616", "Primes d'assurances", "أقساط التأمين", "Insurance", AccountType.Expense, null, "6"),
        new("624", "Transports", "النقل", "Transport", AccountType.Expense, null, "6"),
        new("626", "Frais postaux et télécommunications", "البريد والاتصالات", "Telecom", AccountType.Expense, null, "6"),
        new("627", "Services bancaires", "الخدمات المصرفية", "Bank charges", AccountType.Expense, null, "6"),
        new("628", "Charges diverses de gestion", "أعباء تسيير متنوعة", "General expenses", AccountType.Expense, AccountRoles.GeneralExpense, "6"),
        new("631", "Rémunérations du personnel", "أجور المستخدمين", "Salaries", AccountType.Expense, AccountRoles.Salaries, "6"),
        new("6581", "Écarts d'inventaire", "فروقات الجرد", "Inventory adjustments", AccountType.Expense, AccountRoles.InventoryAdjustment, "6"),
        new("6582", "Écarts sur prix d'achat", "فروقات أسعار الشراء", "Purchase price variance", AccountType.Expense, AccountRoles.PurchasePriceVariance, "6"),
        new("6583", "Écarts de caisse", "فروقات الصندوق", "Cash over/short", AccountType.Expense, AccountRoles.CashOverShort, "6"),
        new("6584", "Écarts d'arrondi", "فروقات التقريب", "Rounding differences", AccountType.Expense, AccountRoles.Rounding, "6"),
        new("7", "Produits", "المنتوجات", "Revenue", AccountType.Revenue, null, null, false),
        new("700", "Ventes de marchandises", "مبيعات البضائع", "Sales of goods", AccountType.Revenue, AccountRoles.SalesRevenue, "7"),
        new("706", "Prestations de services", "تقديم الخدمات", "Services revenue", AccountType.Revenue, AccountRoles.ServiceRevenue, "7"),
        new("709", "Retours et rabais sur ventes", "مردودات وتخفيضات المبيعات", "Sales returns & allowances", AccountType.Revenue, AccountRoles.SalesReturns, "7"),
        new("758", "Autres produits de gestion", "منتوجات تسيير أخرى", "Other income", AccountType.Revenue, null, "7"),
    ];
}

public sealed class AccountingOnboardingContributor(DbContext db) : IOnboardingContributor
{
    public int Order => 30;

    public async Task ContributeAsync(OnboardingContext c, CancellationToken ct)
    {
        var byCode = new Dictionary<string, Account>();
        foreach (var d in DefaultChart.Accounts)
        {
            var name = c.Locale.StartsWith("en") ? d.En : d.Fr;
            var a = new Account
            {
                TenantId = c.TenantId, Code = d.Code, Name = name, NameAr = d.Ar, Type = d.Type, SystemRole = d.Role, IsPostable = d.Postable,
                ParentId = d.Parent is null ? null : byCode[d.Parent].Id,
            };
            byCode[d.Code] = a;
            db.Add(a);
        }
        db.Add(new BankAccount { TenantId = c.TenantId, Name = "Banque principale", LedgerAccountId = byCode["512"].Id, Currency = c.Currency });
        await db.SaveChangesAsync(ct);
    }
}
