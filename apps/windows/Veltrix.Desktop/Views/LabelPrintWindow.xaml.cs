using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Veltrix.Desktop.Core.Barcodes;
using Veltrix.Desktop.Presentation.ViewModels;
using Veltrix.Desktop.Printing;

namespace Veltrix.Desktop.Views;

/// <summary>Barcode label dialog: options, live preview and printing through the Windows driver (thermal label or A4 printers).</summary>
public partial class LabelPrintWindow : Window
{
    private readonly LabelPrintViewModel _vm;

    public LabelPrintWindow(LabelPrintViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        PrinterText.Text = vm.PrinterName ?? "—";
        Loaded += (_, _) => Render();
    }

    private (FixedDocument Doc, PageMediaSize Media, int Count)? Build()
    {
        try
        {
            var labels = _vm.BuildLabels();
            if (labels.Count == 0) { _vm.Message = _vm.Text("dk.labels.noSelection"); return null; }
            if (_vm.SheetMode)
            {
                var (rows, cols, margin, gap) = _vm.Sheet();
                return (LabelRenderer.Sheet(labels, rows, cols, margin, gap), new PageMediaSize(PageMediaSizeName.ISOA4,
                    LabelLayout.MmToDip(LabelLayout.A4WidthMm), LabelLayout.MmToDip(LabelLayout.A4HeightMm)), labels.Count);
            }
            var (w, h) = _vm.LabelSizeMm();
            return (LabelRenderer.LabelRoll(labels, w, h), new PageMediaSize(LabelLayout.MmToDip(w), LabelLayout.MmToDip(h)), labels.Count);
        }
        catch (Exception e)
        {
            _vm.Message = _vm.Describe(e);
            return null;
        }
    }

    private void Render()
    {
        if (Build() is not { } built) { Viewer.Document = null; return; }
        Viewer.Document = built.Doc;
        _vm.Message = _vm.Format("dk.labels.count", built.Count);
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => Render();

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (Build() is not { } built) return;
        Viewer.Document = built.Doc;
        var dialog = new PrintDialog { UserPageRangeEnabled = true };
        TrySelectPrinter(dialog, _vm.PrinterName);
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (dialog.PrintTicket is { } ticket)
            {
                ticket.PageMediaSize = built.Media;
                ticket.PageOrientation = PageOrientation.Portrait;
            }
            dialog.PrintDocument(built.Doc.DocumentPaginator, _vm.Text("dk.labels.title"));
            _vm.Remember(dialog.PrintQueue?.FullName);
            PrinterText.Text = _vm.PrinterName ?? "—";
            _vm.Message = _vm.Format("dk.labels.count", built.Count);
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
        {
            _vm.Message = _vm.Describe(ex);
        }
    }

    /// <summary>Pre-selects the last used printer (local or network connection) when it still exists.</summary>
    private static void TrySelectPrinter(PrintDialog dialog, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var server = new LocalPrintServer();
            var queue = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .FirstOrDefault(q => string.Equals(q.FullName, name, StringComparison.OrdinalIgnoreCase));
            if (queue is null) return;
            dialog.PrintQueue = queue;
            dialog.PrintTicket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
        }
        catch (PrintSystemException) { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
