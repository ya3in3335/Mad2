namespace Veltrix.Desktop.Core.Data;

// Schema v3: the local ERP (suppliers, purchases, expenses, double-entry accounting, HR/payroll, cash book).
// All amounts are decimal; accounting dates are DateOnly (stored as ISO text, so ordering and range filters work in SQLite).

public sealed class LocalSupplier
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxId { get; set; }
    public string? Notes { get; set; }
    /// <summary>Amount owed to the supplier (positive = we owe; negative = advance paid).</summary>
    public decimal Balance { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public static class PurchaseKinds
{
    public const string Invoice = "Invoice";
    public const string Return = "Return";
}

public static class PurchaseStatuses
{
    public const string Draft = "Draft";
    public const string Received = "Received";
}

/// <summary>Purchase invoice (goods in) or purchase return (goods back to the supplier).</summary>
public sealed class LocalPurchase
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public string Kind { get; set; } = PurchaseKinds.Invoice;
    public string Status { get; set; } = PurchaseStatuses.Draft;
    public Guid SupplierId { get; set; }
    public string? SupplierReference { get; set; }
    /// <summary>For a return: the invoice the goods came from (optional).</summary>
    public Guid? OriginalPurchaseId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LocalPurchaseLine
{
    public Guid Id { get; set; }
    public Guid PurchaseId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TaxRatePercent { get; set; }
    public decimal Net { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    /// <summary>Returns: moving-average cost the goods left the stock at.</summary>
    public decimal StockUnitCost { get; set; }
}

public static class PartyKinds
{
    public const string Customer = "customer";
    public const string Supplier = "supplier";
    public const string Employee = "employee";
}

public static class PayMethods
{
    public const string Cash = "Cash";
    public const string Bank = "Bank";
    public static readonly IReadOnlyList<string> All = [Cash, Bank];
}

/// <summary>Money received from a customer or paid to a supplier.</summary>
public sealed class LocalPayment
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public string PartyType { get; set; } = PartyKinds.Supplier;
    public Guid PartyId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = PayMethods.Cash;
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LocalExpenseCategory
{
    public Guid Id { get; set; }
    /// <summary>Stable key of a seeded category (rent, electricity…), null for user-created ones.</summary>
    public string? Key { get; set; }
    public string Name { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class LocalExpense
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public DateOnly Date { get; set; }
    public Guid CategoryId { get; set; }
    /// <summary>Amount paid, VAT included.</summary>
    public decimal Amount { get; set; }
    public decimal TaxAmount { get; set; }
    public string Method { get; set; } = PayMethods.Cash;
    public string? Payee { get; set; }
    public string? Description { get; set; }
    public Guid? JournalEntryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LocalAccount
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string? NameAr { get; set; }
    /// <summary>Asset, Liability, Equity, Revenue or Expense.</summary>
    public string Type { get; set; } = "";
    /// <summary>System role used by automatic postings (see <c>AccountRoles</c>).</summary>
    public string? Role { get; set; }
    public string? ParentCode { get; set; }
    public bool IsPostable { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

public sealed class LocalJournalEntry
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Manual, Pos, SalesReturn, Purchase, PurchaseReturn, Payment, Expense, Payroll, Cash, Inventory…</summary>
    public string Source { get; set; } = "";
    public Guid? SourceId { get; set; }
    public decimal TotalDebit { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LocalJournalLine
{
    public Guid Id { get; set; }
    public Guid EntryId { get; set; }
    public int LineNo { get; set; }
    public DateOnly Date { get; set; }
    public string AccountCode { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Description { get; set; }
    public string? PartyType { get; set; }
    public Guid? PartyId { get; set; }
}

/// <summary>A closed accounting month (absent row = open).</summary>
public sealed class LocalClosedPeriod
{
    /// <summary>yyyy-MM</summary>
    public string Period { get; set; } = "";
    public DateTimeOffset ClosedAt { get; set; }
}

public sealed class LocalEmployee
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Role { get; set; }
    public string? Phone { get; set; }
    /// <summary>Monthly gross salary (internal calculation, no tax/social-security computation).</summary>
    public decimal Salary { get; set; }
    public DateOnly HireDate { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class LocalSalaryAdvance
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = PayMethods.Cash;
    /// <summary>Payroll run that deducted the advance (null = still outstanding).</summary>
    public Guid? PayrollRunId { get; set; }
    public Guid? JournalEntryId { get; set; }
}

public static class PayrollStatuses
{
    public const string Posted = "Posted";
    public const string Paid = "Paid";
}

public sealed class LocalPayrollRun
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public int Year { get; set; }
    public int Month { get; set; }
    public DateOnly Date { get; set; }
    public decimal TotalGross { get; set; }
    public decimal TotalAdvances { get; set; }
    public decimal TotalNet { get; set; }
    public string Status { get; set; } = PayrollStatuses.Posted;
    public Guid? JournalEntryId { get; set; }
    public Guid? PaymentEntryId { get; set; }
    public string? PaidMethod { get; set; }
    public DateOnly? PaidDate { get; set; }
}

public sealed class LocalPayslip
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public decimal Gross { get; set; }
    public decimal Advances { get; set; }
    public decimal Net { get; set; }
}

/// <summary>Customer return of goods sold at the POS.</summary>
public sealed class LocalSaleReturn
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public Guid SaleId { get; set; }
    public Guid? CustomerId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Net { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public decimal Cost { get; set; }
    /// <summary>Cash, Bank or OnAccount (credited to the customer's balance).</summary>
    public string Method { get; set; } = PayMethods.Cash;
    public string? Reason { get; set; }
    public Guid? JournalEntryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LocalSaleReturnLine
{
    public Guid Id { get; set; }
    public Guid ReturnId { get; set; }
    public Guid SaleLineId { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Net { get; set; }
    public decimal Tax { get; set; }
    public decimal UnitCost { get; set; }
}

/// <summary>Cash book line: every movement of the cash account (530) other than POS sales, linked to the open shift when there is one.</summary>
public sealed class LocalCashMovement
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public DateOnly Date { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ShiftId { get; set; }
    /// <summary>Positive = cash in, negative = cash out.</summary>
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Origin: Manual, Expense, Payment, SalesReturn, Payroll, Advance, ShiftVariance.</summary>
    public string Source { get; set; } = "Manual";
    public Guid? SourceId { get; set; }
    public string? CounterAccountCode { get; set; }
    public Guid? JournalEntryId { get; set; }
}
