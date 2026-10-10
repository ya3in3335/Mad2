using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Core.Standalone;

/// <summary>
/// Standalone mode (offline license, single device, no server): local accounts, the local repository and encrypted backups.
/// </summary>
public sealed class StandaloneService(SecureStore store, Func<LocalDb> db, TimeProvider time, Func<bool> hasValidLicense,
    int iterations = PasswordHasher.DefaultIterations)
{
    public LocalAuthService Auth { get; } = new(store, time, iterations);
    public StandaloneStore Store { get; } = new(db, time);

    /// <summary>Exports an encrypted backup. Only an owner may export, and must re-enter their password (it becomes the backup key).</summary>
    public async Task<byte[]> ExportBackupAsync(LocalUser user, string password, string? tenantId, string? tenantName, CancellationToken ct = default)
    {
        if (!user.IsOwner) throw new InvalidOperationException("dk.sa.err.ownerOnly");
        if (!Auth.VerifySecret(user.Id, password)) throw new InvalidOperationException("auth.invalid");
        await using var ctx = db();
        var payload = await LocalBackup.CaptureAsync(ctx, Auth.ExportUsers(), tenantId, tenantName, time.GetUtcNow(), ct);
        return LocalBackup.Encrypt(payload, password, iterations);
    }

    /// <summary>Restores a backup (data and local accounts). Requires a valid license on this device.</summary>
    public async Task<BackupPayload> RestoreBackupAsync(byte[] file, string password, CancellationToken ct = default)
    {
        if (!hasValidLicense()) throw new InvalidOperationException("dk.sa.err.licenseRequired");
        BackupPayload payload;
        try { payload = LocalBackup.Decrypt(file, password); }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException) { throw new InvalidOperationException("dk.sa.err.backupPassword"); }
        catch (InvalidDataException) { throw new InvalidOperationException("dk.sa.err.backupFile"); }
        await using (var ctx = db()) await LocalBackup.RestoreAsync(ctx, payload, ct);
        Auth.ImportUsers(payload.UsersJson);
        return payload;
    }
}
