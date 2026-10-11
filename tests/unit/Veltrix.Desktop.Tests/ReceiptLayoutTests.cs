using System.Text.Json;
using Veltrix.Desktop.Core.Pos;
using Veltrix.Desktop.Core.Settings;

namespace Veltrix.Desktop.Tests;

public class ReceiptLayoutTests
{
    private static ReceiptModel Sale(string? customer = "Karim", decimal discount = 0) => new("VELTRIX DEMO", "Alger", "Merci", "W-0001",
        new DateTimeOffset(2026, 3, 21, 14, 35, 0, TimeSpan.Zero), "Amina", customer,
        [new ReceiptLine("A very long product name that will not fit on thermal paper at all", 2, 150m, discount, 300m - discount, "6130000000013")],
        300m, discount, 47.90m, 300m - discount, [new PaymentLine("Cash", 500m)], 200m - discount, false);

    private static IReadOnlyList<string> Lines(ReceiptSettings s, int paper = 80, string lang = "en", ReceiptModel? r = null) =>
        ReceiptLayout.Build(r ?? Sale(), s, paper, lang).TextLines();

    [Theory]
    [InlineData(58, ReceiptFontSizes.Normal, 0, 32)]
    [InlineData(80, ReceiptFontSizes.Normal, 0, 48)]
    [InlineData(58, ReceiptFontSizes.Small, 0, 40)]
    [InlineData(80, ReceiptFontSizes.Large, 0, 36)]
    [InlineData(80, ReceiptFontSizes.Normal, 3, 44)]
    [InlineData(210, ReceiptFontSizes.Normal, 0, 126)]
    public void Width_font_and_margins_give_the_column_count(int paper, string font, double margin, int columns) =>
        Assert.Equal(columns, ReceiptLayout.Columns(paper, font, margin));

    [Fact]
    public void Legacy_text_api_keeps_historical_widths()
    {
        Assert.Equal(32, ReceiptText.CharsFor(58));
        Assert.Equal(48, ReceiptText.CharsFor(80));
    }

