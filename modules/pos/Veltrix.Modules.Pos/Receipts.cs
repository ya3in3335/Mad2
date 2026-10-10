using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Veltrix.Modules.Crm;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Pos;

/// <summary>Renders receipts as fixed-width text (thermal printers, 32/42/48 columns) or HTML (A4/standard printers).
/// Clients print the text with their native printing stack (ESC/POS or Windows print).</summary>
public sealed class ReceiptRenderer(DbContext db, ISettingsService settings)
{
    public async Task<string> RenderTextAsync(PosSale sale, int width, CancellationToken ct)
    {
        width = width is 32 or 42 or 48 ? width : await settings.GetAsync(SettingKeys.ReceiptWidthChars, 42, ct);
        var (company, branch, customer, header, footer) = await ContextAsync(sale, ct);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void Center(string s) { foreach (var part in Wrap(s, width)) sb.AppendLine(part.PadLeft((width + part.Length) / 2)); }
        void Row(string left, string right) => sb.AppendLine(left.Length + right.Length + 1 > width
            ? left[..Math.Max(0, width - right.Length - 1)] + " " + right
            : left + right.PadLeft(width - left.Length));
        string Amt(decimal d) => d.ToString("#,##0.00", inv).Replace(',', ' ');
        var line = new string('-', width);

        Center(company.Name.ToUpperInvariant());
        if (company.LegalName is { Length: > 0 } ln && ln != company.Name) Center(ln);
        if ((branch?.Address ?? company.Address) is { } addr) Center(addr);
        if (company.Phone is { } ph) Center("Tel: " + ph);
        if (company.TaxId is { } nif) Center("NIF: " + nif);
        if (company.TradeRegisterNo is { } rc) Center("RC: " + rc);
        if (!string.IsNullOrWhiteSpace(header)) Center(header);
        sb.AppendLine(line);
        sb.AppendLine((sale.IsReturn ? (sale.IsVoidReversal ? "VOID " : "RETURN ") : "") + "No: " + sale.Number);
        sb.AppendLine("Date: " + (sale.ClientCreatedAt ?? sale.CreatedAt).ToOffset(TimeSpan.FromHours(1)).ToString("yyyy-MM-dd HH:mm", inv));
        if (customer is not null) sb.AppendLine("Client: " + customer.Name);
        if (sale.ReprintCount > 0) sb.AppendLine($"*** COPY #{sale.ReprintCount} ***");
        sb.AppendLine(line);
        foreach (var l in sale.Lines.OrderBy(l => l.LineNo))
        {
            foreach (var part in Wrap(l.Description, width)) sb.AppendLine(part);
            Row($"  {l.Quantity.ToString("0.###", inv)} x {Amt(l.UnitPrice)}", Amt(l.LineTotal));
            if (l.DiscountAmount != 0) Row("  Remise", "-" + Amt(Math.Abs(l.DiscountAmount)));
        }
        sb.AppendLine(line);
        Row("Total HT", Amt(sale.Total - sale.TaxTotal));
        foreach (var g in sale.Lines.Where(l => l.TaxAmount != 0).GroupBy(l => l.TaxRatePercent))
            Row($"TVA {g.Key.ToString("0.##", inv)}%", Amt(g.Sum(l => l.TaxAmount)));
        if (sale.Rounding != 0) Row("Arrondi", Amt(sale.Rounding));
        Row("TOTAL TTC", Amt(sale.Total + sale.Rounding));
        sb.AppendLine(line);
        foreach (var p in sale.Payments) Row(p.Method.ToString(), Amt(p.Amount));
        if (sale.Change != 0) Row("Rendu", Amt(sale.Change));
        if (sale.OnAccount != 0) Row("Sur compte", Amt(sale.OnAccount));
        sb.AppendLine(line);
        Center(string.IsNullOrWhiteSpace(footer) ? "Merci de votre visite" : footer);
        Center("VELTRIX OS");
        return sb.ToString();
    }

    public async Task<string> RenderHtmlAsync(PosSale sale, CancellationToken ct)
    {
        var (company, branch, customer, header, footer) = await ContextAsync(sale, ct);
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var inv = CultureInfo.InvariantCulture;
        var rows = string.Join("", sale.Lines.OrderBy(l => l.LineNo).Select(l =>
            $"<tr><td>{E(l.Sku)}</td><td>{E(l.Description)}</td><td class=n>{l.Quantity.ToString("0.###", inv)}</td><td class=n>{l.UnitPrice:0.00}</td><td class=n>{l.DiscountAmount:0.00}</td><td class=n>{l.TaxRatePercent:0.##}%</td><td class=n>{l.LineTotal:0.00}</td></tr>"));
        return $$"""
<!doctype html><html><head><meta charset="utf-8"><title>{{E(sale.Number)}}</title>
<style>body{font-family:Arial,sans-serif;font-size:12px;margin:24px}table{width:100%;border-collapse:collapse}th,td{border-bottom:1px solid #ddd;padding:4px;text-align:left}.n{text-align:right}h1{font-size:18px}</style>
</head><body>
<h1>{{E(company.Name)}}</h1><div>{{E(company.LegalName)}} — {{E(branch?.Address ?? company.Address)}}</div>
<div>NIF: {{E(company.TaxId)}} · RC: {{E(company.TradeRegisterNo)}} · NIS: {{E(company.StatisticalId)}} · AI: {{E(company.ArticleNo)}}</div>
<p>{{E(header)}}</p>
<h2>{{(sale.IsReturn ? "Avoir / Retour" : "Ticket")}} {{E(sale.Number)}}</h2>
<div>Date: {{(sale.ClientCreatedAt ?? sale.CreatedAt):yyyy-MM-dd HH:mm}} · Client: {{E(customer?.Name ?? "-")}}</div>
<table><thead><tr><th>SKU</th><th>Article</th><th class=n>Qté</th><th class=n>PU</th><th class=n>Remise</th><th class=n>TVA</th><th class=n>Total</th></tr></thead><tbody>{{rows}}</tbody></table>
<p class=n>Total HT: {{sale.Total - sale.TaxTotal:0.00}}<br>TVA: {{sale.TaxTotal:0.00}}<br><b>Total TTC: {{sale.Total + sale.Rounding:0.00}} {{E(company.Currency)}}</b></p>
<p>{{E(footer)}}</p></body></html>
""";
    }

    private async Task<(Company Company, Branch? Branch, Customer? Customer, string? Header, string? Footer)> ContextAsync(PosSale sale, CancellationToken ct)
    {
        var company = await db.Set<Company>().AsNoTracking().SingleAsync(c => c.Id == sale.TenantId, ct);
        var branch = await db.Set<Branch>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == sale.BranchId, ct);
        var customer = sale.CustomerId is { } cid ? await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == cid, ct) : null;
        var header = await settings.GetAsync<string?>(SettingKeys.ReceiptHeader, null, ct);
        var footer = await settings.GetAsync<string?>(SettingKeys.ReceiptFooter, null, ct);
        return (company, branch, customer, header, footer);
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        for (var i = 0; i < text.Length; i += width) yield return text.Substring(i, Math.Min(width, text.Length - i));
    }
}
