using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Workers;

namespace Veltrix.Api.IntegrationTests;

[Collection("api")]
public sealed class BackupTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Encrypted_backup_is_created_and_restore_verified_in_disposable_database()
    {
        await Scenario.CreateTenantAsync(f);
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<BackupOptions>();
        options.EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        options.Directory = Path.Combine(Path.GetTempPath(), "veltrix-backups-" + Guid.NewGuid().ToString("N"));
        options.VerifyRestore = true;
        var record = await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(CancellationToken.None);
        Assert.Equal("completed", record.Status);
        Assert.True(record.Verified, record.VerificationDetails);
        Assert.Contains("companies restored=", record.VerificationDetails);

        // Tampering with the encrypted file is detected.
        var path = Path.Combine(options.Directory, record.FileName);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^5] ^= 0xFF;
        await using var ms = new MemoryStream(bytes);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => BackupService.DecryptAsync(ms, Stream.Null, BackupService.Key(options), CancellationToken.None));
        Directory.Delete(options.Directory, true);
    }
}