    [Theory]
    [InlineData(58, ReceiptFontSizes.Large)]
    [InlineData(58, ReceiptFontSizes.Normal)]
    [InlineData(80, ReceiptFontSizes.Small)]
    [InlineData(210, ReceiptFontSizes.Normal)]
    public void Every_line_fits_the_columns_and_long_names_wrap(int paper, string font)
    {
        var s = new ReceiptSettings { FontSize = font, ShowItemCode = true, TotalInWords = true, Address = "12 rue Didouche Mourad, Alger Centre, 16000 Algérie" };
        var doc = ReceiptLayout.Build(Sale(), s, paper, "fr");
        Assert.All(doc.TextLines(), l => Assert.True(l.Length <= doc.Page.Columns, $"{l.Length} > {doc.Page.Columns}: {l}"));
        // Wrapped, not cut: every word of the long product name is printed.
        var words = string.Join(' ', doc.TextLines()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.All("A very long product name that will not fit on thermal paper at all".Split(' '), w => Assert.Contains(w, words));
    }

    [Fact]
    public void Wrap_breaks_on_words_and_cuts_overlong_words()
    {
        Assert.Equal(["Merci de", "votre", "visite"], ReceiptLayout.Wrap("Merci de votre visite", 8));
        Assert.Equal(["ABCDE", "FGH"], ReceiptLayout.Wrap("ABCDEFGH", 5));
        Assert.Equal([""], ReceiptLayout.Wrap("", 10));
    }

    [Fact]
    public void Defaults_reproduce_the_historical_receipt()
    {
        var text = ReceiptLayout.Build(Sale(), new ReceiptSettings(), 80, "ar").ToText(); // default receipt language is English
        foreach (var expected in new[] { "VELTRIX DEMO", "Alger", "W-0001", "Cashier", "Customer", "Subtotal", "Tax", "TOTAL DZD", "Cash", "Change", "Merci" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("6130000000013", text);
        Assert.DoesNotContain("In words", text);
    }

    [Fact]
    public void Body_toggles_hide_their_lines()
    {
        var s = new ReceiptSettings { ShowCashier = false, ShowCustomer = false, ShowTax = false, ShowPayments = false, ShowUnitPrice = false, ShowSaleNumber = false, ShowDate = false };
        var text = string.Join('\n', Lines(s));
        foreach (var hidden in new[] { "Cashier", "Customer", "Tax", "Cash", "Change", "150.00", "W-0001", "2026" }) Assert.DoesNotContain(hidden, text);
        Assert.Contains("2 x", text);
        Assert.Contains("TOTAL DZD", text);
    }

    [Fact]
    public void Discount_lines_follow_the_toggle()
    {
        var r = Sale(discount: 30m);
        Assert.Contains(Lines(new ReceiptSettings(), r: r), l => l.Contains("Discount") && l.EndsWith("-30.00"));
        Assert.DoesNotContain(Lines(new ReceiptSettings { ShowDiscount = false }, r: r), l => l.Contains("Discount"));
    }

    [Fact]
    public void Item_code_header_fields_and_footer_are_printed_when_set()
    {
        var s = new ReceiptSettings
        {
            Language = "fr", ShowItemCode = true, ShopName = "Supérette El Amel", Phone = "0550 00 00 00", Nif = "000016001234567", Rc = "16/00-1234567B21",
            HeaderLines = "Ouvert 7j/7", FooterLines = "Merci de votre visite\nÀ bientôt", ShowSaleBarcode = true,
        };
        var doc = ReceiptLayout.Build(Sale(), s, 80, "fr");
        var text = doc.ToText();
        foreach (var expected in new[] { "Supérette El Amel", "Tél: 0550 00 00 00", "NIF: 000016001234567", "RC: 16/00-1234567B21", "Ouvert 7j/7", "6130000000013",
                     "Merci de votre visite", "À bientôt", "Caissier", "Sous-total" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("VELTRIX DEMO", text);
        Assert.Equal("W-0001", Assert.Single(doc.Rows, r => r.Kind == ReceiptRowKind.Barcode).Text);
        Assert.DoesNotContain(doc.Rows, r => r.Kind == ReceiptRowKind.Logo);
    }

    [Fact]
    public void Totals_block_is_ordered_and_right_aligned()
    {
        var lines = Lines(new ReceiptSettings());
        var order = new[] { "Subtotal", "Tax", "TOTAL DZD", "Cash", "Change" }.Select(k => lines.ToList().FindIndex(l => l.StartsWith(k + " "))).ToList();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
        var total = lines.Single(l => l.StartsWith("TOTAL"));
        Assert.Equal(48, total.Length);
        Assert.EndsWith("300.00", total);
    }

    [Fact]
    public void Arabic_receipt_is_rtl_with_ltr_amounts()
    {
        var doc = ReceiptLayout.Build(Sale(), new ReceiptSettings { Language = "app" }, 58, "ar");
        Assert.True(doc.Page.RightToLeft);
        Assert.Contains(doc.Rows, r => r.Kind == ReceiptRowKind.Pair && r.Text == "المجموع DZD" && r.Right == "300.00");
        Assert.All(doc.Rows.Where(r => r.Text.StartsWith("2 x")), r => Assert.True(r.Ltr));
        Assert.False(ReceiptLayout.Build(Sale(), new ReceiptSettings { Language = "fr" }, 58, "ar").Page.RightToLeft);
    }

    [Fact]
    public void Logo_and_copies_reach_the_page()
    {
        var page = ReceiptLayout.Page(58, new ReceiptSettings { LogoPath = "C:/x/logo.png", Copies = 3, MarginMm = 2 }, "en");
        Assert.Equal(3, page.Copies);
        Assert.Equal(44, page.ContentWidthMm);
        Assert.Equal(ReceiptRowKind.Logo, ReceiptLayout.Build(Sale(), new ReceiptSettings { LogoPath = "C:/x/logo.png" }, page).Rows[0].Kind);
    }

    [Theory]
    [InlineData(1222, "mille deux cent vingt-deux dinars")]
    [InlineData(80, "quatre-vingts dinars")]
    [InlineData(71, "soixante et onze dinars")]
    [InlineData(200000, "deux cent mille dinars")]
    [InlineData(1, "un dinar")]
    [InlineData(2500000.5, "deux millions cinq cent mille dinars et cinquante centimes")]
    public void Total_in_french_words(decimal amount, string expected) => Assert.Equal(expected, AmountInWords.French(amount, "DZD"));

    [Fact]
    public void Settings_without_receipt_section_load_with_defaults_and_values_are_clamped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "veltrix-rcpt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "language": "fr", "receiptPaperMm": 58 }""");
            var s = new SettingsStore(path).Load();
            Assert.Equal(58, s.ReceiptPaperMm);
            Assert.True(s.Receipt.ShowTax);
            Assert.Equal(1, s.Receipt.Copies);
            Assert.Equal("en", s.Receipt.Language);

            File.WriteAllText(path, """{ "receiptPaperMm": 210, "receipt": { "copies": 9, "fontSize": "huge", "dateFormat": "x", "language": "de" } }""");
            s = new SettingsStore(path).Load();
            Assert.Equal(ReceiptPaper.A4, s.ReceiptPaperMm);
            Assert.Equal(3, s.Receipt.Copies);
            Assert.Equal(ReceiptFontSizes.Normal, s.Receipt.FontSize);
            Assert.Equal(ReceiptDateFormats.Default, s.Receipt.DateFormat);
            Assert.Equal("en", s.Receipt.Language);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Receipts_saved_before_item_codes_still_deserialize()
    {
        var json = """{"name":"Pain","quantity":1,"unitPrice":25,"discount":0,"total":25}""";
        var line = JsonSerializer.Deserialize<ReceiptLine>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(line.Code);
        Assert.Equal("Pain", line.Name);
    }
}
