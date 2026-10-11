using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Diagnostics;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Settings;

namespace Veltrix.Desktop.Core.Cameras;

/// <summary>CCTV over the shop LAN (no internet): camera list, per-sale snapshots, discovery and connection tests.</summary>
public sealed class CameraService
{
    private readonly AppSettings _settings;

    public CameraService(Func<LocalDb> db, IDataProtector protector, string appDataRoot, ISnapshotCapturer capturer, AppSettings settings, TimeProvider time,
        AppLog? log = null)
    {
        _settings = settings;
        Repository = new CameraRepository(db, new CredentialProtector(protector), time);
        Store = new SnapshotStore(appDataRoot, time);
        Snapshots = new SaleSnapshotService(db, Repository, Store, capturer, time, () => _settings.Cameras, log);
    }

    public CameraRepository Repository { get; }
    public SnapshotStore Store { get; }
    public SaleSnapshotService Snapshots { get; }
    public CameraSettings Settings => _settings.Cameras;

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken ct = default) => OnvifDiscovery.ProbeAsync(OnvifDiscovery.DefaultTimeout, ct);

    public Task<CameraTestResult> TestAsync(CameraConfig camera, CancellationToken ct = default) => CameraConnectionTester.TestAsync(camera, CameraStream.Sub, ct);
}
