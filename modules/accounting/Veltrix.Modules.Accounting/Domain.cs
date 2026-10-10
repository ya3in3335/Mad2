using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Accounting;

public enum AccountType { Asset, Liability, Equity, Revenue, Expense }
public enum JournalStatus { Draft, Posted }
public enum JournalSource { Manual, Pos, Sales, Purchasing, Inventory, Expense, Payroll, Reversal, Opening, CashManagement, Subscription }
public enum PeriodStatus { Open, Closed }

/// <summary>System roles let business modules post to the right accounts without hardcoding account codes.</summary>
public static class AccountRoles
{
    public const string Cash = "cash";
    public const string Bank = "bank";
    public const string CardClearing = "card_clearing";
    public const string AccountsReceivable = "accounts_receivable";
    public const string AccountsPayable = "accounts_payable";
    public const string Inventory = "inventory";
    public const string GoodsReceivedNotInvoiced = "grni";
    public const string VatCollected = "vat_collected";
    public const string VatDeductible = "vat_deductible";
    public const string SalesRevenue = "sales_revenue";
    public const string ServiceRevenue = "service_revenue";
    public const string SalesReturns = "sales_returns";
    public const string CostOfGoodsSold = "cogs";
    public const string InventoryAdjustment = "inventory_adjustment";
    public const string PurchasePriceVariance = "purchase_price_variance";
    public const string GeneralExpense = "general_expense";
    public const string Salaries = "salaries_expense";
    public const string SalariesPayable = "salaries_payable";
    public const string PayrollDeductionsPayable = "payroll_deductions_payable";
    public const string Equity = "equity";
    public const string RetainedEarnings = "retained_earnings";
    public const string CashOverShort = "cash_over_short";
    public const string Rounding = "rounding";
    public const string CustomerAdvances = "customer_advances";
    public const string SupplierAdvances = "supplier_advances";
}

public static class PartyTypes
{
    public const string Customer = "customer";
    public const string Supplier = "supplier";
    public const string Employee = "employee";
}

public sealed class Account : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public AccountType Type { get; set; }
    public Guid? ParentId { get; set; }
    public bool IsPostable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public string? SystemRole { get; set; }

    /// <summary>Assets and expenses have debit-normal balances.</summary>
    public bool IsDebitNormal => Type is AccountType.Asset or AccountType.Expense;
}

public sealed class FiscalPeriod : TenantEntity
{
    public int Year { get; set; }
    public int Month { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public PeriodStatus Status { get; set; } = PeriodStatus.Open;
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }
}

public sealed class JournalEntry : TenantEntity, IImmutableWhenFinal
{
    public string Number { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Description { get; set; } = "";
    public JournalSource Source { get; set; }
    public Guid? SourceId { get; set; }
    public JournalStatus Status { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? ReversalOfId { get; set; }
    public Guid? ReversedById { get; set; }
    public decimal TotalDebit { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public Guid? PostedBy { get; set; }
    public List<JournalLine> Lines { get; set; } = new();
    public bool IsFinal => Status == JournalStatus.Posted;
}

public sealed class JournalLine : TenantEntity
{
    public Guid JournalEntryId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Description { get; set; }
    public string? PartyType { get; set; }
    public Guid? PartyId { get; set; }
    public Guid? BranchId { get; set; }
    /// <summary>Denormalized from the entry for efficient ledger and aging queries.</summary>
    public DateOnly Date { get; set; }
}

public sealed class BankAccount : TenantEntity
{
    public string Name { get; set; } = "";
    public string? BankName { get; set; }
    public string? AccountNumber { get; set; }
    public Guid LedgerAccountId { get; set; }
    public string Currency { get; set; } = "DZD";
    public bool IsActive { get; set; } = true;
}

public sealed class BankStatementLine : TenantEntity
{
    public Guid BankAccountId { get; set; }
    public DateOnly Date { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string? Reference { get; set; }
    public Guid? MatchedJournalLineId { get; set; }
    public bool Reconciled { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
}

public enum ExpenseStatus { Posted, Reversed }

public sealed class Expense : TenantEntity
{
    public string Number { get; set; } = "";
    public DateOnly Date { get; set; }
    public Guid ExpenseAccountId { get; set; }
    public decimal Amount { get; set; }
    public decimal TaxAmount { get; set; }
    public Guid PaymentAccountId { get; set; }
    public string? Payee { get; set; }
    public string? Description { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? JournalEntryId { get; set; }
    public ExpenseStatus Status { get; set; }
}

internal sealed class AccountConfig : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("accounts", "accounting");
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.SystemRole }).IsUnique().HasFilter("\"SystemRole\" IS NOT NULL");
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(150);
        b.Property(x => x.NameAr).HasMaxLength(150);
        b.Property(x => x.SystemRole).HasMaxLength(40);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.IsDebitNormal);
    }
}

internal sealed class FiscalPeriodConfig : IEntityTypeConfiguration<FiscalPeriod>
{
    public void Configure(EntityTypeBuilder<FiscalPeriod> b)
    {
        b.ToTable("fiscal_periods", "accounting");
        b.HasIndex(x => new { x.TenantId, x.Year, x.Month }).IsUnique();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
    }
}

internal sealed class JournalEntryConfig : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> b)
    {
        b.ToTable("journal_entries", "accounting");
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Date });
        b.HasIndex(x => new { x.Source, x.SourceId });
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.IsFinal);
    }
}

internal sealed class JournalLineConfig : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> b)
    {
        b.ToTable("journal_lines", "accounting", t => t.HasCheckConstraint("ck_journal_line_side",
            "(\"Debit\" >= 0 AND \"Credit\" >= 0) AND NOT (\"Debit\" > 0 AND \"Credit\" > 0)"));
        b.HasIndex(x => new { x.TenantId, x.AccountId, x.Date });
        b.HasIndex(x => new { x.TenantId, x.PartyType, x.PartyId });
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.PartyType).HasMaxLength(20);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BankAccountConfig : IEntityTypeConfiguration<BankAccount>
{
    public void Configure(EntityTypeBuilder<BankAccount> b)
    {
        b.ToTable("bank_accounts", "accounting");
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.BankName).HasMaxLength(120);
        b.Property(x => x.AccountNumber).HasMaxLength(60);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.LedgerAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BankStatementLineConfig : IEntityTypeConfiguration<BankStatementLine>
{
    public void Configure(EntityTypeBuilder<BankStatementLine> b)
    {
        b.ToTable("bank_statement_lines", "accounting");
        b.HasIndex(x => new { x.BankAccountId, x.Date });
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.HasOne<BankAccount>().WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExpenseConfig : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> b)
    {
        b.ToTable("expenses", "accounting");
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.Payee).HasMaxLength(150);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.PaymentAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
