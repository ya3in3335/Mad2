using System.Windows;
using System.Windows.Controls;
using Veltrix.Contracts.Catalog;
using Veltrix.Desktop.Platform;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

public partial class ProductsView : UserControl
{
    public ProductsView() => InitializeComponent();

    /// <summary>Opens the label dialog for the selected products (multi-select with Ctrl/Shift).</summary>
    private async void PrintLabels_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProductsViewModel vm) return;
        var products = Grid.SelectedItems.OfType<ProductDto>().ToList();
        if (products.Count == 0 && vm.Selected is { } one) products.Add(one);
        if (products.Count == 0)
        {
            vm.StatusMessage = (Application.Current.TryFindResource("dk.labels.noSelection") as string) ?? "";
            return;
        }
        try { products = await vm.EnsureBarcodesAsync(products); }
        catch (Exception ex) { vm.StatusMessage = ex.Message; return; }
        var labels = vm.CreateLabelPrint(products);
        var window = new LabelPrintWindow(labels) { Owner = Window.GetWindow(this) };
        WpfPlatformUi.ApplyTo(window, labels.Language);
        window.ShowDialog();
    }
}
