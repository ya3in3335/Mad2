using System.Globalization;
using System.Text;

namespace Veltrix.Desktop.Core.Pos;

/// <summary>
/// Customisation of the printed sales receipt, persisted in <c>AppSettings.Receipt</c>. The paper width stays in
/// <c>AppSettings.ReceiptPaperMm</c> (58, 80 or <see cref="ReceiptPaper.A4"/>). Defaults reproduce the historical receipt.
/// </summary>
public sealed class ReceiptSettings
{
    public double MarginMm { get; set; }
    /// <summary><see cref="ReceiptFontSizes"/> code.</summary>
    public string FontSize { get; set; } = ReceiptFontSizes.Normal;
    public int Copies { get; set; } = 1;

    /// <summary>Copy of the shop logo inside the application data folder (printed in grayscale).</summary>
    public string? LogoPath { get; set; }
    public double LogoMaxWidthMm { get; set; } = 40;
    /// <summary>Overrides the company name when set.</summary>
    public string? ShopName { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Nif { get; set; }
    public string? Rc { get; set; }
    public string? Nis { get; set; }
    public string? Ai { get; set; }
    /// <summary>Extra header lines, one per line.</summary>
    public string? HeaderLines { get; set; }

    public bool ShowItemCode { get; set; }
    public bool ShowUnitPrice { get; set; } = true;
    public bool ShowDiscount { get; set; } = true;
    public bool ShowTax { get; set; } = true;
    public bool ShowCashier { get; set; } = true;
    public bool ShowCustomer { get; set; } = true;
    public bool ShowSaleNumber { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public string DateFormat { get; set; } = ReceiptDateFormats.Default;
    /// <summary>Total spelled out in French (common on Algerian receipts).</summary>
    public bool TotalInWords { get; set; }
    /// <summary>Payment lines (amount paid per method) and change.</summary>
    public bool ShowPayments { get; set; } = true;

    /// <summary>Footer lines, one per line (e.g. "Merci de votre visite").</summary>
    public string? FooterLines { get; set; }
    /// <summary>Code 128 barcode of the sale number at the bottom.</summary>
    public bool ShowSaleBarcode { get; set; }

    /// <summary>"app" (follow the interface language) or a fixed language code (ar, fr, en).</summary>
    public string Language { get; set; } = "en";
    /// <summary><see cref="ReceiptTemplates"/> code: shop tickets (thermal) or company documents (A4).</summary>
    public string Template { get; set; } = ReceiptTemplates.Classic;

    public ReceiptSettings Clone() => (ReceiptSettings)MemberwiseClone();

    /// <summary>Clamps values read from a settings file (hand edits, older versions).</summary>
    public void Normalize()
    {
        MarginMm = double.IsFinite(MarginMm) ? Math.Clamp(MarginMm, 0, 15) : 0;
        FontSize = ReceiptFontSizes.Normalize(FontSize);
        Copies = Math.Clamp(Copies, 1, 3);
        LogoMaxWidthMm = double.IsFinite(LogoMaxWidthMm) ? Math.Clamp(LogoMaxWidthMm, 10, 150) : 40;
        if (!ReceiptDateFormats.All.Contains(DateFormat)) DateFormat = ReceiptDateFormats.Default;
        Language = Language is "app" or "ar" or "fr" or "en" ? Language : "en";
        Template = ReceiptTemplates.Normalize(Template);
    }
}

public static class ReceiptPaper
{
    public const int Mm58 = 58;
    public const int Mm80 = 80;
    /// <summary>A4 sheet (210 mm wide).</summary>
    public const int A4 = 210;

    public static int Normalize(int mm) => mm is Mm58 or Mm80 or A4 ? mm : Mm80;

    /// <summary>Printable width: thermal heads print 48 / 72 mm; A4 keeps 10 mm hardware margins.</summary>
    public static double PrintableMm(int paperMm) => Normalize(paperMm) switch
    {
        Mm58 => 48,
        A4 => 190,
        _ => 72,
    };
}

public static class ReceiptFontSizes
{
    public const string Small = "small";
    public const string Normal = "normal";
    public const string Large = "large";
    public static readonly IReadOnlyList<string> All = [Small, Normal, Large];

    public static string Normalize(string? code) => code is Small or Large ? code : Normal;

    /// <summary>Monospace character advance on paper. Normal gives the historical 32 (58 mm) / 48 (80 mm) columns.</summary>
    public static double CharWidthMm(string code) => Normalize(code) switch
    {
        Small => 1.2,
        Large => 2.0,
        _ => 1.5,
    };
}

public static class ReceiptDateFormats
{
    public const string Default = "yyyy-MM-dd HH:mm";
    public static readonly IReadOnlyList<string> All = [Default, "dd/MM/yyyy HH:mm", "dd/MM/yyyy", "dd-MM-yyyy HH:mm:ss", "MM/dd/yyyy HH:mm"];
}

/// <summary>Receipt / invoice designs. Shop tickets suit 58 / 80 mm thermal printers; company documents are laid out for A4.</summary>
public static class ReceiptTemplates
{
    public const string Classic = "classic";
    public const string Compact = "compact";
    public const string Detailed = "detailed";
    public const string Modern = "modern";
    public const string Boxed = "boxed";
    public const string Invoice = "invoice";
    public const string Proforma = "proforma";
    public const string Delivery = "delivery";

    public static readonly IReadOnlyList<string> Shop = [Classic, Compact, Detailed, Modern, Boxed];
    public static readonly IReadOnlyList<string> Company = [Invoice, Proforma, Delivery];
    public static readonly IReadOnlyList<string> All = [.. Shop, .. Company];

    public static bool IsCompany(string code) => Company.Contains(code);
    public static string Normalize(string? code) => code is not null && All.Contains(code) ? code : Classic;
}

public enum ReceiptRowKind { Text, Pair, Separator, Logo, Barcode, Columns }

/// <summary>One table cell: relative width, right alignment (amounts) and LTR (numbers, codes).</summary>
public sealed record ReceiptCell(string Text, double Weight, bool Right = false, bool Ltr = false);

/// <summary>
/// One row of the receipt. Text rows are already wrapped to the column count. <paramref name="Ltr"/> marks text that must stay
/// left-to-right inside an Arabic receipt (amounts, quantities, codes); <see cref="Right"/> values of pairs are always LTR.
/// </summary>
/// <remarks>Separator rows may carry their character in <see cref="Text"/> ("=", "*"); Columns rows carry <see cref="Cells"/>.</remarks>
public sealed record ReceiptRow(ReceiptRowKind Kind, string Text = "", string? Right = null, bool Center = false, bool Bold = false,
    int Indent = 0, bool Ltr = false, IReadOnlyList<ReceiptCell>? Cells = null);

/// <summary>Physical page description shared by the preview and the printer.</summary>
public sealed record ReceiptPage(int PaperMm, double PrintableWidthMm, double MarginMm, int Columns, double CharWidthMm, string Language,
    bool RightToLeft, int Copies, string? LogoPath, double LogoMaxWidthMm)
{
    public double ContentWidthMm => Math.Max(10, PrintableWidthMm - 2 * MarginMm);
}

public sealed record ReceiptDocument(ReceiptPage Page, IReadOnlyList<ReceiptRow> Rows)
{
    /// <summary>Plain fixed-width rendering (logical order; logo and barcode rows are omitted, the barcode text is kept).</summary>
    public IReadOnlyList<string> TextLines()
    {
        var w = Page.Columns;
        var lines = new List<string>();
        foreach (var row in Rows)
        {
            switch (row.Kind)
            {
                case ReceiptRowKind.Separator:
                    lines.Add(new string(row.Text is { Length: 1 } ch ? ch[0] : '-', w));
                    break;
                case ReceiptRowKind.Columns when row.Cells is { Count: > 0 } cells:
                    var total = cells.Sum(c => c.Weight);
                    var line = new StringBuilder();
                    foreach (var c in cells)
                    {
                        var cw = Math.Max(1, (int)Math.Floor(w * c.Weight / total) - 1);
                        var text = c.Text.Length > cw ? c.Text[..cw] : c.Text;
                        line.Append(c.Right ? text.PadLeft(cw) : text.PadRight(cw)).Append(' ');
                    }
                    lines.Add(line.ToString().TrimEnd());
                    break;
                case ReceiptRowKind.Text:
                    var t = new string(' ', row.Indent) + row.Text;
                    lines.Add(row.Center && t.Length < w ? new string(' ', (w - t.Length) / 2) + t : t);
                    break;
                case ReceiptRowKind.Pair:
                    var left = new string(' ', row.Indent) + row.Text;
                    var right = row.Right ?? "";
                    lines.Add(left + new string(' ', Math.Max(1, w - left.Length - right.Length)) + right);
                    break;
                case ReceiptRowKind.Barcode:
                    lines.Add(row.Text.Length < w ? new string(' ', (w - row.Text.Length) / 2) + row.Text : row.Text);
                    break;
            }
        }
        return lines;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var l in TextLines()) sb.AppendLine(l);
        return sb.ToString();
    }
}

/// <summary>
/// Builds the receipt layout (rows already fitted to the paper's column count) from a sale and the receipt settings.
/// The WPF renderer only draws these rows, so the preview, the test print and real sales print identically.
/// </summary>
public static class ReceiptLayout
{
    private const int MinColumns = 16;

