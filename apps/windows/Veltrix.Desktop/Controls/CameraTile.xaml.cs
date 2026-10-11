using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Veltrix.Desktop.Core.Cameras;
using Veltrix.Desktop.Platform;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Controls;

/// <summary>
/// Live camera view: plays <see cref="Feed"/> while loaded, shows an "offline" overlay and reconnects with exponential backoff
/// (1 s → 30 s) when the stream errors, ends or stalls. The player is stopped and disposed when the tile is unloaded (page leave).
/// </summary>
public partial class CameraTile : UserControl
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    public static readonly DependencyProperty FeedProperty = DependencyProperty.Register(nameof(Feed), typeof(CameraFeed), typeof(CameraTile),
        new PropertyMetadata(null, (d, _) => ((CameraTile)d).Restart()));
    public static readonly DependencyProperty ShowSnapshotButtonProperty = DependencyProperty.Register(nameof(ShowSnapshotButton), typeof(bool), typeof(CameraTile),
        new PropertyMetadata(false, (d, e) => ((CameraTile)d).SnapshotButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));
    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(CameraTile),
        new PropertyMetadata(null, (d, e) => ((CameraTile)d).CloseButton.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible));

    private readonly DispatcherTimer _retry = new();
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(5) };
    private MediaPlayer? _player;
    private bool _active;
    private int _attempt;
    private int _generation;
    private long _lastTime = -1;
    private int _stalls;

    public CameraTile()
    {
        InitializeComponent();
        _retry.Tick += (_, _) => { _retry.Stop(); Restart(); };
        _watchdog.Tick += (_, _) => CheckStall();
        // Plays only while loaded AND visible (a collapsed POS tile must not keep a stream open).
        Loaded += (_, _) => UpdateActive();
        Unloaded += (_, _) => UpdateActive();
        IsVisibleChanged += (_, _) => UpdateActive();
    }

    public CameraFeed? Feed { get => (CameraFeed?)GetValue(FeedProperty); set => SetValue(FeedProperty, value); }
    public bool ShowSnapshotButton { get => (bool)GetValue(ShowSnapshotButtonProperty); set => SetValue(ShowSnapshotButtonProperty, value); }
    /// <summary>When set, a "×" button in the corner executes it (POS tile: hide).</summary>
    public ICommand? CloseCommand { get => (ICommand?)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    public event EventHandler? DoubleClicked;
    public event EventHandler? SnapshotRequested;

    public bool IsLive { get; private set; }

    /// <summary>Saves the current frame as JPEG (libVLC writes it asynchronously). False when nothing is playing.</summary>
    public bool TakeSnapshot(string path) => IsLive && _player is { } p && p.TakeSnapshot(0, path, 0, 0);

    private void UpdateActive()
    {
        var active = IsLoaded && IsVisible;
        if (active == _active) return;
        _active = active;
        if (active) Restart();
        else
        {
            _generation++;
            _retry.Stop();
            StopPlayer();
        }
    }

    private async void Restart()
    {
        var generation = ++_generation;
        _retry.Stop();
        NameText.Text = Feed?.Name ?? "";
        if (!_active) return;
        StopPlayer();
        if (Feed is not { } feed) { SetStatus("cam.noCamera"); return; }
        if (!VlcRuntime.TryGet(out var vlc)) { SetStatus("cam.videoUnavailable"); return; }
        SetStatus("cam.connecting");
        // LAN only: refuse hosts that are (or resolve to) public addresses.
        var lan = RtspUrlBuilder.TryGetEndpoint(feed.Url, out var host, out _) && await LanAddress.ResolveLocalAsync(host, CancellationToken.None) is not null;
        if (generation != _generation || !_active) return;
        if (!lan) { SetStatus(LanAddress.IsAcceptableHostText(host) ? "cam.offline" : "cam.err.notLan"); ScheduleRetry(); return; }
        var player = _player = new MediaPlayer(vlc) { EnableMouseInput = false, EnableKeyInput = false, EnableHardwareDecoding = true };
        player.Playing += OnPlaying;
        player.EncounteredError += OnLost;
        player.EndReached += OnLost;
        Video.MediaPlayer = player;
        using var media = VlcRuntime.CreateMedia(vlc, feed);
        player.Play(media);
        _lastTime = -1;
        _stalls = 0;
        _watchdog.Start();
    }

    private void OnPlaying(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!ReferenceEquals(sender, _player)) return;
        _attempt = 0;
        IsLive = true;
        OfflinePanel.Visibility = Visibility.Collapsed;
    });

    private void OnLost(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!ReferenceEquals(sender, _player) || !_active) return;
        Lost();
    });

    private void Lost()
    {
        IsLive = false;
        StopPlayer();
        SetStatus("cam.offline");
        ScheduleRetry();
    }

    private void ScheduleRetry()
    {
        var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, _attempt)));
        _attempt = Math.Min(_attempt + 1, 10);
        _retry.Interval = delay;
        _retry.Start();
    }

    /// <summary>RTSP over TCP can hang without an error when a camera drops off Wi-Fi: no clock progress for ~10 s counts as a disconnect.</summary>
    private void CheckStall()
    {
        if (_player is not { } p || !_active) { _watchdog.Stop(); return; }
        if (!IsLive) return;
        var t = p.Time;
        _stalls = t == _lastTime ? _stalls + 1 : 0;
        _lastTime = t;
        if (_stalls >= 2) Lost();
    }

    private void StopPlayer()
    {
        _watchdog.Stop();
        IsLive = false;
        if (_player is not { } p) return;
        _player = null;
        p.Playing -= OnPlaying;
        p.EncounteredError -= OnLost;
        p.EndReached -= OnLost;
        Video.MediaPlayer = null;
        // Stop can block for seconds on a dead network stream: never on the UI thread.
        _ = Task.Run(() =>
        {
            try { p.Stop(); }
            finally { p.Dispose(); }
        });
    }

    private void SetStatus(string key)
    {
        OfflinePanel.Visibility = Visibility.Visible;
        StatusText.SetResourceReference(TextBlock.TextProperty, key);
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { DoubleClicked?.Invoke(this, EventArgs.Empty); e.Handled = true; }
    }

    private void Snapshot_Click(object sender, RoutedEventArgs e) => SnapshotRequested?.Invoke(this, EventArgs.Empty);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (CloseCommand?.CanExecute(null) == true) CloseCommand.Execute(null);
    }
}
