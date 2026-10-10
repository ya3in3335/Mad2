using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Veltrix.Desktop.Core.Security;

/// <summary>
/// PBKDF2-SHA256 verifier that lets the last signed-in user unlock the app while the server is unreachable.
/// Format: <c>pbkdf2-sha256$iterations$salt(b64)$hash(b64)</c>. The email is bound into the derived key.
/// </summary>
public static class OfflineCredential
{
    public const int DefaultIterations = 210_000;
    private const string Prefix = "pbkdf2-sha256";

    public static string Create(string email, string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(email, password, salt, iterations);
        return string.Join('$', Prefix, iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string email, string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var it) || it < 10_000)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            return CryptographicOperations.FixedTimeEquals(Derive(email, password, salt, it), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string email, string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant() + "\n" + password), salt, iterations, HashAlgorithmName.SHA256, 32);
}