    public static int Columns(int paperMm, string fontSize, double marginMm) =>
        Math.Max(MinColumns, (int)Math.Floor((ReceiptPaper.PrintableMm(paperMm) - 2 * Math.Max(0, marginMm)) / ReceiptFontSizes.CharWidthMm(fontSize) + 1e-9));

    public static string ResolveLanguage(ReceiptSettings s, string appLanguage) =>
        s.Language is "ar" or "fr" or "en" ? s.Language : Settings.Languages.Normalize(appLanguage);

    public static ReceiptPage Page(int paperMm, ReceiptSettings s, string appLanguage)
    {
        var paper = ReceiptPaper.Normalize(paperMm);
        var lang = ResolveLanguage(s, appLanguage);
        return new ReceiptPage(paper, ReceiptPaper.PrintableMm(paper), s.MarginMm, Columns(paper, s.FontSize, s.MarginMm),
            ReceiptFontSizes.CharWidthMm(s.FontSize), lang, lang == "ar", Math.Clamp(s.Copies, 1, 3),
            string.IsNullOrWhiteSpace(s.LogoPath) ? null : s.LogoPath, s.LogoMaxWidthMm);
    }

    /// <summary>Page for a bare column count (legacy text callers): English, no logo.</summary>
    public static ReceiptPage PageForColumns(int columns)
    {
        columns = Math.Max(MinColumns, columns);
        var paper = columns <= 32 ? ReceiptPaper.Mm58 : ReceiptPaper.Mm80;
        return new ReceiptPage(paper, ReceiptPaper.PrintableMm(paper), 0, columns, ReceiptPaper.PrintableMm(paper) / columns, "en", false, 1, null, 40);
    }

