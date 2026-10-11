using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Catalog;
using Veltrix.Contracts.Identity;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Pos;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Tests;

/// <summary>Standalone mode (offline license): local accounts, local repository routing, sale → stock → report, encrypted backups.</summary>
public sealed class StandaloneTests : IAsyncLifetime
{
    private const int FastIterations = PasswordHasher.MinimumIterations; // production uses 210 000; tests stay fast
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "veltrix-sa-" + Guid.NewGuid().ToString("N"));

    private LocalDb Db() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_conn).Options);
    private SecureStore Store(string name = "a") => new(Path.Combine(_dir, name), new PlainProtector());

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        await using var db = Db();
        await db.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _conn.DisposeAsync();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    // ------------------------------------------------------------------ local auth

    [Fact]
    public void Password_hash_is_salted_pbkdf2_with_at_least_210k_iterations()
    {
        var a = PasswordHasher.Hash("correct horse");
        var b = PasswordHasher.Hash("correct horse");
        Assert.StartsWith("pbkdf2-sha256$210000$", a);
        Assert.NotEqual(a, b); // random salt
        Assert.True(PasswordHasher.Verify("correct horse", a));
        Assert.False(PasswordHasher.Verify("correct horsE", a));
        Assert.False(PasswordHasher.Verify("x", "pbkdf2-sha256$1000$AAAA$AAAA")); // too few iterations is rejected
        Assert.DoesNotContain("correct horse", a);
    }

    [Theory]
    [InlineData("abc1234", "dk.sa.err.passwordShort")]
    [InlineData("1234567", null)]
    [InlineData("12345", "dk.sa.err.pinShort")]
    [InlineData("", "dk.sa.err.secretRequired")]
    [InlineData("123456", null)]
    [InlineData("s3cret-pass", null)]
    public void Secret_policy_requires_password_8_or_pin_6(string secret, string? error) => Assert.Equal(error, LocalAuthService.ValidateSecret(secret));

    [Fact]
    public void No_default_account_exists_until_the_owner_is_created()
    {
        var auth = new LocalAuthService(Store(), _time, FastIterations);
        Assert.False(auth.HasOwner);
        Assert.Equal(LocalSignInStatus.NoUsers, auth.SignIn("admin", "admin").Status);
        Assert.Throws<InvalidOperationException>(() => auth.AddUser("cashier", "12345678", LocalRoles.Cashier));

        var owner = auth.CreateOwner("Amina", "owner-pass-1");
        Assert.True(auth.HasOwner);
        Assert.Throws<InvalidOperationException>(() => auth.CreateOwner("Other", "owner-pass-2"));
        var r = auth.SignIn(" amina ", "owner-pass-1");
        Assert.Equal(LocalSignInStatus.SignedIn, r.Status);
        Assert.Equal(owner.Id, r.User!.Id);
        Assert.Contains("*", r.User.ToProfile().Permissions);

        var cashier = auth.AddUser("Karim", "246810", LocalRoles.Cashier);
        var profile = auth.SignIn("karim", "246810").User!.ToProfile();
        Assert.Contains("pos.sell", profile.Permissions);
        Assert.DoesNotContain("*", profile.Permissions);
        Assert.Throws<InvalidOperationException>(() => auth.AddUser("KARIM", "13579135", LocalRoles.Cashier)); // names are unique
        auth.RemoveUser(cashier.Id);
        Assert.Throws<InvalidOperationException>(() => auth.RemoveUser(owner.Id)); // last owner stays
    }

    [Fact]
    public void Five_failures_lock_the_account_with_exponential_backoff_that_survives_restart()
    {
        var store = Store();
        var auth = new LocalAuthService(store, _time, FastIterations);
        auth.CreateOwner("Amina", "owner-pass-1");
        for (var i = 0; i < 4; i++) Assert.Equal(LocalSignInStatus.Invalid, auth.SignIn("Amina", "wrong-pass").Status);
        var fifth = auth.SignIn("Amina", "wrong-pass");
        Assert.Equal(LocalSignInStatus.LockedOut, fifth.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), fifth.RetryAfter);

        // Even the right password is refused while locked — also after an app restart.
        var restarted = new LocalAuthService(store, _time, FastIterations);
        Assert.Equal(LocalSignInStatus.LockedOut, restarted.SignIn("Amina", "owner-pass-1").Status);

        _time.Advance(TimeSpan.FromSeconds(31));
        var sixth = restarted.SignIn("Amina", "wrong-pass");
        Assert.Equal(LocalSignInStatus.LockedOut, sixth.Status);
        Assert.Equal(TimeSpan.FromSeconds(60), sixth.RetryAfter); // doubled

        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(LocalSignInStatus.SignedIn, restarted.SignIn("Amina", "owner-pass-1").Status);
        Assert.Equal(LocalSignInStatus.Invalid, restarted.SignIn("Amina", "wrong-pass").Status); // counter reset by success
    }

    // ------------------------------------------------------------------ routing + sale → stock → report

    /// <summary>An API client with no server configured: any call that escapes standalone routing fails the test.</summary>
    private static ApiClient NoServerApi() => new(new HttpClient(new FailingHandler()), new NoTokens(), new NoServer());

    private async Task<(StandaloneStore Store, PosService Pos, ProductDto Coffee, ProductDto Cake)> SeedAsync()
    {
        var store = new StandaloneStore(Db, _time);
        var coffee = await store.UpsertProductAsync(new UpsertProductRequest("COF", "Coffee", null, null, "Standard", "Active", null, null, null,
            StandaloneStore.TaxCategories[2].Id, 119m, null, 60m, true, false, false, false, 5, 0, null, null, ["6130001"], null), null);
        var cake = await store.UpsertProductAsync(new UpsertProductRequest("CAK", "Cake", null, null, "Standard", "Active", null, null, null,
            StandaloneStore.TaxCategories[0].Id, 200m, null, 120m, true, false, false, false, 0, 0, null, null, [], null), null);
        await store.AdjustStockAsync(coffee.Id, 10);
        await store.AdjustStockAsync(cake.Id, 3);
        return (store, new PosService(NoServerApi(), Db, _time, () => true), coffee, cake);
    }

    [Fact]
    public async Task Pos_lookup_finds_generated_in_store_barcode()
    {
        var (store, pos, _, _) = await SeedAsync();
        var code = await new Veltrix.Desktop.Core.Barcodes.InStoreBarcodeGenerator(Db).NextAsync();
        var p = await store.UpsertProductAsync(new UpsertProductRequest("GEN", "Generated", null, null, "Standard", "Active", null, null, null,
            StandaloneStore.TaxCategories[0].Id, 90m, null, 50m, true, false, false, false, 0, 0, null, null, [code], null), null);
        Assert.Equal(p.Id, (await pos.FindByCodeAsync(code, default))!.Id);
        Assert.NotEqual(code, await new Veltrix.Desktop.Core.Barcodes.InStoreBarcodeGenerator(Db).NextAsync());
    }

    [Fact]
    public async Task Standalone_pos_reads_and_writes_only_the_local_database()
    {
        var (_, pos, coffee, _) = await SeedAsync();
        var (registers, offline) = await pos.RegistersAsync(default);
        Assert.False(offline);
        var reg = Assert.Single(registers);
        Assert.Equal(coffee.Id, (await pos.FindByCodeAsync("6130001", default))!.Id);
        var (found, searchOffline) = await pos.SearchAsync("cof", default);
        Assert.Single(found);
        Assert.False(searchOffline);
        var shift = await pos.OpenShiftAsync(reg.Id, 1000, default);
        Assert.False(shift.Offline);

        await using var db = Db();
        Assert.Equal(0, await db.Outbox.CountAsync()); // nothing is ever queued for a server
        Assert.Equal(19m, (await db.Products.FindAsync(coffee.Id))!.TaxRatePercent);
    }

    [Fact]
    public async Task Sale_decrements_stock_and_feeds_dashboard_and_reports()
    {
        var (store, pos, coffee, cake) = await SeedAsync();
        var reg = (await pos.RegistersAsync(default)).Registers[0];
        var shift = await pos.OpenShiftAsync(reg.Id, 500, default);
        var cart = new Cart();
        cart.Add(SellableProduct.From(coffee), 2); // 238.00 incl. 19 % tax
        cart.Add(SellableProduct.From(cake), 1);   // 200.00
        var config = PosConfig.Default;
        var total = cart.Totals(config).Total;
        Assert.Equal(438m, total);

        var result = await pos.CheckoutAsync(cart, config, reg.Id, reg.Code, shift, null, null, [new PaymentLine("Cash", 500m)], "Shop", "Amina", "fr", default);
        Assert.False(result.Offline);
        Assert.Equal(62m, result.Receipt.Change);

        var stock = (await store.StockAsync(1, 50, null, false)).Items.ToDictionary(s => s.ProductId, s => s.Quantity);
        Assert.Equal(8m, stock[coffee.Id]);
        Assert.Equal(2m, stock[cake.Id]);

        var dash = await store.DashboardAsync();
        Assert.Equal(438m, dash.SalesToday);
        Assert.Equal(1, dash.TransactionsToday);
        Assert.Equal(438m, dash.SalesMonth);
        Assert.Equal(238m, dash.TopProducts[0].Value);
        // margin: net = 200 + 200 = 400, cost = 2×60 + 120 = 240 → 40 %
        Assert.Equal(40m, dash.GrossMarginMonth);

        var today = DateOnly.FromDateTime(_time.GetLocalNow().Date);
        var byDay = await store.RunReportAsync("sales-by-day", today, today);
        Assert.Equal(438m, byDay.Summary["Total"]);
        var byProduct = await store.RunReportAsync("sales-by-product", today, today);
        Assert.Equal(2, byProduct.Rows.Count);
        Assert.Contains("Coffee", StandaloneStore.ToCsv(byProduct));

        var sales = await store.SalesAsync(null, _time.GetUtcNow().AddDays(-1));
        Assert.Equal(2, Assert.Single(sales).Lines.Count);

        var closed = await pos.CloseShiftAsync(shift.Id, 938m, null, default);
        Assert.Equal(938m, closed!.ExpectedCash); // 500 float + 500 cash − 62 change
        Assert.Equal(0m, closed.Variance);
    }

    [Fact]
    public async Task Schema_v1_database_is_upgraded_in_place_without_losing_data()
    {
        await using (var db = Db())
        {
            db.Products.Add(new LocalProduct { Id = Guid.NewGuid(), Sku = "OLD", Name = "Old product", SalePrice = 5 });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"SaleLines\"; ALTER TABLE \"Products\" DROP COLUMN \"PurchasePrice\";");
            await db.SetValueAsync(LocalDb.Keys.SchemaVersion, "1");
        }
        await using (var db = Db()) await db.InitializeAsync();
        await using (var db = Db())
        {
            Assert.Equal("Old product", (await db.Products.SingleAsync()).Name);
            Assert.Equal(0, await db.SaleLines.CountAsync());
            Assert.Equal(LocalDb.SchemaVersion.ToString(), await db.GetValueAsync(LocalDb.Keys.SchemaVersion));
        }
    }

    // ------------------------------------------------------------------ backup

    [Fact]
    public async Task Encrypted_backup_round_trips_to_a_new_device_and_rejects_wrong_password()
    {
        var (_, pos, coffee, _) = await SeedAsync();
        var oldDevice = new StandaloneService(Store("old"), Db, _time, () => true, FastIterations);
        var owner = oldDevice.Auth.CreateOwner("Amina", "owner-pass-1");
        var cashier = oldDevice.Auth.AddUser("Karim", "246810", LocalRoles.Cashier);
        var reg = (await pos.RegistersAsync(default)).Registers[0];
        var shift = await pos.OpenShiftAsync(reg.Id, 0, default);
        var cart = new Cart();
        cart.Add(SellableProduct.From(coffee), 1);
        await pos.CheckoutAsync(cart, PosConfig.Default, reg.Id, reg.Code, shift, null, null, [new PaymentLine("Cash", 119m)], "Shop", "Amina", "fr", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => oldDevice.ExportBackupAsync(cashier, "246810", null, "Shop")); // owner only
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldDevice.ExportBackupAsync(owner, "not-the-password", null, "Shop"));
        var file = await oldDevice.ExportBackupAsync(owner, "owner-pass-1", null, "Shop");
        Assert.DoesNotContain("Coffee"u8.ToArray(), file.Chunk(6).Select(c => c.ToArray())); // ciphertext only
        Assert.Equal("VLXBAK01"u8.ToArray(), file[..8]);

        // New device: empty database, no accounts.
        await using var conn2 = new SqliteConnection("Data Source=:memory:");
        await conn2.OpenAsync();
        LocalDb Db2() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(conn2).Options);
        await using (var db = Db2()) await db.InitializeAsync();

        var unlicensed = new StandaloneService(Store("unlicensed"), Db2, _time, () => false, FastIterations);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => unlicensed.RestoreBackupAsync(file, "owner-pass-1"));
        Assert.Equal("dk.sa.err.licenseRequired", e.Message);

        var newDevice = new StandaloneService(Store("new"), Db2, _time, () => true, FastIterations);
        Assert.Equal("dk.sa.err.backupPassword", (await Assert.ThrowsAsync<InvalidOperationException>(() => newDevice.RestoreBackupAsync(file, "owner-pass-2"))).Message);
        var tampered = file.ToArray();
        tampered[^1] ^= 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => newDevice.RestoreBackupAsync(tampered, "owner-pass-1"));
        Assert.False(newDevice.Auth.HasOwner);

        await newDevice.RestoreBackupAsync(file, "owner-pass-1");
        Assert.Equal(LocalSignInStatus.SignedIn, newDevice.Auth.SignIn("Amina", "owner-pass-1").Status);
        Assert.Equal(LocalSignInStatus.SignedIn, newDevice.Auth.SignIn("Karim", "246810").Status);
        var restored = new StandaloneStore(Db2, _time);
        Assert.Equal(2, (await restored.ProductsAsync(1, 50, null)).TotalCount);
        Assert.Equal(119m, (await restored.DashboardAsync()).SalesToday);
        Assert.Equal(9m, (await restored.StockAsync(1, 50, "Coffee", false)).Items.Single().Quantity);
    }

    private sealed class PlainProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Reverse().ToArray();
    }

    private sealed class NoServer : IServerAddress { public string? ServerUrl => null; }

    private sealed class NoTokens : ITokenStore
    {
        public string? AccessToken => null;
        public string? RefreshToken => null;
        public void Save(TokenResponse tokens) { }
        public void Clear() { }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Standalone mode must not call the server: " + request.RequestUri);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
