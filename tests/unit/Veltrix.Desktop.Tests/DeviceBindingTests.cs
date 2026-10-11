using System.Security.Cryptography;
using System.Text;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Tests;

/// <summary>Install-time device binding (no activation codes): only the elevated --bind path binds; the app only verifies.</summary>
public sealed class DeviceBindingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private readonly FakeMachine _pcA = new();
    private readonly FakeMachine _pcB = new();

    private static string Fp(string machineGuid) => DeviceFingerprint.ComputeBinding(new FingerprintInputs("SHOP-PC", 8, "10.0.26100.0", machineGuid));
    private static DeviceBindingManager Manager(MemoryBindingStore store, FakeMachine machine, string fp, bool elevated = false) =>
        new(store, machine, fp, new FixedTime(Now), () => elevated);
    /// <summary>What the elevated installer does: VeltrixOS.exe --bind.</summary>
    private static BindingResult InstallerBind(MemoryBindingStore store, FakeMachine machine, string fp) => Manager(store, machine, fp, elevated: true).Bind();

    [Fact]
    public void First_run_without_installer_binding_is_refused_and_writes_nothing()
    {
        var store = new MemoryBindingStore();
        var m = Manager(store, _pcA, Fp("guid-a"));
        Assert.Equal(BindingStatus.NotInstalled, m.Verify().Status);
        Assert.False(m.IsBound);
        Assert.All(store.Locations, l => Assert.Null(store.Read(l)));
    }

    [Fact]
    public void Installer_bind_writes_both_locations_then_app_verifies()
    {
        var store = new MemoryBindingStore();
        var r = InstallerBind(store, _pcA, Fp("guid-a"));
        Assert.Equal(BindingStatus.NewlyBound, r.Status);
        Assert.All(store.Locations, l => Assert.NotNull(store.Read(l)));
        Assert.Equal(Now, r.Record!.BoundAt);
        var app = Manager(store, _pcA, Fp("guid-a"));
        var v = app.Verify();
        Assert.Equal(BindingStatus.Bound, v.Status);
        Assert.Equal(r.Record, v.Record);
        Assert.True(app.IsBound);
    }

    [Fact]
    public void Bind_without_elevation_is_refused()
    {
        var store = new MemoryBindingStore();
        Assert.Equal(BindingStatus.NotElevated, Manager(store, _pcA, Fp("guid-a"), elevated: false).Bind().Status);
        Assert.All(store.Locations, l => Assert.Null(store.Read(l)));

        // Nor can a normal user replace an existing binding.
        InstallerBind(store, _pcA, Fp("guid-a"));
        var before = store.Read(store.Locations[0]);
        Assert.Equal(BindingStatus.NotElevated, Manager(store, _pcB, Fp("guid-b")).Bind().Status);
        Assert.Equal(before, store.Read(store.Locations[0]));
    }

    [Fact]
    public void Data_copied_to_another_machine_is_refused()
    {
        var store = new MemoryBindingStore();
        InstallerBind(store, _pcA, Fp("guid-a"));
        // Different DPAPI machine key → cannot decrypt.
        Assert.Equal(BindingStatus.WrongDevice, Manager(store.Clone(), _pcB, Fp("guid-b")).Verify().Status);
        // Same protector (e.g. cloned disk) but another MachineGuid → fingerprint mismatch.
        Assert.Equal(BindingStatus.WrongDevice, Manager(store.Clone(), _pcA, Fp("guid-b")).Verify().Status);
    }

    [Fact]
    public void Deleted_binding_is_refused()
    {
        var store = new MemoryBindingStore();
        InstallerBind(store, _pcA, Fp("guid-a"));
        foreach (var l in store.Locations) store.Remove(l);
        Assert.Equal(BindingStatus.NotInstalled, Manager(store, _pcA, Fp("guid-a")).Verify().Status);
    }

    [Fact]
    public void Tampered_binding_is_refused()
    {
        var store = new MemoryBindingStore();
        var mgr = Manager(store, _pcA, Fp("guid-a"), elevated: true);
        var original = mgr.Bind().Record!;

        var garbage = store.Clone();
        garbage.Write(garbage.Locations[0], "{not json"u8.ToArray());
        Assert.Equal(BindingStatus.Tampered, Manager(garbage, _pcA, Fp("guid-a")).Verify().Status);

        // Valid envelope for another record in one location only → locations disagree.
        var mixed = store.Clone();
        mixed.Write(mixed.Locations[1], mgr.Seal(original with { Iid = Guid.NewGuid() }));
        Assert.Equal(BindingStatus.Tampered, Manager(mixed, _pcA, Fp("guid-a")).Verify().Status);

        // Bit flip in the stored MAC.
        var flipped = store.Clone();
        var text = Encoding.UTF8.GetString(flipped.Read(flipped.Locations[0])!);
        var i = text.IndexOf("\"m\":\"", StringComparison.Ordinal) + 5;
        text = text[..i] + (text[i] == 'A' ? 'B' : 'A') + text[(i + 1)..];
        flipped.Write(flipped.Locations[0], Encoding.UTF8.GetBytes(text));
        flipped.Remove(flipped.Locations[1]);
        Assert.Equal(BindingStatus.Tampered, Manager(flipped, _pcA, Fp("guid-a")).Verify().Status);
    }

    [Fact]
    public void App_accepts_one_valid_location_but_never_repairs_only_bind_does()
    {
        var store = new MemoryBindingStore();
        InstallerBind(store, _pcA, Fp("guid-a"));
        var good = store.Read(store.Locations[0]);
        store.Remove(store.Locations[1]);

        Assert.Equal(BindingStatus.Bound, Manager(store, _pcA, Fp("guid-a")).Verify().Status);
        Assert.Null(store.Read(store.Locations[1]));

        var r = InstallerBind(store, _pcA, Fp("guid-a"));
        Assert.Equal(BindingStatus.Bound, r.Status); // existing binding kept
        Assert.Equal(good, store.Read(store.Locations[1]));
    }

    [Fact]
    public void Elevated_reinstall_keeps_a_valid_binding_and_replaces_an_unusable_one()
    {
        var store = new MemoryBindingStore();
        var first = InstallerBind(store, _pcA, Fp("guid-a")).Record;
        var again = InstallerBind(store, _pcA, Fp("guid-a"));
        Assert.Equal(BindingStatus.Bound, again.Status);
        Assert.Equal(first, again.Record);

        // Hardware change (new MachineGuid): app refuses until the vendor re-installs.
        Assert.Equal(BindingStatus.WrongDevice, Manager(store, _pcA, Fp("guid-new")).Verify().Status);
        Assert.Equal(BindingStatus.NewlyBound, InstallerBind(store, _pcA, Fp("guid-new")).Status);
        Assert.Equal(BindingStatus.Bound, Manager(store, _pcA, Fp("guid-new")).Verify().Status);
    }

    [Fact]
    public void Bind_fails_unless_every_location_is_written()
    {
        var store = new MemoryBindingStore { ReadOnly = true };
        Assert.Equal(BindingStatus.StorageFailed, InstallerBind(store, _pcA, Fp("guid-a")).Status);
    }

    [Fact]
    public void Binding_fingerprint_ignores_os_version_and_cpu_count()
    {
        var a = DeviceFingerprint.ComputeBinding(new FingerprintInputs("PC", 4, "10.0.19045.0", "G"));
        var b = DeviceFingerprint.ComputeBinding(new FingerprintInputs("PC", 8, "10.0.26100.0", "g"));
        Assert.Equal(a, b);
        Assert.NotEqual(a, DeviceFingerprint.ComputeBinding(new FingerprintInputs("PC", 4, "10.0.19045.0", "H")));
    }

    /// <summary>Stands in for DPAPI LocalMachine: AES-GCM under a per-machine key, so another machine cannot decrypt.</summary>
    private sealed class FakeMachine : IDataProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

        public byte[] Protect(byte[] plaintext)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ct = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, plaintext, ct, tag);
            return [.. nonce, .. tag, .. ct];
        }

        public byte[] Unprotect(byte[] data)
        {
            if (data.Length < 28) throw new CryptographicException("short");
            var pt = new byte[data.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), pt);
            return pt;
        }
    }

    private sealed class MemoryBindingStore : IDeviceBindingStore
    {
        private readonly Dictionary<string, byte[]> _data = [];
        public bool ReadOnly { get; init; }
        public IReadOnlyList<string> Locations { get; } = ["file", "registry"];
        public byte[]? Read(string location) => _data.TryGetValue(location, out var d) ? d : null;
        public bool Write(string location, byte[] data)
        {
            if (ReadOnly) return false;
            _data[location] = data;
            return true;
        }
        public void Remove(string location) => _data.Remove(location);
        public MemoryBindingStore Clone()
        {
            var c = new MemoryBindingStore();
            foreach (var (k, v) in _data) c._data[k] = v;
            return c;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
