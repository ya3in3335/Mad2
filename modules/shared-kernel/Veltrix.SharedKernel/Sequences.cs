namespace Veltrix.SharedKernel;

/// <summary>Gap-aware document numbering per tenant/branch/document type. Numbers are allocated inside the
/// business transaction with a row lock so finalized numbers are never reused.</summary>
public sealed class DocumentSequence : TenantEntity
{
    public string DocumentType { get; set; } = "";
    /// <summary>Guid.Empty for company-wide sequences.</summary>
    public Guid BranchId { get; set; }
    public string Prefix { get; set; } = "";
    public long NextNumber { get; set; } = 1;
    public int Padding { get; set; } = 6;
    public bool ResetYearly { get; set; } = true;
    public int Year { get; set; }
}

public interface ISequenceService
{
    Task<string> NextAsync(string documentType, Guid? branchId, CancellationToken ct = default);
}

public static class DocumentTypes
{
    public const string PosReceipt = "pos_receipt";
    public const string SalesInvoice = "sales_invoice";
    public const string CreditNote = "credit_note";
    public const string Quotation = "quotation";
    public const string SalesOrder = "sales_order";
    public const string DeliveryNote = "delivery_note";
    public const string PurchaseOrder = "purchase_order";
    public const string GoodsReceipt = "goods_receipt";
    public const string PurchaseRequest = "purchase_request";
    public const string SupplierPayment = "supplier_payment";
    public const string CustomerPayment = "customer_payment";
    public const string JournalEntry = "journal_entry";
    public const string StockTransfer = "stock_transfer";
    public const string StockCount = "stock_count";
    public const string Expense = "expense";
    public const string Payslip = "payslip";

    public static readonly IReadOnlyDictionary<string, string> DefaultPrefixes = new Dictionary<string, string>
    {
        [PosReceipt] = "TK", [SalesInvoice] = "FA", [CreditNote] = "AV", [Quotation] = "DV", [SalesOrder] = "BC",
        [DeliveryNote] = "BL", [PurchaseOrder] = "BCF", [GoodsReceipt] = "BR", [PurchaseRequest] = "DA",
        [SupplierPayment] = "RF", [CustomerPayment] = "RC", [JournalEntry] = "JE", [StockTransfer] = "TR",
        [StockCount] = "INV", [Expense] = "DEP", [Payslip] = "BP",
    };
}
