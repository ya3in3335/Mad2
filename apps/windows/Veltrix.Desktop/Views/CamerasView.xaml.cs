using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Veltrix.Desktop.Controls;
using Veltrix.Desktop.Platform;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

public partial class CamerasView : UserControl
{
    private CamerasViewModel? _vm;

    public CamerasView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.EditorRequested -= OnEditorRequested;
            _vm = e.NewValue as CamerasViewModel;
            if (_vm is not null) _vm.EditorRequested += OnEditorRequested;
        };
        // Leaving the page unloads the tiles, which stops and disposes every player (CameraTile.Unloaded).
        Unloaded += (_, _) => { if (_vm is not null) _vm.EditorRequested -= OnEditorRequested; };
        Loaded += (_, _) => { if (_vm is not null) { _vm.EditorRequested -= OnEditorRequested; _vm.EditorRequested += OnEditorRequested; } };
    }

    private void OnEditorRequested(object? sender, CameraEditorViewModel editor)
    {
        var window = new CameraEditWindow(editor) { Owner = Window.GetWindow(this) };
        WpfPlatformUi.ApplyTo(window, App.Services?.Settings.Language ?? "en");
        window.ShowDialog();
    }

    private void Tile_DoubleClicked(object? sender, EventArgs e)
    {
        if (_vm is null || sender is not CameraTile { DataContext: CameraTileViewModel tile }) return;
        var window = new Window
        {
            Owner = Window.GetWindow(this), WindowStyle = WindowStyle.None, WindowState = WindowState.Maximized, ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Black, ShowInTaskbar = false, Title = tile.Name,
        };
        var player = new CameraTile { Feed = _vm.FullscreenFeed(tile), ShowSnapshotButton = true };
        player.DoubleClicked += (_, _) => window.Close();
        player.SnapshotRequested += (_, _) => Snapshot(player, tile);
        window.Content = player;
        window.KeyDown += (_, k) => { if (k.Key == Key.Escape) window.Close(); };
        window.ShowDialog();
    }

    private void Tile_SnapshotRequested(object? sender, EventArgs e)
    {
        if (sender is CameraTile { DataContext: CameraTileViewModel tile } player) Snapshot(player, tile);
    }

    private void Snapshot(CameraTile player, CameraTileViewModel tile)
    {
        if (_vm is null || App.Services is not { } s) return;
        if (!player.IsLive) { _vm.ReportSnapshot(null); return; }
        var path = s.Ui.PickSaveFile(_vm.SnapshotFileName(tile), "JPEG (*.jpg)|*.jpg");
        if (path is null) return;
        if (!path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) path += ".jpg";
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        _vm.ReportSnapshot(player.TakeSnapshot(path) ? path : null);
    }

    private void Cameras_MouseDoubleClick(object sender, MouseButtonEventArgs e) => _vm?.EditCommand.Execute(null);
}
