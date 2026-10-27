using System.Globalization;

namespace Veltrix.Desktop.Core.Pos;

/// <summary>Document vocabulary of the extra designs (the classic ticket keeps <see cref="ReceiptLabels"/>).</summary>
public sealed record DocLabels(string Ticket, string Invoice, string Proforma, string Delivery, string No, string Item, string Code, string Qty, string Price,
    string Amount, string TotalHt, string TotalTtc, string Seller, string Client, string InvoiceWords, string Stamp, string DeliveredBy, string ReceivedBy,
    string Payment, string Items, string Thanks)
{
    public static readonly DocLabels Fr = new("TICKET DE CAISSE", "FACTURE", "FACTURE PROFORMA", "BON DE LIVRAISON", "N°", "Désignation", "Code", "Qté", "P.U.",
        "Montant", "Total HT", "Total TTC", "Vendeur", "Client", "Arrêtée la présente facture à la somme de", "Cachet et signature", "Livré par", "Reçu par",
        "Paiement", "Articles", "Merci de votre visite");
    public static readonly DocLabels En = new("SALES RECEIPT", "INVOICE", "PROFORMA INVOICE", "DELIVERY NOTE", "No.", "Item", "Code", "Qty", "Unit price",
        "Amount", "Total excl. tax", "Total incl. tax", "Seller", "Customer", "This invoice is closed at the sum of", "Stamp and signature", "Delivered by",
        "Received by", "Payment", "Items", "Thank you for your visit");
    public static readonly DocLabels Ar = new("وصل الصندوق", "فاتورة", "فاتورة شكلية", "وصل تسليم", "رقم", "البيان", "الرمز", "الكمية", "سعر الوحدة",
        "المبلغ", "المجموع دون رسم", "المجموع بالرسم", "البائع", "الزبون", "أوقفت هذه الفاتورة على مبلغ", "الختم والإمضاء", "سلّمه", "استلمه",
        "الدفع", "عدد المواد", "شكرًا على زيارتكم");

    public static DocLabels For(string language) => language switch { "ar" => Ar, "fr" => Fr, _ => En };
}