    public static ReceiptDocument Build(ReceiptModel r, ReceiptSettings s, int paperMm, string appLanguage, bool reprint = false) =>
        Build(r, s, Page(paperMm, s, appLanguage), reprint);

    public static ReceiptDocument Build(ReceiptModel r, ReceiptSettings s, ReceiptPage page, bool reprint = false) =>
        ReceiptTemplates.Normalize(s.Template) switch
        {
            ReceiptTemplates.Classic => BuildClassic(r, s, page, reprint),
            var t => ReceiptDesigns.Build(t, r, s, page, reprint),
        };

    private static ReceiptDocument BuildClassic(ReceiptModel r, ReceiptSettings s, ReceiptPage page, bool reprint)
    {
        var w = page.Columns;
        var L = ReceiptLabels.For(page.Language);
        var c = CultureInfo.InvariantCulture;
        var rows = new List<ReceiptRow>();
        string M(decimal v) => v.ToString("#,##0.00", c);

        void Centered(string? text, bool bold = false, bool ltr = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var part in text.Replace("\r", "").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                foreach (var line in Wrap(part.Trim(), w)) rows.Add(new ReceiptRow(ReceiptRowKind.Text, line, Center: true, Bold: bold, Ltr: ltr));
            }
        }
        void Field(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) Centered($"{label}: {value.Trim()}");
        }
        void Pair(string left, string right, bool bold = false, int indent = 0, bool ltr = false)
        {
            if (indent + left.Length + right.Length + 1 <= w)
            {
                rows.Add(new ReceiptRow(ReceiptRowKind.Pair, left, right, Bold: bold, Indent: indent, Ltr: ltr));
                return;
            }
            // Too long for one line: the label wraps, the value goes right-aligned on the last line.
            var wrapped = Wrap(left, w - indent);
            for (var i = 0; i < wrapped.Count - 1; i++) rows.Add(new ReceiptRow(ReceiptRowKind.Text, wrapped[i], Bold: bold, Indent: indent, Ltr: ltr));
            var last = wrapped.Count > 0 ? wrapped[^1] : "";
            if (indent + last.Length + right.Length + 1 > w)
            {
                rows.Add(new ReceiptRow(ReceiptRowKind.Text, last, Bold: bold, Indent: indent, Ltr: ltr));
                last = "";
            }
            rows.Add(new ReceiptRow(ReceiptRowKind.Pair, last, right.Length > w - indent - 1 ? right[..Math.Max(1, w - indent - 1)] : right, Bold: bold, Indent: indent, Ltr: ltr));
        }
        void Separator() => rows.Add(new ReceiptRow(ReceiptRowKind.Separator));

