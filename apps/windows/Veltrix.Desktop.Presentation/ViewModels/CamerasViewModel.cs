using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Cameras;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>What a video player needs: URL without credentials (they go to the player as options, never into logs) plus the credentials.</summary>
public sealed record CameraFeed(Guid CameraId, string Name, string Url, string Username, string Password)
{
    public static CameraFeed For(CameraConfig cam, CameraStream stream)
    {
        var (user, pass) = (cam.Username, cam.Password);
        if (cam.Brand == CameraBrand.Generic && Uri.TryCreate(cam.CustomUrl, UriKind.Absolute, out var u) && u.UserInfo.Length > 0)
        {
            var parts = u.UserInfo.Split(':', 2);
            (user, pass) = (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return new(cam.Id, cam.Name, RtspUrlBuilder.Build(cam, stream, includeCredentials: false), user, pass);
    }
}

public sealed class CameraTileViewModel(CameraConfig camera, CameraFeed feed)
{
    public CameraConfig Camera { get; } = camera;
    public CameraFeed Feed { get; } = feed;
    public string Name => Camera.Name;
}

public sealed record CameraRow(CameraConfig Camera, string BrandText, string Address, bool IsCashier, bool Enabled);

/// <summary>Cameras page (owner only): live grid (1, 2×2, 3×3) on sub streams, full screen on the main stream, camera management.</summary>
public sealed partial class CamerasViewModel(DesktopServices s) : PageViewModel(s)
{
    private IReadOnlyList<CameraConfig> _all = [];

    public override string TitleKey => "cam.nav";

    public ObservableCollection<CameraTileViewModel> Tiles { get; } = [];
    public ObservableCollection<CameraRow> Cameras { get; } = [];
    public IReadOnlyList<int> GridSizes { get; } = [1, 2, 3];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PageText))] private int _gridSize = 2;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PageText))] private int _pageIndex;
    [ObservableProperty] private CameraRow? _selectedCamera;
    [ObservableProperty] private bool _showManagement;

    /// <summary>Raised to open the add/edit dialog; the view shows it modally and the page reloads afterwards.</summary>
    public event EventHandler<CameraEditorViewModel>? EditorRequested;

    public int PageCount => Math.Max(1, (int)Math.Ceiling(EnabledCameras.Count / (double)(GridSize * GridSize)));
    public string PageText => $"{Math.Min(PageIndex, PageCount - 1) + 1} / {PageCount}";
    private List<CameraConfig> EnabledCameras => _all.Where(c => c.Enabled).ToList();

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (S.Cameras is null) return false;
        _all = await S.Cameras.Repository.ListAsync(ct);
        Cameras.Clear();
        foreach (var c in _all)
            Cameras.Add(new CameraRow(c, c.Brand.ToString(), c.Brand == CameraBrand.Generic ? RtspUrlBuilder.Redact(c.CustomUrl ?? "") : $"{c.Host} · ch {c.Channel}",
                c.IsCashier, c.Enabled));
        RebuildTiles();
        if (_all.Count == 0) ShowManagement = true;
        return _all.Count > 0;
    }

    partial void OnGridSizeChanged(int value)
    {
        PageIndex = 0;
        RebuildTiles();
    }

    partial void OnPageIndexChanged(int value) => RebuildTiles();

    private void RebuildTiles()
    {
        var size = GridSize * GridSize;
        var enabled = EnabledCameras;
        var page = Math.Clamp(PageIndex, 0, PageCount - 1);
        Tiles.Clear();
        // Grid views use the sub stream (low bandwidth/CPU); a single view uses the camera's configured stream.
        foreach (var c in enabled.Skip(page * size).Take(size)) Tiles.Add(new CameraTileViewModel(c, CameraFeed.For(c, GridSize == 1 ? c.Stream : CameraStream.Sub)));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageText));
    }

    [RelayCommand]
    public void SetGrid(string? size)
    {
        if (int.TryParse(size, out var n) && n is 1 or 2 or 3) GridSize = n;
    }

    [RelayCommand]
    public void NextPage() => PageIndex = (PageIndex + 1) % PageCount;

    [RelayCommand]
    public void PreviousPage() => PageIndex = (PageIndex - 1 + PageCount) % PageCount;

    [RelayCommand]
    public void ToggleManagement() => ShowManagement = !ShowManagement;

    /// <summary>Main (full-quality) stream for the full-screen view.</summary>
    public CameraFeed FullscreenFeed(CameraTileViewModel tile) => CameraFeed.For(tile.Camera, CameraStream.Main);

    /// <summary>Suggested file name for a manual snapshot.</summary>
    public string SnapshotFileName(CameraTileViewModel tile)
    {
        var safe = new string(tile.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
        return $"{safe}-{S.Time.GetLocalNow():yyyyMMdd-HHmmss}.jpg";
    }

    public void ReportSnapshot(string? path) => StatusMessage = path is null ? Loc.Get("cam.snapshotFailed") : Loc.Format("cam.snapshotSaved", path);

    [RelayCommand]
    public void Add() => OpenEditor(null);

    [RelayCommand]
    public void Edit(CameraRow? row)
    {
        if ((row ?? SelectedCamera) is { } r) OpenEditor(r.Camera);
    }

    [RelayCommand]
    public Task DeleteAsync(CameraRow? row) => TryActionAsync(async () =>
    {
        if ((row ?? SelectedCamera) is not { } r || S.Cameras is null) return;
        if (!S.Ui.Confirm(Loc.Format("cam.confirmDelete", r.Camera.Name))) return;
        await S.Cameras.Repository.DeleteAsync(r.Camera.Id);
        await ReloadAsync();
    });

    private void OpenEditor(CameraConfig? camera)
    {
        if (S.Cameras is null) return;
        var editor = new CameraEditorViewModel(S, camera);
        EditorRequested?.Invoke(this, editor);
        if (editor.Saved) _ = ReloadAsync();
    }
}

