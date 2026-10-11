using System.Globalization;
using System.Windows.Input;
using System.Xml.Linq;
using Veltrix.Desktop.Presentation;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Tests;

/// <summary>Keyboard shortcut table (<see cref="ShortcutMap"/>) and the single-instance helper.</summary>
public class ShortcutTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string LocalizationDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "windows", "Veltrix.Desktop", "Localization");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Localization not found above " + AppContext.BaseDirectory);
    }

    private static HashSet<string> Keys(string lang) =>
        Directory.GetFiles(LocalizationDir(), $"Strings*.{lang}.xaml")
            .SelectMany(f => XDocument.Load(f).Root!.Elements().Select(e => (string?)e.Attribute(X + "Key") ?? ""))
            .ToHashSet();

    [Fact]
    public void No_duplicate_gesture_within_a_scope()
    {
        var dups = ShortcutMap.All.GroupBy(s => (s.Scope, s.Gesture)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dups);
    }

    [Fact]
    public void Page_shortcuts_never_shadow_shell_or_every_page_gestures()
    {
        var shared = ShortcutMap.All.Where(s => s.Scope is ShortcutMap.Global or ShortcutMap.Page).Select(s => s.Gesture).ToList();
        Assert.Equal(shared.Count, shared.Distinct().Count());
        var clashes = ShortcutMap.All.Where(s => s.Scope is not (ShortcutMap.Global or ShortcutMap.Page or ShortcutMap.System) && shared.Contains(s.Gesture)).ToList();
        Assert.Empty(clashes);
    }

    [Fact]
    public void Gestures_are_canonical_and_avoid_reserved_or_text_editing_keys()
    {
        string[] reserved = ["Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Win+V"];
        foreach (var s in ShortcutMap.All)
        {
            Assert.Equal(ShortcutMap.Normalize(s.Gesture), s.Gesture);
            Assert.DoesNotContain(s.Gesture, reserved);
            // Delete / Enter double as text editing keys: they must yield to a focused text box.
            if (s.Gesture is "Delete" or "Enter") Assert.True(s.YieldsToText, $"{s.Scope} {s.Gesture}");
        }
        Assert.Equal("Ctrl+Shift+T", ShortcutMap.Normalize("shift+ctrl+t"));
        Assert.Equal("Ctrl+/", ShortcutMap.Normalize("ctrl+/"));
    }

    [Fact]
    public void Required_conventions_are_present()
    {
        bool Has(string scope, string gesture) => ShortcutMap.All.Any(s => s.Scope == scope && s.Gesture == gesture);
        foreach (var g in new[] { "F1", "Ctrl+/", "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+L", "Ctrl+Shift+T" }.Concat(Enumerable.Range(1, 9).Select(i => $"Ctrl+{i}")))
            Assert.True(Has(ShortcutMap.Global, g), g);
        Assert.True(Has(ShortcutMap.Page, "F5"));
        Assert.True(Has(ShortcutMap.Page, "Ctrl+F"));
        foreach (var g in new[] { "F2", "F3", "F4", "F7", "F8", "F9", "F12", "Ctrl+F3", "Esc", "Enter", "Delete" })
            Assert.True(Has("pos", g), g);
        Assert.True(Has(ShortcutMap.System, ShortcutMap.LaunchHotkey));
        Assert.Equal("Ctrl+Alt+V", ShortcutMap.LaunchHotkey);
    }

    [Fact]
    public void Every_shortcut_maps_to_an_existing_command_and_properties()
    {
        foreach (var s in ShortcutMap.All)
        {
            if (s.IsViewAction)
            {
                Assert.Contains(s.Command, new[] { ShortcutMap.FocusSearch, ShortcutMap.FocusScan, ShortcutMap.ToggleTheme, ShortcutMap.Launch });
                continue;
            }
            Assert.True(ShortcutMap.ScopeTypes.TryGetValue(s.Scope, out var type), $"unknown scope {s.Scope}");
            var cmd = type!.GetProperty(s.Command);
            Assert.True(cmd is not null && typeof(ICommand).IsAssignableFrom(cmd.PropertyType), $"{type.Name}.{s.Command}");
            if (s.Parameter is not null) Assert.NotNull(type.GetProperty(s.Parameter));
            if (s.When is not null) Assert.NotNull(type.GetProperty(s.When));
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("ar")]
    public void Every_shortcut_label_is_localized(string lang)
    {
        var keys = Keys(lang);
        var needed = ShortcutMap.All.Select(s => s.LabelKey).Concat(ShortcutMap.GroupTitleKeys.Values).Append("sc.title").Append("sc.hint").Distinct();
        Assert.Empty(needed.Where(k => !keys.Contains(k)));
    }

    [Fact]
    public void Help_groups_merge_gestures_and_list_visible_pages_only()
    {
        var groups = ShortcutMap.Groups(new KeyLocalizer(), [("pos", "POS"), ("dashboard", "Dashboard")]);
        Assert.Equal(["sc.group.global", "sc.group.page", "POS", "sc.group.system"], groups.Select(g => g.Title));
        var global = groups[0].Rows;
        Assert.Contains(new ShortcutRow("Ctrl+1 … Ctrl+9", "sc.goto"), global);
        Assert.Contains(new ShortcutRow("F1 / Ctrl+/", "sc.help"), global);
        Assert.Contains(new ShortcutRow("Ctrl+Alt+V", "sc.launch"), groups[^1].Rows);
    }

    [Fact]
    public void Single_instance_second_claim_is_refused_until_the_first_is_released()
    {
        var id = "VELTRIX-Test-" + Guid.NewGuid().ToString("N");
        Assert.Equal(($@"Local\{id}", $@"Local\{id}-Activate"), SingleInstance.Names(id));
        // Claims from another thread (mutex ownership is per thread, as with a second process).
        SingleInstance? Other() => Task.Run(() => SingleInstance.TryClaim(id, null)).Result;
        using (var first = SingleInstance.TryClaim(id, null))
        {
            Assert.NotNull(first);
            Assert.Null(Other());
        }
        using var again = SingleInstance.TryClaim(id, null);
        Assert.NotNull(again);
    }

    private sealed class KeyLocalizer : ILocalizer
    {
        public string Get(string key) => key;
        public CultureInfo Culture => CultureInfo.InvariantCulture;
        public event EventHandler? LanguageChanged { add { } remove { } }
    }
}
