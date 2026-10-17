using System.Security.Cryptography;

namespace Veltrix.Desktop.Core.Workforce.Terminals;

/// <summary>
/// The official ZKTeco Standalone SDK 6.2.4.11 (32-bit) files, downloaded from ZKTeco's own GitHub repository onto the customer's PC (they are
/// not shipped in VELTRIX) and placed next to the bridge in the admin-only program folder. Every file is pinned by SHA-256: a changed or
/// corrupted download is refused. The bridge uses them through registration-free COM (its manifest), so nothing is registered system-wide.
/// </summary>
public static class ZkTecoSdkFiles
{
    public const string Version = "6.2.4.11";
    public const string SourceUrl = "https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/";

    public static readonly IReadOnlyList<(string Name, string Sha256)> Files =
    [
        ("commpro.dll", "4d574b6268a7f8478b189de83d66a2467fb65cdda60113d75aa8f236a34b10fd"),
        ("comms.dll", "cbb6b257d89d787662b0bb1d94fb63b19d26681095d771229be68a426c53c2ff"),
        ("plcommpro.dll", "042855e5409ad48f399fdb0b0fd50f737fad04f31a21205e2a61087ae8117df2"),
        ("plcomms.dll", "2fea08225edb6169138dea938e7d8aebd88d54ba7a4767d4076abf70c879efc4"),
        ("plrscagent.dll", "ba0dd7b6a7684f698ca120aa7f1dadf5c063c21929b05acc7a052766dac2755c"),
        ("plrscomm.dll", "3a984350c94a68385fe4864a0f872d89b5d8a07aa1ef4dd5f378e6643d4835f6"),
        ("pltcpcomm.dll", "d7af6d2b1c64e0b2d9dc366d5ecd4f92f85d06de51102903f3886087c4f5d2c7"),
        ("rscagent.dll", "f4c4dae04630cdb0117192bfef300a9553ab00964ea804babfdc9bea1c6764de"),
        ("rscomm.dll", "12d4393ad481dd70c7b0b12e4021952c8ad51624059c9d5f421707d865595429"),
        ("tcpcomm.dll", "1a082e01885e3ca67c858bcfbabf5191cc5b85b77e374edc5596478af2d53167"),
        ("usbcomm.dll", "0f523f2333d2e01cbbba31544bf2a2d3a57e1d9ad5642d2f47f1b1d5d53a6d89"),
        ("zkemkeeper.dll", "134d58586597746c788c37997d0ff64056bfac97e6cc73ca29a5a004eccd47b5"),
        ("zkemsdk.dll", "0c94035aa1a50f83069e77d8d9ee393a1e40f4e81824bcbb20ae775d8e587a40"),
    ];

    /// <summary>Folder of the bridge and the SDK files under the program folder (…\VELTRIX OS\ZkBridge).</summary>
    public static string BridgeDirectory(string programDirectory) => Path.Combine(programDirectory, "ZkBridge");

    /// <summary>True when every SDK file is present with its pinned hash.</summary>
    public static bool IsInstalled(string directory) => Files.All(f => HashOf(Path.Combine(directory, f.Name)) == f.Sha256);

    private static string? HashOf(string path)
    {
        try { return File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Downloads every file, verifies its hash, then moves the set into <paramref name="directory"/> (nothing is written on any mismatch).</summary>
    public static async Task DownloadAsync(HttpClient http, string directory, CancellationToken ct = default)
    {
        var staging = Path.Combine(Path.GetTempPath(), "veltrix-zksdk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, sha) in Files)
            {
                var bytes = await http.GetByteArrayAsync(SourceUrl + name, ct);
                var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (actual != sha) throw new InvalidDataException($"{name}: unexpected SHA-256 {actual} (expected {sha}).");
                await File.WriteAllBytesAsync(Path.Combine(staging, name), bytes, ct);
            }
            Directory.CreateDirectory(directory);
            foreach (var (name, _) in Files) File.Copy(Path.Combine(staging, name), Path.Combine(directory, name), overwrite: true);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch (IOException) { /* temp */ }
        }
    }
}
