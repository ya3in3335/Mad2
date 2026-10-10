using System.Security.Cryptography;
using System.Text.Json;
using Veltrix.Contracts.Licensing;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Tests;

public sealed class LicenseVerifierTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ECDsa _vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _iid = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private byte[] VendorSpki => _vendor.ExportSubjectPublicKeyInfo();
    private string Kth => DeviceKeys.Thumbprint(_device.ExportSubjectPublicKeyInfo());

    private LicenseCertificatePayload Payload(long? lexp = null, long? exp = null, string status = "Active", long? iat = null) =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), "Acme", Guid.NewGuid(), _iid, Kth, "fp", "professional", "Subscription", ["pos"], 5,
            iat ?? _now.AddHours(-1).ToUnixTimeSeconds(), exp ?? _now.AddDays(14).ToUnixTimeSeconds(), lexp ?? _now.AddDays(200).ToUnixTimeSeconds(), status);

    private string Sign(LicenseCertificatePayload p, ECDsa? key = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(p, Json);
        var sig = (key ?? _vendor).SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Base64Url.Encode(body) + "." + Base64Url.Encode(sig);
    }

    private LicenseEvaluation Eval(string? cert, DateTimeOffset? now = null, long watermark = 0, Guid? iid = null, string? kth = null) =>
        LicenseVerifier.Evaluate(cert, VendorSpki, iid ?? _iid, kth ?? Kth, now ?? _now, watermark);

    [Fact]
    public void Valid_certificate_is_accepted_and_advances_watermark()
    {
        var r = Eval(Sign(Payload()));
        Assert.Equal(LicenseState.Valid, r.State);
        Assert.True(r.CanSell);
        Assert.Equal(_now.ToUnixTimeSeconds(), r.Watermark);
        Assert.Equal(200, r.DaysRemaining);
    }

    [Fact]
    public void Missing_certificate_requires_activation() => Assert.True(Eval(null).RequiresActivation);

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var cert = Sign(Payload());
        var forged = Payload() with { MaxDev = 500 };
        var tampered = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(forged, Json)) + "." + cert.Split('.')[1];
        Assert.Equal(LicenseState.InvalidSignature, Eval(tampered).State);
    }

    [Fact]
    public void Certificate_signed_by_another_key_is_rejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(LicenseState.InvalidSignature, Eval(Sign(Payload(), other)).State);
        Assert.Equal(LicenseState.InvalidSignature, Eval("garbage").State);
        Assert.Equal(LicenseState.InvalidSignature, Eval("a.b").State);
    }

    [Fact]
    public void Certificate_for_another_device_is_rejected()
    {
        var cert = Sign(Payload());
        Assert.Equal(LicenseState.WrongDevice, Eval(cert, iid: Guid.NewGuid()).State);
        Assert.Equal(LicenseState.WrongDevice, Eval(cert, kth: new string('0', 64)).State);
    }

    [Fact]
    public void Expired_license_becomes_read_only()
    {
        var r = Eval(Sign(Payload(lexp: _now.AddMinutes(-1).ToUnixTimeSeconds(), iat: _now.AddDays(-2).ToUnixTimeSeconds())));
        Assert.Equal(LicenseState.LicenseExpired, r.State);
        Assert.True(r.ReadOnly);
        Assert.False(r.CanSell);
    }

    [Fact]
    public void Offline_grace_exhausted_requires_refresh()
    {
        var r = Eval(Sign(Payload(exp: _now.AddSeconds(-1).ToUnixTimeSeconds(), iat: _now.AddDays(-14).ToUnixTimeSeconds())));
        Assert.Equal(LicenseState.CertificateExpired, r.State);
    }

    [Fact]
    public void Renewal_warning_within_fourteen_days()
    {
        var r = Eval(Sign(Payload(lexp: _now.AddDays(10).ToUnixTimeSeconds(), exp: _now.AddDays(10).ToUnixTimeSeconds())));
        Assert.Equal(LicenseState.RenewSoon, r.State);
        Assert.Equal(10, r.DaysRemaining);
        Assert.True(r.CanSell);
    }

    [Fact]
    public void Clock_rollback_beyond_tolerance_is_detected()
    {
        var cert = Sign(Payload());
        var watermark = _now.ToUnixTimeSeconds();
        Assert.Equal(LicenseState.ClockRollback, Eval(cert, _now.AddSeconds(-(LicenseVerifier.ClockToleranceSeconds + 1)), watermark).State);
        // Small drift within tolerance is fine and never moves the watermark backwards.
        var ok = Eval(cert, _now.AddSeconds(-(LicenseVerifier.ClockToleranceSeconds - 5)), watermark);
        Assert.Equal(LicenseState.Valid, ok.State);
        Assert.Equal(watermark, ok.Watermark);
    }

    [Fact]
    public void Clock_before_issue_time_is_detected()
    {
        var cert = Sign(Payload(iat: _now.AddDays(1).ToUnixTimeSeconds()));
        Assert.Equal(LicenseState.ClockRollback, Eval(cert).State);
    }

    [Fact]
    public void Revoked_and_suspended_statuses_are_reported()
    {
        Assert.Equal(LicenseState.Revoked, Eval(Sign(Payload(status: "Revoked"))).State);
        Assert.Equal(LicenseState.Suspended, Eval(Sign(Payload(status: "Suspended"))).State);
    }

    [Fact]
    public void Challenge_signature_verifies_with_device_public_key()
    {
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var sig = DeviceKeys.SignChallenge(_device, nonce, _iid, "activate");
        using var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(_device.ExportSubjectPublicKeyInfo(), out _);
        Assert.True(pub.VerifyData(System.Text.Encoding.UTF8.GetBytes($"{nonce}|{_iid}|activate"), Convert.FromBase64String(sig),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    public void Dispose()
    {
        _vendor.Dispose();
        _device.Dispose();
    }
}
