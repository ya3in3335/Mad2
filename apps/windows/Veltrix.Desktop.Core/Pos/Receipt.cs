using System.Globalization;
using System.Text;

namespace Veltrix.Desktop.Core.Pos;

public sealed record ReceiptLine(string Name, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total);

public sealed record ReceiptModel(
    string CompanyName, string? Header, string? Footer, string Number, DateTimeOffset Date, string Cashier, string? Customer,
    IReadOnlyList<ReceiptLine> Lines, decimal Subtotal, decimal Discount, decimal Tax, decimal Total, IReadOnlyList<PaymentLine> Payments,
    decimal Change, bool Offline, string Currency = "DZD");

/// <summary>Fixed-width text layout used for the thermal preview and plain-text printers.</summary>
public static class ReceiptText
{
    public static int CharsFor(int paperMm) => paperMm <= 58 ? 32 : 48;

    public static string Render(ReceiptModel r, int width, bool reprint = false)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void Center(string s) => sb.AppendLine(s.Length >= width ? s[..width] : new string(' ', (width - s.Length) / 2) + s);
        void Pair(string left, string right)
        {
            if (left.Length + right.Length + 1 > width) left = left[..Math.Max(0, width - right.Length - 1)];
            sb.Append(left).Append(' ', Math.Max(1, width - left.Length - right.Length)).AppendLine(right);
        }
        string M(decimal v) => v.ToString("#,##0.00", c);

        Center(r.CompanyName);
        if (!string.IsNullOrWhiteSpace(r.Header)) foreach (var h in r.Header.Split('\n')) Center(h.Trim());
        if (reprint) Center("*** REPRINT ***");
        sb.AppendLine(new string('-', width));
        Pair(r.Number, r.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", c));
        Pair("Cashier", r.Cashier);
        if (r.Customer is not null) Pair("Customer", r.Customer);
        sb.AppendLine(new string('-', width));
        foreach (var l in r.Lines)
        {
            sb.AppendLine(l.Name.Length > width ? l.Name[..width] : l.Name);
            Pair($"  {l.Quantity.ToString("0.###", c)} x {M(l.UnitPrice)}", M(l.Total));
            if (l.Discount > 0) Pair("  Discount", "-" + M(l.Discount));
        }
        sb.AppendLine(new string('-', width));
        Pair("Subtotal", M(r.Subtotal));
        if (r.Discount > 0) Pair("Discount", "-" + M(r.Discount));
        Pair("Tax", M(r.Tax));
        Pair("TOTAL " + r.Currency, M(r.Total));
        foreach (var p in r.Payments) Pair(p.Method, M(p.Amount));
        if (r.Change > 0) Pair("Change", M(r.Change));
        sb.AppendLine(new string('-', width));
        if (r.Offline) Center("Recorded offline - will sync");
        if (!string.IsNullOrWhiteSpace(r.Footer)) foreach (var f in r.Footer.Split('\n')) Center(f.Trim());
        return sb.ToString();
    }
}
