using System.Security.Cryptography;
using System.Text;

namespace Veltrix.Desktop.Core.Security;

public static class Base64Url
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>Device-key helpers matching the server activation protocol (ECDSA P-256, SHA-256, DER signatures).</summary>
public static class DeviceKeys
{
    public static string ChallengeMessage(string nonce, Guid installationId, string purpose) => $"{nonce}|{installationId}|{purpose}";

    /// <summary>Lower-case hex SHA-256 of the SubjectPublicKeyInfo bytes; equals the certificate's <c>kth</c> claim.</summary>
    public static string Thumbprint(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();

    public static string SignChallenge(ECDsa key, string nonce, Guid installationId, string purpose) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(ChallengeMessage(nonce, installationId, purpose)),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
}
