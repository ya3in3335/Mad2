using Veltrix.Desktop.Core.Pos;

namespace Veltrix.Desktop.Tests;

public sealed class ReceiptDesignTests
{
    public static IEnumerable<object[]> Cases() =>
        from t in ReceiptTemplates.All
        from lang in new[] { "fr", "ar", "en" }
        from paper in ReceiptTemplates.IsCompany(t) ? new[] { ReceiptPaper.A4 } : new[] { ReceiptPaper.Mm58, ReceiptPaper.Mm80 }
        select new object[] { t, lang, paper };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_design_fits_the_paper_and_shows_the_essentials(string template, string lang, int paper)
    {
        var s = new ReceiptSettings { Template = template, Language = lang, ShopName = "Superette El Amel", Nif = "000116001234567" };
        s.Normalize();
        var doc = ReceiptLayout.Build(ReceiptLayout.Sample(lang, null, new DateTimeOffset(2026, 3, 21, 14, 35, 0, TimeSpan.Zero)), s, paper, lang);
        var lines = doc.TextLines();
        Assert.All(lines, l => Assert.True(l.Length <= doc.Page.Columns, $"'{l}' wider than {doc.Page.Columns}"));
        var text = string.Join('\n', lines);
        Assert.Contains("Superette El Amel", text);
        Assert.Contains("R-000123", text);
        if (template != ReceiptTemplates.Delivery) Assert.Contains("1,222.00", text);
        else Assert.DoesNotContain("450.00", text); // delivery note: no prices
        if (template is ReceiptTemplates.Invoice or ReceiptTemplates.Proforma) Assert.Contains("mille deux cent vingt-deux dinars", text);
    }

    [Fact]
    public void Unknown_template_falls_back_to_classic_and_classic_is_unchanged()
    {
        var s = new ReceiptSettings { Template = "nope" };
        s.Normalize();
        Assert.Equal(ReceiptTemplates.Classic, s.Template);
        var sample = ReceiptLayout.Sample("en", null, DateTimeOffset.UnixEpoch);
        Assert.Equal(ReceiptLayout.Build(sample, new ReceiptSettings(), 80, "en").ToText(), ReceiptLayout.Build(sample, s, 80, "en").ToText());
    }
}