        // Header
        if (page.LogoPath is not null) rows.Add(new ReceiptRow(ReceiptRowKind.Logo, page.LogoPath));
        Centered(string.IsNullOrWhiteSpace(s.ShopName) ? r.CompanyName : s.ShopName, bold: true);
        Centered(s.Address);
        Field(L.Phone, s.Phone);
        Field("NIF", s.Nif);
        Field("RC", s.Rc);
        Field("NIS", s.Nis);
        Field("AI", s.Ai);
        Centered(r.Header);
        Centered(s.HeaderLines);
        if (reprint) Centered($"*** {L.Reprint} ***", bold: true);
        Separator();

        var date = r.Date.ToLocalTime().ToString(ReceiptDateFormats.All.Contains(s.DateFormat) ? s.DateFormat : ReceiptDateFormats.Default, c);
        if (s.ShowSaleNumber && s.ShowDate) Pair(r.Number, date, ltr: true);
        else if (s.ShowSaleNumber) Pair(L.Sale, r.Number);
        else if (s.ShowDate) Pair(L.Date, date);
        if (s.ShowCashier && !string.IsNullOrWhiteSpace(r.Cashier)) Pair(L.Cashier, r.Cashier);
        if (s.ShowCustomer && !string.IsNullOrWhiteSpace(r.Customer)) Pair(L.Customer, r.Customer!);
        Separator();

        // Body
        foreach (var l in r.Lines)
        {
            foreach (var line in Wrap(l.Name, w)) rows.Add(new ReceiptRow(ReceiptRowKind.Text, line));
            if (s.ShowItemCode && !string.IsNullOrWhiteSpace(l.Code)) rows.Add(new ReceiptRow(ReceiptRowKind.Text, l.Code!, Indent: 2, Ltr: true));
            var qty = l.Quantity.ToString("0.###", c);
            Pair(s.ShowUnitPrice ? $"{qty} x {M(l.UnitPrice)}" : $"{qty} x", M(l.Total), indent: 2, ltr: true);
            if (s.ShowDiscount && l.Discount > 0) Pair(L.Discount, "-" + M(l.Discount), indent: 2);
        }
        Separator();

