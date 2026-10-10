using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Veltrix.Contracts.Reporting;

namespace Veltrix.Modules.Reporting;

/// <summary>Dependency-free exporters for report tables.</summary>
public static class ReportExporters
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static (byte[] Content, string ContentType, string Extension) Export(ReportTable t, string format) => format.ToLowerInvariant() switch
    {
        "csv" => (Csv(t), "text/csv; charset=utf-8", "csv"),
        "xlsx" or "excel" => (Xlsx(t), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
        "pdf" => (Pdf(t), "application/pdf", "pdf"),
        "html" or "print" => (Encoding.UTF8.GetBytes(Html(t)), "text/html; charset=utf-8", "html"),
        _ => throw new Veltrix.SharedKernel.ValidationException(new Dictionary<string, string[]> { ["format"] = ["Use csv, xlsx, pdf or html."] }),
    };

    private static string Cell(object? v) => v switch
    {
        null => "",
        decimal d => d.ToString("0.00", Inv),
        double d => d.ToString("0.##", Inv),
        DateOnly d => d.ToString("yyyy-MM-dd", Inv),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    public static byte[] Csv(ReportTable t)
    {
        var sb = new StringBuilder();
        static string Esc(string s) => s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        sb.AppendLine(string.Join(';', t.Columns.Select(c => Esc(c.Title))));
        foreach (var r in t.Rows) sb.AppendLine(string.Join(';', r.Select(v => Esc(Cell(v)))));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static byte[] Xlsx(ReportTable t)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string path, string content)
            {
                var e = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Add("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            var sheetName = SecurityElement.Escape(t.Title.Length > 31 ? t.Title[..31] : t.Title);
            Add("xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{sheetName}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
            void Row(int n, IEnumerable<object?> values)
            {
                sb.Append($"<row r=\"{n}\">");
                foreach (var v in values)
                {
                    if (v is decimal or int or long or double) sb.Append($"<c t=\"n\"><v>{Cell(v)}</v></c>");
                    else sb.Append($"<c t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(Cell(v))}</t></is></c>");
                }
                sb.Append("</row>");
            }
            Row(1, t.Columns.Select(c => (object?)c.Title));
            var i = 2;
            foreach (var r in t.Rows) Row(i++, r);
            sb.Append("</sheetData></worksheet>");
            Add("xl/worksheets/sheet1.xml", sb.ToString());
        }
        return ms.ToArray();
    }

    public static string Html(ReportTable t)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var head = string.Join("", t.Columns.Select(c => $"<th>{E(c.Title)}</th>"));
        var body = string.Join("", t.Rows.Select(r => "<tr>" + string.Join("", r.Select((v, i) => $"<td class='{(t.Columns[i].Type is "money" or "number" ? "n" : "")}'>{E(Cell(v))}</td>")) + "</tr>"));
        var summary = string.Join("", t.Summary.Select(s => $"<div><b>{E(s.Key)}:</b> {E(Cell(s.Value))}</div>"));
        return $"<!doctype html><html dir='auto'><head><meta charset='utf-8'><title>{E(t.Title)}</title><style>body{{font-family:Arial,'Noto Naskh Arabic',sans-serif;font-size:12px;margin:24px}}table{{border-collapse:collapse;width:100%}}th,td{{border:1px solid #ccc;padding:4px}}th{{background:#0D1D33;color:#F5F8FF}}.n{{text-align:right}}@media print{{body{{margin:0}}}}</style></head><body><h2>{E(t.Title)}</h2><div>{t.GeneratedAt:yyyy-MM-dd HH:mm} UTC</div>{summary}<table><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table><p style='font-size:10px;color:#666'>VELTRIX OS — Powering Business Intelligence.</p></body></html>";
    }

    /// <summary>Minimal PDF (Helvetica/WinAnsi). Characters outside Latin-1 (e.g. Arabic) are not supported by the base
    /// fonts and are replaced; use the HTML/print export for Arabic reports.</summary>
    public static byte[] Pdf(ReportTable t)
    {
        const float W = 842, H = 595, Margin = 36, LineH = 13;
        var colW = (W - 2 * Margin) / Math.Max(1, t.Columns.Count);
        var maxChars = Math.Max(4, (int)(colW / 5.2));
        var lines = new List<string[]> { t.Columns.Select(c => c.Title).ToArray() };
        lines.AddRange(t.Rows.Select(r => r.Select(Cell).ToArray()));
        var perPage = (int)((H - 2 * Margin - 40) / LineH);
        var pages = lines.Skip(1).Chunk(perPage).ToList();
        if (pages.Count == 0) pages.Add([]);

        static string Pdf8(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                var c = ch > 255 ? '?' : ch;
                if (c is '(' or ')' or '\\') sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }
        string Fit(string s) => s.Length > maxChars ? s[..(maxChars - 1)] + "." : s;

        var objects = new List<string>();
        var pageIds = new List<int>();
        // 1 catalog, 2 pages, 3 font, 4 bold font, then (content, page) pairs.
        var contents = new List<string>();
        for (var p = 0; p < pages.Count; p++)
        {
            var sb = new StringBuilder();
            sb.Append($"BT /F2 14 Tf {Margin} {H - Margin} Td ({Pdf8(t.Title)}) Tj ET\n");
            sb.Append($"BT /F1 8 Tf {W - Margin - 120} {H - Margin} Td (Page {p + 1}/{pages.Count}) Tj ET\n");
            var y = H - Margin - 30;
            void Row(string[] cells, string font)
            {
                for (var i = 0; i < cells.Length; i++)
                    sb.Append($"BT /{font} 8 Tf {Margin + i * colW:0.##} {y:0.##} Td ({Pdf8(Fit(cells[i]))}) Tj ET\n");
                y -= LineH;
            }
            Row(lines[0], "F2");
            sb.Append($"{Margin} {y + LineH - 3:0.##} m {W - Margin} {y + LineH - 3:0.##} l S\n");
            foreach (var r in pages[p]) Row(r, "F1");
            contents.Add(sb.ToString());
        }
        var kids = new StringBuilder();
        for (var p = 0; p < pages.Count; p++) { pageIds.Add(5 + p * 2 + 1); kids.Append($"{5 + p * 2 + 1} 0 R "); }
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var p = 0; p < pages.Count; p++)
        {
            var bytes = Encoding.Latin1.GetBytes(contents[p]);
            objects.Add($"<< /Length {bytes.Length} >>\nstream\n{contents[p]}endstream");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {5 + p * 2} 0 R >>");
        }
        var output = new MemoryStream();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); output.Write(b); }
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
