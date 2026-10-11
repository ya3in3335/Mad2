using System.Security.Cryptography;
using System.Text;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Tests;

/// <summary>Trust-on-first-use device binding (no activation codes): first run binds, copies and tampering are refused.</summary>
public sealed class DeviceBindingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private readonly FakeMachine _pcA = new();
    private readonly FakeMachine _pcB = new();

    private static string Fp(string machineGuid) => DeviceFingerprint.ComputeBinding(new FingerprintInputs("SHOP-PC", 8, "10.0.26100.0", machineGuid));
    private static DeviceBindingManager Manager(MemoryBindingStore store, FakeMachine machine, string fp) => new(store, machine, fp, new FixedTime(Now));

    [Fact]
    public void First_run_binds_both_locations_and_consumes_the_install_token()
    {
        var store = new MemoryBindingStore { Token = "fresh-install" };
        var r = Manager(store, _pcA, Fp("guid-a")).Verify();
        Assert.Equal(BindingStatus.NewlyBound, r.Status);
        Assert.True(r.IsLicensed);
        Assert.Null(store.Token);
        Assert.All(store.Locations, l => Assert.NotNull(store.Read(l)));
        Assert.Equal(Now, r.Record!.BoundAt);
        Assert.NotEqual(Guid.Empty, r.Record.Iid);
    }

    [Fact]
    public void Second_run_is_bound_with_the_same_record()
    {
        var store = new MemoryBindingStore { Token = "t" };
        var first = Manager(store, _pcA, Fp("guid-a")).Verify();
        var second = Manager(store, _pcA, Fp("guid-a")).Verify();
        Assert.Equal(BindingStatus.Bound, second.Status);
        Assert.Equal(first.Record, second.Record);
    }

    [Fact]
    public void Data_copied_to_another_machine_is_refused()
    {
        var store = new MemoryBindingStore { Token = "t" };
        Manager(store, _pcA, Fp("guid-a")).Verify();
        var copy = store.Clone();
        // Different DPAPI machine key → cannot decrypt.
        Assert.Equal(BindingStatus.WrongDevice, Manager(copy, _pcB, Fp("guid-b")).Verify().Status);
        // Same protector (e.g. cloned disk) but another MachineGuid → fingerprint mismatch.
        var m = Manager(store.Clone(), _pcA, Fp("guid-b"));
        Assert.Equal(BindingStatus.WrongDevice, m.Verify().Status);
        Assert.False(m.IsBound);
    }

    [Fact]
    public void No_binding_and_no_token_is_refused()
    {
        var store = new MemoryBindingStore { Token = "t" };
        Manager(store, _pcA, Fp("guid-a")).Verify();
        foreach (var l in store.Locations) store.Remove(l);
        Assert.Equal(BindingStatus.NotInstalled, Manager(store, _pcA, Fp("guid-a")).Verify().Status);
        Assert.Equal(BindingStatus.NotInstalled, Manager(new MemoryBindingStore(), _pcA, Fp("guid-a")).Verify().Status);
    }

    [Fact]
    public void Tampered_binding_is_refused()
    {
        var store = new MemoryBindingStore { Token = "t" };
        var mgr = Manager(store, _pcA, Fp("guid-a"));
        var original = mgr.Verify().Record!;

        // Garbage bytes.
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
    public void Missing_location_is_repaired_from_the_valid_one()
    {
        var store = new MemoryBindingStore { Token = "t" };
        Manager(store, _pcA, Fp("guid-a")).Verify();
        var good = store.Read(store.Locations[0]);
        store.Remove(store.Locations[1]);
        var r = Manager(store, _pcA, Fp("guid-a")).Verify();
        Assert.Equal(BindingStatus.Bound, r.Status);
        Assert.Equal(good, store.Read(store.Locations[1]));
    }

    [Fact]
    public void Vendor_reinstall_token_rebinds_an_unusable_binding_and_keeps_a_valid_one()
    {
        var store = new MemoryBindingStore { Token = "t1" };
        var first = Manager(store, _pcA, Fp("guid-a")).Verify().Record;

        // Re-install on the same PC: binding kept, token consumed.
        store.Token = "t2";
        var again = Manager(store, _pcA, Fp("guid-a")).Verify();
        Assert.Equal(BindingStatus.Bound, again.Status);
        Assert.Equal(first, again.Record);
        Assert.Null(store.Token);

        // Hardware change (new MachineGuid) + vendor re-install: re-bound.
        store.Token = "t3";
        Assert.Equal(BindingStatus.NewlyBound, Manager(store, _pcA, Fp("guid-new")).Verify().Status);
        Assert.Equal(BindingStatus.Bound, Manager(store, _pcA, Fp("guid-new")).Verify().Status);
    }

    [Fact]
    public void Nothing_writable_fails_without_consuming_the_token()
    {
        var store = new MemoryBindingStore { Token = "t", ReadOnly = true };
        Assert.Equal(BindingStatus.StorageFailed, Manager(store, _pcA, Fp("guid-a")).Verify().Status);
        Assert.Equal("t", store.Token);
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
        public string? Token { get; set; }
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
        public string? ReadInstallToken() => Token;
        public bool DeleteInstallToken() { Token = null; return true; }
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