        // Totals
        Pair(L.Subtotal, M(r.Subtotal));
        if (s.ShowDiscount && r.Discount > 0) Pair(L.Discount, "-" + M(r.Discount));
        if (s.ShowTax) Pair(L.Tax, M(r.Tax));
        Pair($"{L.Total} {r.Currency}", M(r.Total), bold: true);
        if (s.TotalInWords)
            foreach (var line in Wrap($"{L.InWords}: {AmountInWords.French(r.Total, r.Currency)}", w)) rows.Add(new ReceiptRow(ReceiptRowKind.Text, line));
        if (s.ShowPayments)
        {
            foreach (var p in r.Payments) Pair(L.Payment(p.Method), M(p.Amount));
            if (r.Change > 0) Pair(L.Change, M(r.Change));
        }
        Separator();

        // Footer
        if (r.Offline) Centered(L.Offline);
        Centered(r.Footer);
        Centered(s.FooterLines);
        if (s.ShowSaleBarcode && !string.IsNullOrWhiteSpace(r.Number)) rows.Add(new ReceiptRow(ReceiptRowKind.Barcode, r.Number, Center: true, Ltr: true));
        return new ReceiptDocument(page, rows);
    }

    /// <summary>Text already rendered elsewhere (server reprint): one text row per line.</summary>
    public static ReceiptDocument FromPlainText(string text, ReceiptPage page) =>
        new(page, (text ?? "").Replace("\r", "").TrimEnd('\n').Split('\n').Select(l => new ReceiptRow(ReceiptRowKind.Text, l, Ltr: true)).ToList());

    /// <summary>Word wrap to <paramref name="width"/> characters; words longer than a line are cut.</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var result = new List<string>();
        var line = new StringBuilder();
        foreach (var word in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var rest = word;
            while (rest.Length > 0)
            {
                var needed = line.Length == 0 ? rest.Length : line.Length + 1 + rest.Length;
                if (needed <= width)
                {
                    if (line.Length > 0) line.Append(' ');
                    line.Append(rest);
                    rest = "";
                }
                else if (line.Length > 0)
                {
                    result.Add(line.ToString());
                    line.Clear();
                }
                else
                {
                    result.Add(rest[..width]);
                    rest = rest[width..];
                }
            }
        }
        if (line.Length > 0 || result.Count == 0) result.Add(line.ToString());
        return result;
    }

    /// <summary>Sample sale for the settings preview and the test print.</summary>
    public static ReceiptModel Sample(string language, string? companyName, DateTimeOffset now)
    {
        var ar = language == "ar";
        var lines = new List<ReceiptLine>
        {
            new(ar ? "قهوة محمصة 250 غ" : language == "fr" ? "Café torréfié 250 g" : "Roasted coffee 250 g", 2, 450m, 0, 900m, "6130000000013"),
            new(ar ? "حليب كامل الدسم 1 ل" : language == "fr" ? "Lait entier 1 L" : "Whole milk 1 L", 3, 110m, 33m, 297m, "6130000000020"),
            new(ar ? "خبز" : language == "fr" ? "Pain" : "Bread", 1, 25m, 0, 25m, "BREAD-01"),
        };
        return new ReceiptModel(string.IsNullOrWhiteSpace(companyName) ? "VELTRIX" : companyName!, null, null, "R-000123", now,
            ar ? "أمينة" : "Amina", ar ? "كريم ب." : "Karim B.", lines, 1255m, 33m, 194.77m, 1222m,
            [new PaymentLine("Cash", 1500m)], 278m, false);
    }
}

