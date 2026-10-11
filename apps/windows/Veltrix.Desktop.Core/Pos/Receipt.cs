namespace Veltrix.Desktop.Core.Pos;

/// <summary>One sold item. <paramref name="Code"/> (SKU/barcode) is optional: receipts saved before it existed deserialize without it.</summary>
public sealed record ReceiptLine(string Name, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total, string? Code = null);

public sealed record ReceiptModel(
    string CompanyName, string? Header, string? Footer, string Number, DateTimeOffset Date, string Cashier, string? Customer,
    IReadOnlyList<ReceiptLine> Lines, decimal Subtotal, decimal Discount, decimal Tax, decimal Total, IReadOnlyList<PaymentLine> Payments,
    decimal Change, bool Offline, string Currency = "DZD");

/// <summary>Fixed-width text layout with the default receipt settings (kept for callers that only know a column count).</summary>
public static class ReceiptText
{
    public static int CharsFor(int paperMm) => ReceiptLayout.Columns(paperMm, ReceiptFontSizes.Normal, 0);

    public static string Render(ReceiptModel r, int width, bool reprint = false) =>
        ReceiptLayout.Build(r, new ReceiptSettings(), ReceiptLayout.PageForColumns(width), reprint).ToText();
}
