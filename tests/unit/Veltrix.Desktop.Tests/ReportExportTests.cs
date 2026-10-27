using System.IO.Compression;
using System.Text;
using Veltrix.Desktop.Core.Reporting;

namespace Veltrix.Desktop.Tests;

public sealed class ReportExportTests
{
    private sealed record Row(string Name, decimal Hours, int Days, string Note);

    [Fact]
    public void Xlsx_is_a_valid_package_with_numbers_text_and_rtl_for_arabic()
    {
        var t = ReportExport.FromRows([new Row("أمين", 7.5m, 3, "=CMD()"), new Row("Sara <&>", 8m, 1, "")], "attendance", "الحضور");
        using var ms = new MemoryStream();
        ReportExport.WriteXlsx(t, ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms);
        Assert.Equal(["[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml"],
            zip.Entries.Select(e => e.FullName));
        var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
        Assert.Contains("rightToLeft=\"1\"", sheet);
        Assert.Contains("<c r=\"B2\"><v>7.5</v></c>", sheet);
        Assert.Contains("<t xml:space=\"preserve\">=CMD()</t>", sheet); // text, never a formula
        Assert.Contains("Sara &lt;&amp;&gt;", sheet);
        Assert.DoesNotContain("<f>", sheet);
        Assert.Equal("AA", ReportExport.ColumnName(26));
        System.Xml.Linq.XDocument.Parse(sheet); // well-formed
    }

    [Fact]
    public void Pdf_has_one_page_per_image_and_a_valid_xref()
    {
        var white = Enumerable.Repeat((byte)255, 20 * 30 * 3).ToArray();
        using var ms = new MemoryStream();
        ReportExport.WritePdf([(white, 20, 30), (white, 20, 30)], 595.28, 841.89, ms, "Rapport الحضور");
        var pdf = Encoding.Latin1.GetString(ms.ToArray());
        Assert.StartsWith("%PDF-1.4", pdf);
        Assert.Contains("/Type /Pages /Count 2", pdf);
        Assert.EndsWith("%%EOF\n", pdf);
        var xref = int.Parse(pdf[(pdf.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref", pdf[xref..]);
        // Every object offset in the xref table points at "N 0 obj".
        var lines = pdf[xref..].Split('\n').Skip(3).TakeWhile(l => l.EndsWith(" n ", StringComparison.Ordinal)).ToList();
        for (var i = 0; i < lines.Count; i++) Assert.StartsWith($"{i + 1} 0 obj", pdf[int.Parse(lines[i][..10])..]);
    }
}
