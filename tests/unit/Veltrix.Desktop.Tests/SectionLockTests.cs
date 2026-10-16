using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Tests;

/// <summary>Separate password of the HR / workforce / cameras section.</summary>
public sealed class SectionLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "veltrix-sec-" + Guid.NewGuid().ToString("N"));
    private readonly WorkforceTests.ManualTime _time = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
    private static readonly LocalUser Owner = new(Guid.NewGuid(), "owner", LocalRoles.Owner, "x", DateTimeOffset.UnixEpoch);
    private static readonly LocalUser HrAdmin = new(Guid.NewGuid(), "hr", LocalRoles.HrAdmin, "x", DateTimeOffset.UnixEpoch);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class PlainProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }

    private SecureStore Store() => new(_dir, new PlainProtector());
    private SectionLockService Lock(SecureStore? store = null) => new(store ?? Store(), _time, PasswordHasher.MinimumIterations);

    [Fact]
    public void Protects_hr_workforce_and_cameras_only()
    {
        Assert.True(SectionLockService.IsProtected("wf.employees"));
        Assert.True(SectionLockService.IsProtected("wf.terminals"));
        Assert.True(SectionLockService.IsProtected("erp.hr"));
        Assert.True(SectionLockService.IsProtected("cameras"));
        Assert.False(SectionLockService.IsProtected("pos"));
        Assert.False(SectionLockService.IsProtected("erp.accounting"));
    }

    [Fact]
    public void Only_owner_sets_it_once_and_it_is_stored_hashed()
    {
        var l = Lock();
        Assert.False(l.IsConfigured);
        Assert.Equal(SectionUnlockStatus.NotConfigured, l.Unlock("anything").Status);
        Assert.Equal("sec.err.ownerOnly", Assert.Throws<InvalidOperationException>(() => l.Setup(HrAdmin, "secret1", "secret1")).Message);
        Assert.Equal("sec.err.tooShort", Assert.Throws<InvalidOperationException>(() => l.Setup(Owner, "abc", "abc")).Message);
        Assert.Equal("sec.err.mismatch", Assert.Throws<InvalidOperationException>(() => l.Setup(Owner, "secret1", "secret2")).Message);
        l.Setup(Owner, "secret1", "secret1");
        Assert.True(l.IsUnlocked);
        Assert.Throws<InvalidOperationException>(() => l.Setup(Owner, "secret9", "secret9"));
        var stored = Store().Get(SectionLockService.HashKey)!;
        Assert.StartsWith("pbkdf2-sha256$", stored);
        Assert.DoesNotContain("secret1", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Unlock_expires_after_idle_and_relocks_on_demand()
    {
        var store = Store();
        Lock(store).Setup(Owner, "secret1", "secret1");
        var l = Lock(store); // new app session: locked again
        Assert.False(l.IsUnlocked);
        Assert.Equal(SectionUnlockStatus.Invalid, l.Unlock("wrong").Status);
        Assert.Equal(SectionUnlockStatus.Unlocked, l.Unlock("secret1").Status);
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(l.IsUnlocked); // used: idle timer restarts
        _time.Advance(SectionLockService.IdleTimeout + TimeSpan.FromSeconds(1));
        Assert.False(l.IsUnlocked);
        l.Unlock("secret1");
        l.Lock();
        Assert.False(l.IsUnlocked);
    }

    [Fact]
    public void Repeated_failures_lock_out_with_backoff_even_across_restarts()
    {
        var store = Store();
        Lock(store).Setup(Owner, "secret1", "secret1");
        var l = Lock(store);
        for (var i = 1; i < SectionLockService.MaxFailures; i++) Assert.Equal(SectionUnlockStatus.Invalid, l.Unlock("bad").Status);
        var r = l.Unlock("bad");
        Assert.Equal((SectionUnlockStatus.LockedOut, SectionLockService.BaseLockout), (r.Status, r.RetryAfter));
        Assert.Equal(SectionUnlockStatus.LockedOut, Lock(store).Unlock("secret1").Status); // the right one is refused while locked
        _time.Advance(SectionLockService.BaseLockout);
        Assert.Equal(SectionUnlockStatus.Unlocked, Lock(store).Unlock("secret1").Status);
    }

    [Fact]
    public void Owner_changes_with_current_or_resets_with_sign_in_password()
    {
        var store = Store();
        var l = Lock(store);
        l.Setup(Owner, "secret1", "secret1");
        Assert.Equal("sec.err.invalid", Assert.Throws<InvalidOperationException>(() => l.Change(Owner, "nope", "secret2", "secret2")).Message);
        Assert.Throws<InvalidOperationException>(() => l.Change(HrAdmin, "secret1", "secret2", "secret2"));
        l.Change(Owner, "secret1", "secret2", "secret2");
        Assert.Equal(SectionUnlockStatus.Invalid, Lock(store).Unlock("secret1").Status);
        Assert.Equal(SectionUnlockStatus.Unlocked, Lock(store).Unlock("secret2").Status);

        var auth = new LocalAuthService(new SecureStore(Path.Combine(_dir, "auth"), new PlainProtector()), _time, PasswordHasher.MinimumIterations);
        var owner = auth.CreateOwner("Boss", "owner-pass");
        Assert.Equal("auth.invalid", Assert.Throws<InvalidOperationException>(() => l.Reset(owner, auth, "wrong", "secret3", "secret3")).Message);
        l.Reset(owner, auth, "owner-pass", "secret3", "secret3");
        Assert.Equal(SectionUnlockStatus.Unlocked, Lock(store).Unlock("secret3").Status);
    }
}
