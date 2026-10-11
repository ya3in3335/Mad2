using System.Globalization;
using System.Xml.Linq;
using Veltrix.Desktop.Core.Settings;

namespace Veltrix.Desktop.Tests;

/// <summary>Accent palettes (apps/windows/Veltrix.Desktop/Themes/Accents/Accent.*.xaml) are swapped at runtime, so they must all define the
/// same keys, and the on-accent text must stay readable (WCAG AA, 4.5:1) on every stop of the primary gradient.</summary>
public class AccentThemeTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string AccentsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "windows", "Veltrix.Desktop", "Themes", "Accents");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Themes/Accents not found above " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, XDocument> Load() =>
        Directory.GetFiles(AccentsDir(), "Accent.*.xaml").ToDictionary(f => Path.GetFileName(f), f => XDocument.Load(f));

    private static string Key(XElement e) => (string?)e.Attribute(X + "Key") ?? "";

    private static XElement Resource(XDocument doc, string key) => doc.Root!.Elements().Single(e => Key(e) == key);

    [Fact]
    public void Every_catalog_accent_has_a_dark_and_light_dictionary()
    {
        var files = Load().Keys.ToHashSet();
        Assert.Equal(Accents.All.Count * 2, files.Count);
        foreach (var a in Accents.All)
        {
            Assert.Contains($"Accent.{a.File}.Dark.xaml", files);
            Assert.Contains($"Accent.{a.File}.Light.xaml", files);
        }
        Assert.Equal(Accents.All.Count, Accents.All.Select(a => a.Code).Distinct().Count());
        Assert.Equal(Accents.Default, Accents.Normalize(null));
        Assert.Equal(Accents.Default, Accents.Normalize("no-such-accent"));
        Assert.Equal("aurora", Accents.Normalize("Aurora"));
        Assert.Equal(Accents.Default, new AppSettings().Accent);
    }

    [Fact]
    public void All_accent_dictionaries_define_the_identical_key_set()
    {
        var docs = Load();
        var reference = docs.First();
        var expected = reference.Value.Root!.Elements().Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(expected.Count, expected.Distinct().Count());
        Assert.Contains("Brush.primaryGradient", expected);
        Assert.Contains("Brush.onPrimary", expected);
        foreach (var (name, doc) in docs)
        {
            var keys = doc.Root!.Elements().Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(expected.SequenceEqual(keys), $"{name} key set differs from {reference.Key}: " +
                string.Join(", ", expected.Except(keys).Select(k => "-" + k).Concat(keys.Except(expected).Select(k => "+" + k))));
        }
    }

    [Fact]
    public void On_accent_text_has_at_least_4_5_contrast_on_every_gradient_stop()
    {
        foreach (var (name, doc) in Load())
        {
            var on = Parse((string)Resource(doc, "Brush.onPrimary").Attribute("Color")!);
            var stops = Resource(doc, "Brush.primaryGradient").Elements(P + "GradientStop").Select(s => Parse((string)s.Attribute("Color")!)).ToList();
            Assert.True(stops.Count >= 2, name);
            foreach (var stop in stops)
            {
                var ratio = Contrast(on, stop);
                Assert.True(ratio >= 4.5, $"{name}: on-accent vs {stop} = {ratio:F2}:1");
            }
        }
    }

    [Fact]
    public void Settings_swatches_mirror_the_dark_palette()
    {
        var docs = Load();
        foreach (var a in Accents.All)
        {
            var doc = docs[$"Accent.{a.File}.Dark.xaml"];
            var stops = Resource(doc, "Brush.primaryGradient").Elements(P + "GradientStop").Select(s => (string)s.Attribute("Color")!).ToList();
            Assert.Equal([a.Start, a.Mid, a.End], stops);
            Assert.Equal(a.OnAccent, (string)Resource(doc, "Brush.onPrimary").Attribute("Color")!);
            foreach (var c in new[] { a.Start, a.Mid, a.End })
                Assert.True(Contrast(Parse(a.OnAccent), Parse(c)) >= 4.5, $"{a.Code} swatch check mark on {c}");
        }
    }

    private static (double R, double G, double B) Parse(string hex)
    {
        hex = hex.TrimStart('#');
        Assert.True(hex.Length == 6, $"opaque #RRGGBB expected, got #{hex}");
        double C(int i) => int.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber) / 255.0;
        return (C(0), C(2), C(4));
    }

    private static double Luminance((double R, double G, double B) c)
    {
        static double L(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    private static double Contrast((double, double, double) a, (double, double, double) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
