namespace Veltrix.SharedKernel;

/// <summary>Key/value tenant settings (JSON values) for configurable policies such as negative stock,
/// receipt layouts, rounding and offline grace limits.</summary>
public sealed class TenantSetting : TenantEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public static class SettingKeys
{
    public const string AllowNegativeStock = "inventory.allowNegativeStock";
    public const string CashRoundingIncrement = "pos.cashRoundingIncrement";
    public const string ReceiptHeader = "pos.receiptHeader";
    public const string ReceiptFooter = "pos.receiptFooter";
    public const string ReceiptWidthChars = "pos.receiptWidthChars";
    public const string PricesIncludeTax = "pos.pricesIncludeTax";
    public const string DefaultCurrency = "company.currency";
    public const string Locale = "company.locale";
    public const string LargeDiscountThresholdPct = "intelligence.largeDiscountPct";
    public const string MaxDiscountWithoutPermissionPct = "pos.maxDiscountPct";
}
