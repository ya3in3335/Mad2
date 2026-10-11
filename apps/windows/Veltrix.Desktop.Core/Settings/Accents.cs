namespace Veltrix.Desktop.Core.Settings;

/// <summary>One selectable accent colour. <see cref="File"/> names the Themes/Accents/Accent.{File}.{Dark|Light}.xaml pair;
/// the swatch colours mirror the dark variant's Brush.primaryGradient stops and Brush.onPrimary (checked by AccentThemeTests).</summary>
public sealed record AccentInfo(string Code, string File, string Start, string Mid, string End, string OnAccent)
{
    /// <summary>Localized display name resource key (dk.accent.*).</summary>
    public string NameKey => "dk.accent." + Code;
}

/// <summary>Catalog of the accent palettes offered in Settings.</summary>
public static class Accents
{
    /// <summary>Used when no accent was ever stored (or the stored one is unknown).</summary>
    public const string Default = "cyan";

    public static IReadOnlyList<AccentInfo> All { get; } =
    [
        new("silver", "Silver", "#F2F4F8", "#C8CDD8", "#8A90A0", "#0A0B10"),
        new("cyan", "Cyan", "#A5F3FC", "#22D3EE", "#06B6D4", "#0A0B10"),
        new("blue", "Blue", "#93C5FD", "#60A5FA", "#3B82F6", "#0A0B10"),
        new("violet", "Violet", "#7C3AED", "#6D28D9", "#5B21B6", "#FFFFFF"),
        new("magenta", "Magenta", "#C026D3", "#A21CAF", "#86198F", "#FFFFFF"),
        new("rose", "Rose", "#E11D48", "#BE123C", "#9F1239", "#FFFFFF"),
        new("orange", "Orange", "#FDBA74", "#FB923C", "#F97316", "#0A0B10"),
        new("amber", "Amber", "#FDE68A", "#FCD34D", "#F59E0B", "#0A0B10"),
        new("lime", "Lime", "#D9F99D", "#A3E635", "#84CC16", "#0A0B10"),
        new("emerald", "Emerald", "#6EE7B7", "#34D399", "#10B981", "#0A0B10"),
        new("teal", "Teal", "#5EEAD4", "#2DD4BF", "#14B8A6", "#0A0B10"),
        new("aurora", "Aurora", "#22D3EE", "#A78BFA", "#F472B6", "#0A0B10"),
    ];

    public static AccentInfo Get(string? code) =>
        All.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase)) ?? All.First(a => a.Code == Default);

    public static string Normalize(string? code) => Get(code).Code;
}
