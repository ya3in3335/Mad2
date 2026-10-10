namespace Veltrix.SharedKernel;

/// <summary>Tenant-scoped binary storage (product images, employee documents, attachments).</summary>
public interface IFileStorage
{
    Task<string> SaveAsync(Guid tenantId, string category, string extension, Stream content, CancellationToken ct = default);
    Task<Stream?> OpenAsync(string path, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
}

public static class UploadValidation
{
    public static readonly IReadOnlyDictionary<string, string> ImageTypes = new Dictionary<string, string>
    {
        ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/webp"] = ".webp",
    };

    public static readonly IReadOnlyDictionary<string, string> DocumentTypes = new Dictionary<string, string>
    {
        ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["application/pdf"] = ".pdf",
    };

    /// <summary>Verify the declared content type against file magic bytes (never trust the extension or header alone).</summary>
    public static bool MatchesSignature(string contentType, ReadOnlySpan<byte> head) => contentType switch
    {
        "image/png" => head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/jpeg" => head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
        "image/webp" => head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8),
        "application/pdf" => head.Length >= 5 && head[..5].SequenceEqual("%PDF-"u8),
        _ => false,
    };

    /// <summary>Read an upload into memory with a size cap and validate its type. Returns the buffered stream and extension.</summary>
    public static async Task<(MemoryStream Stream, string Extension)> ReadValidatedAsync(
        Stream input, string? contentType, long maxBytes, IReadOnlyDictionary<string, string> allowed, CancellationToken ct)
    {
        if (contentType is null || !allowed.TryGetValue(contentType, out var ext))
            throw ValidationException.For("file", "Unsupported file type.");
        var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > maxBytes) throw ValidationException.For("file", $"File exceeds {maxBytes / 1024} KB.");
            ms.Write(buffer, 0, read);
        }
        if (!MatchesSignature(contentType, ms.GetBuffer().AsSpan(0, (int)Math.Min(ms.Length, 16))))
            throw ValidationException.For("file", "File content does not match its type.");
        ms.Position = 0;
        return (ms, ext);
    }
}
