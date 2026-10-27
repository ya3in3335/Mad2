using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Text;
using Veltrix.Contracts.Reporting;

namespace Veltrix.Desktop.Core.Reporting;

/// <summary>Report exports without third-party libraries: CSV, Excel (.xlsx, Office Open XML) and PDF (from rendered page images).</summary>
public static class ReportExport
{
    public static readonly string SaveFilter = "Excel (*.xlsx)|*.xlsx|PDF (*.pdf)|*.pdf|CSV (*.csv)|*.csv";

    /// <summary>Table of any row model (public properties in declaration order).</summary>
    public static ReportTable FromRows<T>(IEnumerable<T> rows, string key, string? title = null)
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).ToArray();
        return new ReportTable(key, title ?? typeof(T).Name, props.Select(p => new ReportColumn(p.Name, p.Name, p.PropertyType.Name)).ToList(),
            rows.Select(r => (IReadOnlyList<object?>)props.Select(p => p.GetValue(r) switch
            {
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                var v => v,
            }).ToList()).ToList(),
            new Dictionary<string, object?>(), DateTimeOffset.UtcNow, new Dictionary<string, string?>());
    }

    public static bool HasArabic(ReportTable t) =>
        t.Title.Any(IsArabic) || t.Columns.Any(c => c.Title.Any(IsArabic)) || t.Rows.Take(50).Any(r => r.Any(v => v is string s && s.Any(IsArabic)));

    private static bool IsArabic(char c) => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﻿';

    /// <summary>Writes a one-sheet workbook: bold header row, numbers as numbers, text as inline strings (never formulas), RTL sheet for Arabic.</summary>
    public static void WriteXlsx(ReportTable t, Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Entry(string path, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        Entry("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
        Entry("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        var sheetName = SecurityElement.Escape(new string(t.Title.Where(c => !"[]:*?/\\".Contains(c)).Take(31).ToArray()) is { Length: > 0 } n ? n : "Report");
        Entry("xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{sheetName}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        Entry("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
        Entry("xl/styles.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf fontId="0"/><xf fontId="1" applyFont="1"/></cellXfs></styleSheet>""");

        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        sb.Append(HasArabic(t) ? """<sheetViews><sheetView rightToLeft="1" workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>"""
            : """<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        sb.Append("<sheetData>");
        AppendRow(sb, 1, t.Columns.Select(c => (object?)c.Title).ToList(), style: 1);
        var r = 2;
        foreach (var row in t.Rows) AppendRow(sb, r++, row, style: 0);
        sb.Append("</sheetData></worksheet>");
        Entry("xl/worksheets/sheet1.xml", sb.ToString());
    }

    private static void AppendRow(StringBuilder sb, int r, IReadOnlyList<object?> values, int style)
    {
        sb.Append("<row r=\"").Append(r).Append("\">");
        for (var c = 0; c < values.Count; c++)
        {
            var cell = ColumnName(c) + r.ToString(CultureInfo.InvariantCulture);
            var s = style == 1 ? " s=\"1\"" : "";
            switch (values[c])
            {
                case null: break;
                case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    sb.Append("<c r=\"").Append(cell).Append('"').Append(s).Append("><v>")
                      .Append(Convert.ToString(values[c], CultureInfo.InvariantCulture)).Append("</v></c>");
                    break;
                case bool b:
                    sb.Append("<c r=\"").Append(cell).Append("\" t=\"b\"").Append(s).Append("><v>").Append(b ? 1 : 0).Append("</v></c>");
                    break;
                default:
                    var text = Convert.ToString(values[c], CultureInfo.InvariantCulture) ?? "";
                    sb.Append("<c r=\"").Append(cell).Append("\" t=\"inlineStr\"").Append(s).Append("><is><t xml:space=\"preserve\">")
                      .Append(XmlText(text)).Append("</t></is></c>");
                    break;
            }
        }
        sb.Append("</row>");
    }

    /// <summary>XML-escaped text with characters that are invalid in XML 1.0 removed.</summary>
    private static string XmlText(string s) =>
        SecurityElement.Escape(new string(s.Where(ch => ch is '\t' or '\n' or '\r' || ch >= 0x20 && ch != '￾' && ch != '￿').ToArray()));

    public static string ColumnName(int index)
    {
        var name = "";
        for (var i = index + 1; i > 0; i = (i - 1) / 26) name = (char)('A' + (i - 1) % 26) + name;
        return name;
    }

    /// <summary>
    /// Writes a PDF whose pages are the given RGB images (each scaled to <paramref name="pageWidthPt"/> × <paramref name="pageHeightPt"/>),
    /// Flate-compressed. The images come from the application's own layout engine, so Arabic shaping and RTL are exactly as on screen.
    /// </summary>
    public static void WritePdf(IReadOnlyList<(byte[] Rgb, int Width, int Height)> pages, double pageWidthPt, double pageHeightPt, Stream output, string title)
    {
        var offsets = new List<long>();
        var w = output;
        void Raw(string s) { var b = Encoding.ASCII.GetBytes(s); w.Write(b); }
        void Obj(Action body) { offsets.Add(w.Position); Raw($"{offsets.Count} 0 obj\n"); body(); Raw("\nendobj\n"); }
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        Raw("%PDF-1.4\n%âãÏÓ\n");
        var n = pages.Count;
        // Objects: 1 catalog, 2 pages, 3 info, then per page: page, content, image.
        int PageObj(int i) => 4 + i * 3;
        Obj(() => Raw("<< /Type /Catalog /Pages 2 0 R >>"));
        Obj(() => Raw($"<< /Type /Pages /Count {n} /Kids [{string.Join(' ', Enumerable.Range(0, n).Select(i => $"{PageObj(i)} 0 R"))}] >>"));
        Obj(() => Raw($"<< /Title <FEFF{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(title))}> /Producer (VELTRIX OS) >>"));
        for (var i = 0; i < n; i++)
        {
            var (rgb, iw, ih) = pages[i];
            var page = PageObj(i);
            Obj(() => Raw($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(pageWidthPt)} {F(pageHeightPt)}] /Resources << /XObject << /Im0 {page + 2} 0 R >> >> /Contents {page + 1} 0 R >>"));
            var content = Encoding.ASCII.GetBytes($"q {F(pageWidthPt)} 0 0 {F(pageHeightPt)} 0 0 cm /Im0 Do Q");
            Obj(() => { Raw($"<< /Length {content.Length} >>\nstream\n"); w.Write(content); Raw("\nendstream"); });
            var compressed = Deflate(rgb);
            Obj(() =>
            {
                Raw($"<< /Type /XObject /Subtype /Image /Width {iw} /Height {ih} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
                w.Write(compressed);
                Raw("\nendstream");
            });
        }
        var xref = w.Position;
        Raw($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Raw($"{o.ToString("0000000000", CultureInfo.InvariantCulture)} 00000 n \n");
        Raw($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }
}