/// <summary>Add/edit camera dialog: brand presets, LAN discovery and a connection test.</summary>
public sealed partial class CameraEditorViewModel : ObservableObject
{
    private readonly DesktopServices _s;
    private readonly CameraConfig? _original;

    public CameraEditorViewModel(DesktopServices s, CameraConfig? camera)
    {
        _s = s;
        _original = camera;
        var c = camera ?? new CameraConfig { Name = "", Brand = CameraBrand.Hikvision };
        _name = c.Name;
        _brand = c.Brand;
        _host = c.Host;
        _port = c.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _httpPort = c.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _username = c.Username;
        _password = c.Password;
        _channel = c.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _useMainStream = c.Stream == CameraStream.Main;
        _isCashier = c.IsCashier;
        _enabled = c.Enabled;
        _customUrl = c.CustomUrl ?? "rtsp://";
    }

    public IReadOnlyList<CameraBrand> Brands { get; } = [CameraBrand.Hikvision, CameraBrand.Dahua, CameraBrand.Generic];
    public ObservableCollection<DiscoveredDevice> Discovered { get; } = [];
    public bool IsNew => _original is null;
    public bool Saved { get; private set; }

    /// <summary>Raised when the dialog should close (after a successful save).</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewUrl))] private string _name;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewUrl), nameof(IsGeneric), nameof(IsPreset))] private CameraBrand _brand;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewUrl))] private string _host;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewUrl))] private string _port;
    [ObservableProperty] private string _httpPort;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewUrl))] private string _username;
    [ObservableProperty] private string _password;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewUrl))] private string _channel;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewUrl))] private bool _useMainStream;
    [ObservableProperty] private bool _isCashier;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewUrl))] private string _customUrl;
    [ObservableProperty] private DiscoveredDevice? _selectedDiscovered;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;

    public bool IsGeneric => Brand == CameraBrand.Generic;
    public bool IsPreset => !IsGeneric;
    public string PreviewUrl => RtspUrlBuilder.Redact(RtspUrlBuilder.Build(Build(), UseMainStream ? CameraStream.Main : CameraStream.Sub));
    public string Text(string key) => _s.Loc.Get(key);

    partial void OnBrandChanged(CameraBrand value)
    {
        // Brand presets: RTSP 554 / HTTP 80 unless the user typed something else.
        if (string.IsNullOrWhiteSpace(Port)) Port = "554";
        if (string.IsNullOrWhiteSpace(HttpPort)) HttpPort = "80";
        if (string.IsNullOrWhiteSpace(Channel)) Channel = "1";
    }

    partial void OnSelectedDiscoveredChanged(DiscoveredDevice? value)
    {
        if (value is null) return;
        Host = value.Address;
        if (value.BrandGuess != CameraBrand.Generic) Brand = value.BrandGuess;
        if (string.IsNullOrWhiteSpace(Name)) Name = value.Name ?? value.Hardware ?? value.Address;
    }

    private static int Int(string text, int fallback) =>
        int.TryParse(text?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public CameraConfig Build() => new()
    {
        Id = _original?.Id ?? Guid.Empty, SortOrder = _original?.SortOrder ?? 0, Name = Name.Trim(), Brand = Brand, Host = Host.Trim(),
        Port = Int(Port, -1), HttpPort = Int(HttpPort, -1), Username = Username.Trim(), Password = Password, Channel = Int(Channel, -1),
        Stream = UseMainStream ? CameraStream.Main : CameraStream.Sub, Role = IsCashier ? CameraRoles.Cashier : CameraRoles.General, Enabled = Enabled,
        CustomUrl = IsGeneric ? CustomUrl.Trim() : null,
    };

    [RelayCommand]
    public async Task DiscoverAsync()
    {
        if (IsBusy || _s.Cameras is null) return;
        IsBusy = true;
        Message = Text("cam.discovering");
        try
        {
            var found = await _s.Cameras.DiscoverAsync();
            Discovered.Clear();
            foreach (var d in found) Discovered.Add(d);
            Message = found.Count == 0 ? Text("cam.discoverNone") : _s.Loc.Format("cam.discoverFound", found.Count);
        }
        catch (Exception e)
        {
            _s.Log.Warn("Camera discovery failed", e);
            Message = Text("cam.discoverNone");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task TestAsync()
    {
        if (IsBusy || _s.Cameras is null) return;
        IsBusy = true;
        Message = Text("cam.testing");
        try
        {
            var cam = Build();
            CameraRepository.Validate(cam);
            var r = await _s.Cameras.TestAsync(cam);
            Message = Text("cam.test." + r.Status.ToString().ToLowerInvariant()) + (r.Detail is null ? "" : $" ({r.Detail})");
        }
        catch (InvalidOperationException e) { Message = Text(e.Message); }
        catch (Exception e)
        {
            _s.Log.Warn("Camera test failed", e);
            Message = Text("cam.test.error");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (IsBusy || _s.Cameras is null) return;
        IsBusy = true;
        try
        {
            await _s.Cameras.Repository.SaveAsync(Build());
            Saved = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException e) { Message = Text(e.Message); }
        catch (Exception e)
        {
            _s.Log.Warn("Camera save failed", e);
            Message = ErrorText.Describe(_s.Loc, e);
        }
        finally { IsBusy = false; }
    }
}