/// <summary>Fixed receipt vocabulary per language (the printed receipt does not depend on the UI resource dictionaries).</summary>
public sealed record ReceiptLabels(string Cashier, string Customer, string Sale, string Date, string Subtotal, string Discount, string Tax,
    string Total, string Change, string Reprint, string Offline, string Phone, string InWords, string Cash, string Card, string OnAccount)
{
    public static readonly ReceiptLabels En = new("Cashier", "Customer", "Sale", "Date", "Subtotal", "Discount", "Tax", "TOTAL", "Change",
        "REPRINT", "Recorded offline - will sync", "Tel", "In words", "Cash", "Card", "On account");
    public static readonly ReceiptLabels Fr = new("Caissier", "Client", "Vente", "Date", "Sous-total", "Remise", "TVA", "TOTAL", "Rendu",
        "DUPLICATA", "Enregistré hors ligne - sera synchronisé", "Tél", "Arrêté à la somme de", "Espèces", "Carte", "À crédit");
    public static readonly ReceiptLabels Ar = new("أمين الصندوق", "الزبون", "البيع", "التاريخ", "المجموع الفرعي", "تخفيض", "الرسم", "المجموع", "الباقي",
        "نسخة مكررة", "سُجّل دون اتصال - ستتم المزامنة", "الهاتف", "المبلغ بالحروف", "نقدًا", "بطاقة", "على الحساب");

    public static ReceiptLabels For(string language) => language switch
    {
        "ar" => Ar,
        "fr" => Fr,
        _ => En,
    };

    public string Payment(string method) => method.ToLowerInvariant() switch
    {
        "cash" => Cash,
        "card" => Card,
        "onaccount" => OnAccount,
        _ => method,
    };
}

/// <summary>Spells amounts in French (traditional spelling: "quatre-vingts", "deux cents", "mille").</summary>
public static class AmountInWords
{
    private static readonly string[] Units =
    [
        "zéro", "un", "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf", "dix", "onze", "douze", "treize", "quatorze",
        "quinze", "seize", "dix-sept", "dix-huit", "dix-neuf",
    ];
    private static readonly string[] Tens = ["", "", "vingt", "trente", "quarante", "cinquante", "soixante"];

    public static string French(decimal amount, string currency)
    {
        amount = Math.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero);
        var whole = (long)Math.Floor(amount);
        var cents = (int)((amount - whole) * 100);
        var unit = currency == "DZD" ? (whole > 1 ? "dinars" : "dinar") : currency;
        var text = $"{French(whole)} {unit}";
        if (cents > 0) text += $" et {French(cents)} {(currency == "DZD" ? (cents > 1 ? "centimes" : "centime") : "cts")}";
        return text;
    }

    public static string French(long n)
    {
        if (n == 0) return Units[0];
        var parts = new List<string>();
        var billions = n / 1_000_000_000;
        var millions = n / 1_000_000 % 1000;
        var thousands = n / 1000 % 1000;
        var rest = n % 1000;
        if (billions > 0) parts.Add($"{Below1000(billions, false)} {(billions > 1 ? "milliards" : "milliard")}");
        if (millions > 0) parts.Add($"{Below1000(millions, false)} {(millions > 1 ? "millions" : "million")}");
        if (thousands > 0) parts.Add(thousands == 1 ? "mille" : $"{Below1000(thousands, false)} mille");
        if (rest > 0) parts.Add(Below1000(rest, true));
        return string.Join(' ', parts);
    }

    /// <summary><paramref name="final"/>: the number ends the amount, so "cents"/"vingts" take their plural s.</summary>
    private static string Below1000(long n, bool final)
    {
        var hundreds = n / 100;
        var rest = n % 100;
        var parts = new List<string>();
        if (hundreds == 1) parts.Add("cent");
        else if (hundreds > 1) parts.Add(Units[hundreds] + (rest == 0 && final ? " cents" : " cent"));
        if (rest > 0) parts.Add(Below100((int)rest, final));
        return string.Join(' ', parts);
    }

    private static string Below100(int n, bool final)
    {
        if (n < 20) return Units[n];
        var ten = n / 10;
        var unit = n % 10;
        if (ten is 7 or 9)
        {
            var baseWord = ten == 7 ? "soixante" : "quatre-vingt";
            var sub = 10 + unit;
            return ten == 7 && unit == 1 ? "soixante et onze" : $"{baseWord}-{Units[sub]}";
        }
        if (ten == 8) return unit == 0 ? (final ? "quatre-vingts" : "quatre-vingt") : $"quatre-vingt-{Units[unit]}";
        if (unit == 0) return Tens[ten];
        return unit == 1 ? $"{Tens[ten]} et un" : $"{Tens[ten]}-{Units[unit]}";
    }
}