/// <summary>
/// Shop tickets (compact, detailed, modern, boxed) and company documents (invoice, proforma, delivery note). All designs honour the
/// receipt settings (shop identity, logo, footer, barcode, language) and produce the same row model the renderer and the text printer use.
/// Amounts in words are spelled in French (the usual language of Algerian commercial documents).
/// </summary>
public static class ReceiptDesigns
{
    private sealed class Rows(int width)
    {
        public readonly List<ReceiptRow> List = [];
        public int W => width;
        public void Center(string? text, bool bold = false, bool ltr = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var part in text.Replace("\r", "").Split('\n'))
                if (!string.IsNullOrWhiteSpace(part))
                    foreach (var l in ReceiptLayout.Wrap(part.Trim(), width)) List.Add(new ReceiptRow(ReceiptRowKind.Text, l, Center: true, Bold: bold, Ltr: ltr));
        }
        public void Left(string? text, bool bold = false, bool ltr = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var part in text.Replace("\r", "").Split('\n'))
                if (!string.IsNullOrWhiteSpace(part))
                    foreach (var l in ReceiptLayout.Wrap(part.Trim(), width)) List.Add(new ReceiptRow(ReceiptRowKind.Text, l, Bold: bold, Ltr: ltr));
        }
        public void Pair(string left, string right, bool bold = false)
        {
            if (left.Length + right.Length + 1 > width) { Left(left, bold); left = ""; }
            List.Add(new ReceiptRow(ReceiptRowKind.Pair, left, right, Bold: bold));
        }
        public void Sep(char c = '-') => List.Add(new ReceiptRow(ReceiptRowKind.Separator, c == '-' ? "" : c.ToString()));
        public void Cols(bool bold, params ReceiptCell[] cells) => List.Add(new ReceiptRow(ReceiptRowKind.Columns, Bold: bold, Cells: cells));
    }

    public static ReceiptDocument Build(string template, ReceiptModel r, ReceiptSettings s, ReceiptPage page, bool reprint)
    {
        var L = ReceiptLabels.For(page.Language);
        var D = DocLabels.For(page.Language);
        var rows = new Rows(page.Columns);
        var c = CultureInfo.InvariantCulture;
        string M(decimal v) => v.ToString("#,##0.00", c);
        string Q(decimal v) => v.ToString("0.###", c);
        var date = r.Date.ToLocalTime().ToString(ReceiptDateFormats.All.Contains(s.DateFormat) ? s.DateFormat : ReceiptDateFormats.Default, c);
        var shop = string.IsNullOrWhiteSpace(s.ShopName) ? r.CompanyName : s.ShopName;
        var company = ReceiptTemplates.IsCompany(template);

        void Identity(bool centered)
        {
            if (page.LogoPath is not null) rows.List.Add(new ReceiptRow(ReceiptRowKind.Logo, page.LogoPath));
            Action<string?, bool, bool> put = centered ? rows.Center : rows.Left;
            put(shop, true, false);
            put(s.Address, false, false);
            if (!string.IsNullOrWhiteSpace(s.Phone)) put($"{L.Phone}: {s.Phone}", false, false);
            var ids = string.Join("  ", new[] { ("NIF", s.Nif), ("RC", s.Rc), ("NIS", s.Nis), ("AI", s.Ai) }
                .Where(x => !string.IsNullOrWhiteSpace(x.Item2)).Select(x => $"{x.Item1}: {x.Item2!.Trim()}"));
            put(ids, false, true);
            put(r.Header, false, false);
            put(s.HeaderLines, false, false);
        }

        void Footer()
        {
            if (r.Offline) rows.Center(L.Offline);
            rows.Center(r.Footer);
            rows.Center(s.FooterLines);
            if (s.ShowSaleBarcode && !string.IsNullOrWhiteSpace(r.Number)) rows.List.Add(new ReceiptRow(ReceiptRowKind.Barcode, r.Number, Center: true, Ltr: true));
        }

        void Payments()
        {
            if (!s.ShowPayments) return;
            foreach (var p in r.Payments) rows.Pair(L.Payment(p.Method), M(p.Amount));
            if (r.Change > 0) rows.Pair(L.Change, M(r.Change));
        }

        switch (template)
        {
            case ReceiptTemplates.Compact:
                rows.Center(shop, bold: true);
                if (!string.IsNullOrWhiteSpace(s.Phone)) rows.Center(s.Phone, ltr: true);
                if (reprint) rows.Center($"*** {L.Reprint} ***", bold: true);
                rows.Pair(r.Number, date);
                rows.Sep();
                foreach (var l in r.Lines)
                    rows.Cols(false, new ReceiptCell(l.Name, 6), new ReceiptCell(Q(l.Quantity), 1.5, Right: true, Ltr: true), new ReceiptCell(M(l.Total), 3, Right: true, Ltr: true));
                rows.Sep();
                rows.Pair($"{L.Total} {r.Currency}", M(r.Total), bold: true);
                Payments();
                Footer();
                break;

            case ReceiptTemplates.Detailed:
                Identity(centered: true);
                if (reprint) rows.Center($"*** {L.Reprint} ***", bold: true);
                rows.Sep();
                rows.Pair(L.Sale, r.Number);
                rows.Pair(L.Date, date);
                if (!string.IsNullOrWhiteSpace(r.Cashier)) rows.Pair(L.Cashier, r.Cashier);
                if (!string.IsNullOrWhiteSpace(r.Customer)) rows.Pair(L.Customer, r.Customer!);
                rows.Sep();
                foreach (var l in r.Lines)
                {
                    rows.Left(l.Name);
                    if (!string.IsNullOrWhiteSpace(l.Code)) rows.Left(l.Code, ltr: true);
                    rows.Pair($"  {Q(l.Quantity)} x {M(l.UnitPrice)}", M(l.Total));
                    if (l.Discount > 0) rows.Pair($"  {L.Discount}", "-" + M(l.Discount));
                }
                rows.Sep();
                rows.Pair(D.Items, Q(r.Lines.Sum(l => l.Quantity)));
                rows.Pair(L.Subtotal, M(r.Subtotal));
                if (r.Discount > 0) rows.Pair(L.Discount, "-" + M(r.Discount));
                rows.Pair(D.TotalHt, M(r.Total - r.Tax));
                rows.Pair(L.Tax, M(r.Tax));
                rows.Pair($"{L.Total} {r.Currency}", M(r.Total), bold: true);
                rows.Left($"{L.InWords}: {AmountInWords.French(r.Total, r.Currency)}");
                Payments();
                rows.Sep();
                Footer();
                break;

            case ReceiptTemplates.Modern:
            case ReceiptTemplates.Boxed:
                var line = template == ReceiptTemplates.Boxed ? '*' : '=';
                rows.Sep(line);
                Identity(centered: true);
                rows.Sep(line);
                rows.Center(D.Ticket, bold: true);
                if (reprint) rows.Center($"*** {L.Reprint} ***", bold: true);
                rows.Center($"{D.No} {r.Number}", ltr: true);
                rows.Center(date, ltr: true);
                if (s.ShowCashier && !string.IsNullOrWhiteSpace(r.Cashier)) rows.Center($"{L.Cashier}: {r.Cashier}");
                if (s.ShowCustomer && !string.IsNullOrWhiteSpace(r.Customer)) rows.Center($"{L.Customer}: {r.Customer}");
                rows.Sep();
                rows.Cols(true, new ReceiptCell(D.Item, 5), new ReceiptCell(D.Qty, 1.5, Right: true), new ReceiptCell(D.Amount, 3, Right: true));
                foreach (var l in r.Lines)
                    rows.Cols(false, new ReceiptCell(l.Name, 5), new ReceiptCell(Q(l.Quantity), 1.5, Right: true, Ltr: true), new ReceiptCell(M(l.Total), 3, Right: true, Ltr: true));
                rows.Sep();
                if (s.ShowDiscount && r.Discount > 0) rows.Pair(L.Discount, "-" + M(r.Discount));
                if (s.ShowTax) rows.Pair(L.Tax, M(r.Tax));
                rows.Sep(line);
                rows.Pair($"{L.Total} {r.Currency}", M(r.Total), bold: true);
                rows.Sep(line);
                if (s.TotalInWords) rows.Left($"{L.InWords}: {AmountInWords.French(r.Total, r.Currency)}");
                Payments();
                rows.Center(string.IsNullOrWhiteSpace(s.FooterLines) && string.IsNullOrWhiteSpace(r.Footer) ? D.Thanks : null);
                Footer();
                break;

            default: // company documents (A4)
                Identity(centered: false);
                rows.Sep('=');
                var title = template switch { ReceiptTemplates.Proforma => D.Proforma, ReceiptTemplates.Delivery => D.Delivery, _ => D.Invoice };
                rows.Center($"{title} {D.No} {r.Number}", bold: true);
                if (reprint) rows.Center($"*** {L.Reprint} ***", bold: true);
                rows.Pair($"{L.Date}: {date}", string.IsNullOrWhiteSpace(r.Cashier) ? "" : $"{D.Seller}: {r.Cashier}");
                rows.Left($"{D.Client}: {(string.IsNullOrWhiteSpace(r.Customer) ? "—" : r.Customer)}", bold: true);
                rows.Sep();
                var prices = template != ReceiptTemplates.Delivery;
                if (prices)
                {
                    rows.Cols(true, new ReceiptCell(D.Code, 2.2), new ReceiptCell(D.Item, 6), new ReceiptCell(D.Qty, 1.3, Right: true),
                        new ReceiptCell(D.Price, 2, Right: true), new ReceiptCell(L.Discount, 1.6, Right: true), new ReceiptCell(D.Amount, 2.4, Right: true));
                    foreach (var l in r.Lines)
                        rows.Cols(false, new ReceiptCell(l.Code ?? "", 2.2, Ltr: true), new ReceiptCell(l.Name, 6), new ReceiptCell(Q(l.Quantity), 1.3, Right: true, Ltr: true),
                            new ReceiptCell(M(l.UnitPrice), 2, Right: true, Ltr: true), new ReceiptCell(l.Discount > 0 ? M(l.Discount) : "", 1.6, Right: true, Ltr: true),
                            new ReceiptCell(M(l.Total), 2.4, Right: true, Ltr: true));
                    rows.Sep();
                    rows.Pair(D.TotalHt, M(r.Total - r.Tax));
                    if (r.Discount > 0) rows.Pair(L.Discount, "-" + M(r.Discount));
                    rows.Pair(L.Tax, M(r.Tax));
                    rows.Pair($"{D.TotalTtc} ({r.Currency})", M(r.Total), bold: true);
                    rows.Sep();
                    rows.Left($"{D.InvoiceWords} : {AmountInWords.French(r.Total, r.Currency)}");
                    if (template == ReceiptTemplates.Invoice && s.ShowPayments && r.Payments.Count > 0)
                        rows.Left($"{D.Payment}: " + string.Join(", ", r.Payments.Select(p => $"{L.Payment(p.Method)} {M(p.Amount)}")));
                    rows.List.Add(new ReceiptRow(ReceiptRowKind.Text, " "));
                    rows.Pair("", D.Stamp, bold: true);
                }
                else
                {
                    rows.Cols(true, new ReceiptCell(D.Code, 3), new ReceiptCell(D.Item, 9), new ReceiptCell(D.Qty, 2, Right: true));
                    foreach (var l in r.Lines)
                        rows.Cols(false, new ReceiptCell(l.Code ?? "", 3, Ltr: true), new ReceiptCell(l.Name, 9), new ReceiptCell(Q(l.Quantity), 2, Right: true, Ltr: true));
                    rows.Sep();
                    rows.Pair(D.Items, Q(r.Lines.Sum(l => l.Quantity)), bold: true);
                    rows.List.Add(new ReceiptRow(ReceiptRowKind.Text, " "));
                    rows.Pair(D.DeliveredBy, D.ReceivedBy, bold: true);
                }
                rows.List.Add(new ReceiptRow(ReceiptRowKind.Text, " "));
                rows.List.Add(new ReceiptRow(ReceiptRowKind.Text, " "));
                Footer();
                break;
        }
        return new ReceiptDocument(page, rows.List);
    }
}
