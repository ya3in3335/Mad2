using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Veltrix.Desktop.Core.Cameras;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Platform;

/// <summary>One shared libVLC instance (win-x64 native files under libvlc\win-x64) tuned for low-latency LAN RTSP.</summary>
public static class VlcRuntime
{
    private static readonly Lazy<LibVLC?> Instance = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    private static LibVLC? Create()
    {
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            return new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--snapshot-format=jpg", "--no-stats", "--no-lua", "--quiet");
        }
        catch (Exception e)
        {
            App.Services?.Log.Warn("libVLC unavailable: camera video disabled", e);
            return null;
        }
    }

    public static bool TryGet(out LibVLC vlc)
    {
        vlc = Instance.Value!;
        return vlc is not null;
    }

    /// <summary>
    /// Media for a camera feed: credentials passed as options (not in the URL, so they never reach logs), TCP-interleaved RTSP
    /// (reliable through Wi-Fi and NAT-less LANs), ~300 ms network caching, no audio.
    /// </summary>
    public static Media CreateMedia(LibVLC vlc, CameraFeed feed)
    {
        var options = new List<string> { ":network-caching=300", ":live-caching=300", ":rtsp-tcp", ":no-audio", ":clock-jitter=0", ":clock-synchro=0", ":rtsp-frame-buffer-size=1000000" };
        if (feed.Username.Length > 0)
        {
            var http = feed.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            options.Add((http ? ":http-user=" : ":rtsp-user=") + feed.Username);
            options.Add((http ? ":http-pwd=" : ":rtsp-pwd=") + feed.Password);
        }
        return new Media(vlc, feed.Url, FromType.FromLocation, [.. options]);
    }
}

/// <summary>Grabs one decoded frame from the camera's main stream with libVLC (no window) and writes it as JPEG.</summary>
public sealed class VlcFrameCapturer : ISnapshotCapturer
{
    public async Task<bool> CaptureAsync(CameraConfig camera, string destinationPath, CancellationToken ct)
    {
        if (!VlcRuntime.TryGet(out var vlc)) return false;
        var feed = CameraFeed.For(camera, CameraStream.Main);
        if (!RtspUrlBuilder.TryGetEndpoint(feed.Url, out var host, out _) || await LanAddress.ResolveLocalAsync(host, ct) is null) return false;
        return await Task.Run(() => Grab(vlc, feed, destinationPath, ct), CancellationToken.None);
    }

    private static async Task<bool> Grab(LibVLC vlc, CameraFeed feed, string path, CancellationToken ct)
    {
        uint width = 0, height = 0, pitch = 0;
        var buffer = IntPtr.Zero;
        var frames = 0;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer.LibVLCVideoFormatCb format = (ref IntPtr opaque, IntPtr chroma, ref uint w, ref uint h, ref uint pitches, ref uint lines) =>
        {
            Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
            width = w;
            height = h;
            pitch = w * 4;
            pitches = pitch;
            lines = h;
            buffer = Marshal.AllocHGlobal((int)(pitch * h));
            return 1;
        };
        MediaPlayer.LibVLCVideoCleanupCb cleanup = (ref IntPtr opaque) => { };
        MediaPlayer.LibVLCVideoLockCb lockCb = (opaque, planes) =>
        {
            Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        };
        MediaPlayer.LibVLCVideoDisplayCb display = (opaque, picture) =>
        {
            // Skip the first frames (often grey/partial until the first key frame is decoded).
            if (++frames < 3 || tcs.Task.IsCompleted) return;
            var bytes = new byte[pitch * height];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            tcs.TrySetResult(bytes);
        };
        var player = new MediaPlayer(vlc) { EnableHardwareDecoding = false };
        try
        {
            player.SetVideoFormatCallbacks(format, cleanup);
            player.SetVideoCallbacks(lockCb, null, display);
            using (var media = VlcRuntime.CreateMedia(vlc, feed)) player.Play(media);
            byte[] frame;
            try { frame = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { return false; }
            var bmp = BitmapSource.Create((int)width, (int)height, 96, 96, PixelFormats.Bgr32, null, frame, (int)pitch);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or VLCException)
        {
            return false;
        }
        finally
        {
            player.Stop();
            player.Dispose();
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            GC.KeepAlive(format);
            GC.KeepAlive(cleanup);
            GC.KeepAlive(lockCb);
            GC.KeepAlive(display);
        }
    }
}
