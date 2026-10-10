using System.Security.Cryptography;
using System.Text;

namespace Veltrix.Modules.Identity;

/// <summary>PBKDF2-HMAC-SHA512 password hashing with per-password salt and versioned format
/// (v1$iterations$salt$hash). Iteration count follows current OWASP guidance (210,000 for SHA-512).</summary>
public sealed class PasswordHasher(int iterations = PasswordHasher.DefaultIterations)
{
    public const int DefaultIterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, KeySize);
        return $"v1${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string stored, out bool needsRehash)
    {
        needsRehash = false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iter)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA512, expected.Length);
        var ok = CryptographicOperations.FixedTimeEquals(actual, expected);
        needsRehash = ok && iter < iterations;
        return ok;
    }

    public static PasswordPolicyResult CheckPolicy(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 10)
            return new(false, "Password must be at least 10 characters.");
        if (password.Length > 128) return new(false, "Password must be at most 128 characters.");
        var classes = (password.Any(char.IsUpper) ? 1 : 0) + (password.Any(char.IsLower) ? 1 : 0) +
                      (password.Any(char.IsDigit) ? 1 : 0) + (password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        return classes < 3
            ? new(false, "Password must contain at least three of: uppercase, lowercase, digits, symbols.")
            : new(true, null);
    }
}

public sealed record PasswordPolicyResult(bool Ok, string? Error);

public static class TokenHashing
{
    public static string NewOpaqueToken(int bytes = 32) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>RFC 6238 TOTP (SHA-1, 6 digits, 30 s) compatible with common authenticator apps.</summary>
public static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] DecodeBase32(string s)
    {
        s = s.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = Base32Alphabet.IndexOf(c);
            if (v < 0) continue;
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { output.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return output.ToArray();
    }

    public static string Compute(string secret, long counter)
    {
        var key = DecodeBase32(secret);
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (code % 1_000_000).ToString("D6");
    }

    public static bool Verify(string secret, string? code, DateTimeOffset now, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6) return false;
        var counter = now.ToUnixTimeSeconds() / 30;
        for (var i = -window; i <= window; i++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(secret, counter + i)), Encoding.ASCII.GetBytes(code)))
                return true;
        return false;
    }
}
