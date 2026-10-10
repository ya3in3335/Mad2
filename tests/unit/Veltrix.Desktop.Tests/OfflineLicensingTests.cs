using System.Security.Cryptography;
using Veltrix.Contracts.Licensing;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Security;
using Veltrix.LicenseIssuer;
using Veltrix.SharedKernel;

namespace Veltrix.Desktop.Tests;

public sealed class OfflineLicensingTests : IDisposable
{
    private readonly ECDsa _vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _iid = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private string Kth => DeviceKeys.Thumbprint(_device.ExportSubjectPublicKeyInfo());
    private string Code => OfflineLicensing.EncodeDeviceCode(_iid, Kth);
    private string PrivatePem => _vendor.ExportPkcs8PrivateKeyPem();
    private byte[] VendorSpki => _vendor.ExportSubjectPublicKeyInfo();

    [Fact]
    public void Device_code_round_trips_including_grouped_form()
    {
        Assert.Equal(OfflineLicensing.DeviceCodeLength, Code.Length);
        var grouped = OfflineLicensing.FormatDeviceCode(Code);
        Assert.Equal(7, grouped.Count(c => c == ' '));
        Assert.True(OfflineLicensing.TryDecodeDeviceCode("  " + grouped.Replace(" ", " \n") + " ", out var iid, out var kth));
        Assert.Equal(_iid, iid);
        Assert.Equal(Kth, kth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    public void Malformed_device_codes_are_rejected(string? code) =>
        Assert.False(OfflineLicensing.TryDecodeDeviceCode(code, out _, out _));

    [Fact]
    public void Device_code_matches_known_vector()
    {
        var iid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var kth = string.Concat(Enumerable.Repeat("ab", 32));
        var code = OfflineLicensing.EncodeDeviceCode(iid, kth);
        Assert.Equal("ABEiM0RVZneImaq7zN3u_6urq6urq6urq6urq6urq6urq6urq6urq6urq6urq6ur", code);
    }

    [Fact]
    public void Issued_certificate_verifies_on_the_bound_device_with_all_features()
    {
        var cert = OfflineIssuer.Issue(PrivatePem, OfflineLicensing.FormatDeviceCode(Code), "Shop A", 365, _now);
        var r = LicenseVerifier.Evaluate(cert, VendorSpki, _iid, Kth, _now.AddMinutes(1), 0);
        Assert.Equal(LicenseState.Valid, r.State);
        var p = r.Payload!;
        Assert.Equal("full", p.Plan);
        Assert.Equal(OfflineLicensing.OfflineType, p.Type);
        Assert.Equal(Features.All, p.Ent);
        Assert.Equal(1, p.MaxDev);
        Assert.Equal("", p.Fp);
        Assert.Equal("Shop A", p.TenantName);
        Assert.Equal(_now.AddDays(365).ToUnixTimeSeconds(), p.Exp);
        Assert.Equal(p.Exp, p.Lexp);

        // Usable for the whole term without any server refresh, then expires as a license (not a renewable certificate).
        Assert.True(LicenseVerifier.Evaluate(cert, VendorSpki, _iid, Kth, _now.AddDays(200), 0).CanSell);
        Assert.Equal(LicenseState.LicenseExpired, LicenseVerifier.Evaluate(cert, VendorSpki, _iid, Kth, _now.AddDays(366), 0).State);
    }

    [Fact]
    public void Issued_certificate_is_rejected_on_another_device()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cert = OfflineIssuer.Issue(PrivatePem, Code, "Shop A", 30, _now);
        var otherKth = DeviceKeys.Thumbprint(other.ExportSubjectPublicKeyInfo());
        Assert.Equal(LicenseState.WrongDevice, LicenseVerifier.Evaluate(cert, VendorSpki, _iid, otherKth, _now, 0).State);
        Assert.Equal(LicenseState.WrongDevice, LicenseVerifier.Evaluate(cert, VendorSpki, Guid.NewGuid(), Kth, _now, 0).State);
    }

    [Fact]
    public void Tampered_or_foreign_certificates_are_rejected()
    {
        var cert = OfflineIssuer.Issue(PrivatePem, Code, "Shop A", 30, _now);
        var parts = cert.Split('.');
        var body = Base64Url.Decode(parts[0]);
        var forged = System.Text.Encoding.UTF8.GetString(body).Replace("\"maxDev\":1", "\"maxDev\":9");
        var tampered = Base64Url.Encode(System.Text.Encoding.UTF8.GetBytes(forged)) + "." + parts[1];
        Assert.Equal(LicenseState.InvalidSignature, LicenseVerifier.Evaluate(tampered, VendorSpki, _iid, Kth, _now, 0).State);

        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var foreign = OfflineIssuer.Issue(attacker.ExportPkcs8PrivateKeyPem(), Code, "Shop A", 30, _now);
        Assert.Equal(LicenseState.InvalidSignature, LicenseVerifier.Evaluate(foreign, VendorSpki, _iid, Kth, _now, 0).State);
    }

    [Fact]
    public void Activation_file_text_is_parsed_back_to_the_code()
    {
        var payload = OfflineIssuer.BuildPayload(Code, "Shop A", 30, _now);
        var cert = OfflineIssuer.Sign(payload, PrivatePem);
        var file = OfflineIssuer.ActivationFileText(payload, cert);
        Assert.Equal(cert, OfflineLicensing.ExtractActivationCode(file));
        var wrapped = string.Join("\r\n", cert.Chunk(40).Select(c => new string(c)));
        Assert.Equal(cert, OfflineLicensing.ExtractActivationCode(wrapped));
        Assert.Null(OfflineLicensing.ExtractActivationCode("# only a comment"));
    }

    [Fact]
    public void Keygen_writes_a_usable_pair_and_never_overwrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "veltrix-keygen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (priv, pub) = OfflineIssuer.GenerateKeys(dir);
            using var check = ECDsa.Create();
            check.ImportFromPem(File.ReadAllText(pub));
            var cert = OfflineIssuer.Issue(File.ReadAllText(priv), Code, "Shop B", 10, _now);
            Assert.NotNull(LicenseVerifier.ReadVerified(cert, check.ExportSubjectPublicKeyInfo()));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(priv));
            Assert.Throws<IOException>(() => OfflineIssuer.GenerateKeys(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public void Dispose()
    {
        _vendor.Dispose();
        _device.Dispose();
    }
}
