using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Barcodes;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Tests;

/// <summary>EAN-13 / Code 128-B encoders, the in-store code generator and label geometry.</summary>
public sealed class BarcodeTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private LocalDb Db() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_conn).Options);

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        await using var db = Db();
        await db.InitializeAsync();
    }

    public async Task DisposeAsync() => await _conn.DisposeAsync();

    private static string Bits(BarcodeSymbol s) => new(s.Modules.Select(m => m ? '1' : '0').ToArray());

    [Theory]
    [InlineData("590123412345", 7)]
    [InlineData("400638133393", 1)]
    [InlineData("978020137962", 4)]
    [InlineData("000000000000", 0)]
    public void Ean13_check_digit(string twelve, int expected) => Assert.Equal(expected, Ean13.CheckDigit(twelve));

    [Theory]
    [InlineData("4006381333931", true)]
    [InlineData("5901234123457", true)]
    [InlineData("4006381333932", false)]
    [InlineData("400638133393", false)]
    [InlineData("40063813339a1", false)]
    public void Ean13_validation(string code, bool valid) => Assert.Equal(valid, Ean13.IsValid(code));

    [Fact]
    public void Ean13_digit_patterns_follow_L_G_R_rules()
    {
        Assert.Equal("0001101", Ean13.LPattern(0));
        Assert.Equal("1110010", Ean13.RPattern(0));
        Assert.Equal("0100111", Ean13.GPattern(0));
        Assert.Equal("0010111", Ean13.GPattern(9));
        for (var d = 0; d < 10; d++)
        {
            // Each digit: 7 modules, 2 bars + 2 spaces; L/G start light (odd/even parity), R starts dark.
            Assert.Equal(7, Ean13.LPattern(d).Length);
            Assert.Equal(1, Ean13.LPattern(d).Count(c => c == '1') % 2);
            Assert.Equal(0, Ean13.GPattern(d).Count(c => c == '1') % 2);
            Assert.StartsWith("1", Ean13.RPattern(d));
        }
    }

    [Fact]
    public void Ean13_encodes_95_modules_with_guards_and_quiet_zones()
    {
        var s = Ean13.Encode("4006381333931");
        Assert.Equal(BarcodeKind.Ean13, s.Kind);
        Assert.Equal(95, s.Width);
        var bits = Bits(s);
        Assert.StartsWith("101", bits);
        Assert.EndsWith("101", bits);
        Assert.Equal("01010", bits.Substring(45, 5));
        // First digit 4 → parity LGLLGG; second digit 0 in L, third digit 0 in G.
        Assert.Equal(Ean13.LPattern(0), bits.Substring(3, 7));
        Assert.Equal(Ean13.GPattern(0), bits.Substring(10, 7));
        Assert.Equal(Ean13.RPattern(1), bits.Substring(85, 7)); // check digit, last right-hand digit
        Assert.Equal(11, s.QuietLeft);
        Assert.Equal(7, s.QuietRight);
        Assert.Equal(6, s.Bars.Count(b => b.IsGuard));
        Assert.Equal(bits.Count(c => c == '1'), s.Bars.Sum(b => b.Width));
        Assert.All(s.Bars, b => Assert.InRange(b.Width, 1, 4));
    }

    [Fact]
    public void Code128_patterns_are_unique_and_11_modules_wide()
    {
        Assert.Equal(107, Code128.Patterns.Length);
        Assert.Equal(107, Code128.Patterns.Distinct().Count());
        Assert.All(Code128.Patterns.Take(106), p => Assert.Equal(11, p.Sum(c => c - '0')));
        Assert.Equal(13, Code128.Patterns[106].Sum(c => c - '0'));
    }

    [Fact]
    public void Code128_checksum_and_symbol_length()
    {
        // "AB": 104 + 1×33 + 2×34 = 205 → 205 mod 103 = 102
        Assert.Equal([104, 33, 34, 102], Code128.Values("AB"));
        // "PJJ123C": 104 + 48 + 2×42 + 3×42 + 4×17 + 5×18 + 6×19 + 7×35 = 879 → 879 mod 103 = 55
        Assert.Equal(55, Code128.Values("PJJ123C")[^1]);
        var s = Code128.Encode("SKU-42a");
        Assert.Equal(BarcodeKind.Code128B, s.Kind);
        Assert.Equal(11 * (1 + 7 + 1) + 13, s.Width);
        Assert.True(s.Modules[0]);
        Assert.True(s.Modules[^1]);
        Assert.Throws<ArgumentException>(() => Code128.Values("é"));
    }

    [Fact]
    public void Encoder_picks_ean13_for_valid_numeric_and_code128_otherwise()
    {
        Assert.Equal(BarcodeKind.Ean13, BarcodeEncoder.Encode("4006381333931").Kind);
        Assert.Equal("5901234123457", BarcodeEncoder.Encode("590123412345").Text);
        Assert.Equal(BarcodeKind.Code128B, BarcodeEncoder.Encode("4006381333932").Kind);
        Assert.Equal(BarcodeKind.Code128B, BarcodeEncoder.Encode("ABC-1").Kind);
    }

    [Fact]
    public void Generator_builds_in_store_ean13()
    {
        var code = InStoreBarcodeGenerator.Build(1);
        Assert.Equal("2100000000012", code);
        Assert.True(Ean13.IsValid(code));
        Assert.True(InStoreBarcodeGenerator.IsInStore(code));
        Assert.False(InStoreBarcodeGenerator.IsInStore("4006381333931"));
    }

    [Fact]
    public async Task Generator_is_unique_persistent_and_skips_existing_codes()
    {
        await using (var db = Db())
        {
            db.Products.Add(new LocalProduct { Id = Guid.NewGuid(), Sku = "A", Name = "A", Barcodes = "123\n" + InStoreBarcodeGenerator.Build(2) });
            db.Products.Add(new LocalProduct { Id = Guid.NewGuid(), Sku = InStoreBarcodeGenerator.Build(3), Name = "B" });
            await db.SaveChangesAsync();
        }
        var gen = new InStoreBarcodeGenerator(Db);
        var codes = new List<string>();
        for (var i = 0; i < 5; i++) codes.Add(await gen.NextAsync());
        Assert.Equal(5, codes.Distinct().Count());
        Assert.All(codes, c => { Assert.StartsWith("21", c); Assert.Equal(13, c.Length); Assert.True(Ean13.IsValid(c)); });
        Assert.Equal(InStoreBarcodeGenerator.Build(1), codes[0]);
        Assert.Equal(InStoreBarcodeGenerator.Build(4), codes[1]); // 2 and 3 are taken
        // A new generator (e.g. after restart) continues from the persisted counter.
        var next = await new InStoreBarcodeGenerator(Db).NextAsync();
        Assert.DoesNotContain(next, codes);
        Assert.Equal(InStoreBarcodeGenerator.Build(8), next);
    }

    [Fact]
    public void Label_price_and_geometry()
    {
        Assert.Equal("90.00 DA", LabelLayout.FormatPrice(90m));
        Assert.Equal("1250.50 DA", LabelLayout.FormatPrice(1250.5m));
        Assert.Equal("ORANGE JUICE 1L", new LabelContent("Orange juice 1l", "x", 1, null).DisplayName);
        var cells = LabelLayout.SheetCells(8, 3, 8, 2);
        Assert.Equal(24, cells.Count);
        Assert.Equal(8, cells[0].X, 6);
        Assert.Equal(210 - 8, cells[2].X + cells[2].Width, 6);
        Assert.Equal(297 - 8, cells[^1].Y + cells[^1].Height, 6);
        var labels = LabelLayout.Expand([(new LabelContent("a", "1", 1, null), 3), (new LabelContent("b", "2", 1, null), 0), (new LabelContent("c", "3", 1, null), 25)]);
        Assert.Equal(28, labels.Count);
        Assert.Equal([24, 4], LabelLayout.Paginate(labels, 24).Select(p => p.Count));
        var ean = Ean13.Encode("4006381333931");
        Assert.Equal(36.0 / 113, LabelLayout.ModuleWidthMm(ean, 36), 6);
        Assert.Equal(LabelLayout.MinModuleMm, LabelLayout.ModuleWidthMm(ean, 10));
        Assert.Equal(0.375, LabelLayout.SnapToDots(0.49));
        Assert.Equal(LabelLayout.MinModuleMm, LabelLayout.SnapToDots(0.1));
    }
}
