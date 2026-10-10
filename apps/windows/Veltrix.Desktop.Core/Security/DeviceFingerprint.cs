using System.Security.Cryptography;
using System.Text;

namespace Veltrix.Desktop.Core.Security;

/// <summary>
/// The limited, non-identifying characteristics used for the hardware fingerprint. Deliberately excludes MAC addresses,
/// disk/board serial numbers and user names: the fingerprint only helps the server recognise a re-install on the same PC.
/// </summary>
public sealed record FingerprintInputs(string MachineName, int ProcessorCount, string OsVersion, string? MachineGuid);

public static class DeviceFingerprint
{
    public const int Version = 1;
    private const string Salt = "veltrix.desktop.fingerprint.v1";

    /// <summary>Lower-case hex SHA-256 over the salted, normalised inputs. Stable for identical inputs.</summary>
    public static string Compute(FingerprintInputs inputs)
    {
        var material = string.Join('|', Salt,
            inputs.MachineName.Trim().ToUpperInvariant(),
            inputs.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inputs.OsVersion.Trim(),
            (inputs.MachineGuid ?? "").Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
