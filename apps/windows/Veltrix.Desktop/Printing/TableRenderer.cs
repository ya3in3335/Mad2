using System.Data;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Veltrix.Desktop.Printing;

/// <summary>Renders a report table (title + header row + rows) as a printable FlowDocument (print to "Microsoft Print to PDF" for a PDF).</summary>
public static class TableRenderer
{
    public static FlowDocument Render(string title, DataTable table, FlowDirection direction)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 10, PagePadding = new Thickness(36), FlowDirection = direction,
            Background = Brushes.White, Foreground = Brushes.Black,
        };
        doc.Blocks.Add(new Paragraph(new Run(title)) { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        doc.Blocks.Add(new Paragraph(new Run(DateTime.Now.ToString("g", System.Globalization.CultureInfo.CurrentCulture)))
        {
            FontSize = 9, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10),
        });
        var t = new Table { CellSpacing = 0, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) };
        foreach (DataColumn _ in table.Columns) t.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        var header = new TableRow { Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)), FontWeight = FontWeights.SemiBold };
        foreach (DataColumn c in table.Columns) header.Cells.Add(Cell(c.ColumnName.Trim()));
        group.Rows.Add(header);
        foreach (DataRow r in table.Rows)
        {
            var row = new TableRow();
            foreach (var v in r.ItemArray) row.Cells.Add(Cell(v?.ToString() ?? ""));
            group.Rows.Add(row);
        }
        t.RowGroups.Add(group);
        doc.Blocks.Add(t);
        return doc;
    }

    private static TableCell Cell(string text) =>
        new(new Paragraph(new Run(text)) { Margin = new Thickness(0) }) { Padding = new Thickness(3, 2, 3, 2), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0.5) };
}
