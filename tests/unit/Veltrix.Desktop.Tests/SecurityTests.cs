using Veltrix.Desktop.Core.Diagnostics;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Tests;

public sealed class SecurityTests
{
    private static readonly FingerprintInputs Inputs = new("POS-01", 8, "Microsoft Windows NT 10.0.22631.0", "4c4c4544-0042-3510-8048-b4c04f4e3332");

    [Fact]
    public void Fingerprint_is_stable_and_normalised()
    {
        var a = DeviceFingerprint.Compute(Inputs);
        Assert.Equal(a, DeviceFingerprint.Compute(Inputs with { MachineName = " pos-01 ", MachineGuid = Inputs.MachineGuid!.ToUpperInvariant() }));
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.Equal(1, DeviceFingerprint.Version);
    }

    [Fact]
    public void Fingerprint_changes_with_characteristics()
    {
        var a = DeviceFingerprint.Compute(Inputs);
        Assert.NotEqual(a, DeviceFingerprint.Compute(Inputs with { ProcessorCount = 4 }));
        Assert.NotEqual(a, DeviceFingerprint.Compute(Inputs with { MachineGuid = null }));
        Assert.NotEqual(a, DeviceFingerprint.Compute(Inputs with { MachineName = "POS-02" }));
    }

    [Fact]
    public void Offline_verifier_accepts_only_the_right_password()
    {
        var stored = OfflineCredential.Create("Owner@Example.com", "S3cure!pass", iterations: 20_000);
        Assert.True(OfflineCredential.Verify("owner@example.com", "S3cure!pass", stored));
        Assert.False(OfflineCredential.Verify("owner@example.com", "wrong", stored));
        Assert.False(OfflineCredential.Verify("other@example.com", "S3cure!pass", stored));
    }

    [Fact]
    public void Offline_verifier_uses_random_salt_and_rejects_malformed_records()
    {
        Assert.NotEqual(OfflineCredential.Create("a@b.c", "x", 20_000), OfflineCredential.Create("a@b.c", "x", 20_000));
        Assert.False(OfflineCredential.Verify("a@b.c", "x", "nonsense"));
        Assert.False(OfflineCredential.Verify("a@b.c", "x", "pbkdf2-sha256$5$AAAA$AAAA"));
        Assert.False(OfflineCredential.Verify("a@b.c", "x", "pbkdf2-sha256$20000$%%%$AAAA"));
    }

    [Fact]
    public void Redactor_removes_secrets_and_masks_emails()
    {
        var text = "Authorization: Bearer abc.def-ghi\n{\"refreshToken\":\"r123\",\"password\":\"hunter2\"} key VLX-ABCDE-FGHJK-LMNPQ-RSTUV user owner@example.com";
        var r = Redactor.Redact(text);
        Assert.DoesNotContain("abc.def-ghi", r);
        Assert.DoesNotContain("r123", r);
        Assert.DoesNotContain("hunter2", r);
        Assert.DoesNotContain("ABCDE-FGHJK", r);
        Assert.DoesNotContain("owner@example.com", r);
        Assert.Contains("o****@example.com", r);
    }
}
