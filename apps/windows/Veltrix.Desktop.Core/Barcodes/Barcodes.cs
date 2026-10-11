namespace Veltrix.Desktop.Core.Barcodes;

public enum BarcodeKind { Ean13, Code128B }

/// <summary>A dark bar: start and width in modules (narrowest bar unit). Guard bars are drawn longer on EAN-13 labels.</summary>
public readonly record struct BarRun(int Start, int Width, bool IsGuard);

/// <summary>
/// An encoded symbol: <see cref="Modules"/> holds one entry per module (true = dark), without quiet zones.
/// <see cref="QuietLeft"/>/<see cref="QuietRight"/> are the minimum light margins (in modules) the renderer must keep.
/// </summary>
public sealed record BarcodeSymbol(BarcodeKind Kind, string Text, bool[] Modules, IReadOnlyList<BarRun> Bars, int QuietLeft, int QuietRight)
{
    public int Width => Modules.Length;
    public int TotalWidth => QuietLeft + Modules.Length + QuietRight;

    internal static IReadOnlyList<BarRun> Runs(bool[] modules, Func<int, bool> isGuard)
    {
        var runs = new List<BarRun>();
        for (var i = 0; i < modules.Length;)
        {
            if (!modules[i]) { i++; continue; }
            var start = i;
            while (i < modules.Length && modules[i]) i++;
            runs.Add(new BarRun(start, i - start, isGuard(start)));
        }
        return runs;
    }
}

public static class BarcodeEncoder
{
    /// <summary>EAN-13 when the code is 13 digits with a valid check digit (or 12 digits: check digit appended), Code 128-B otherwise.</summary>
    public static BarcodeSymbol Encode(string code)
    {
        code = (code ?? "").Trim();
        if (code.Length == 13 && Ean13.IsValid(code)) return Ean13.Encode(code);
        if (code.Length == 12 && Ean13.IsDigits(code)) return Ean13.Encode(code + Ean13.CheckDigit(code));
        return Code128.Encode(code);
    }
}

/// <summary>EAN-13 (GS1): L/G/R digit patterns, parity from the first digit, 3-5-3 guard bars, mod-10 check digit.</summary>
public static class Ean13
{
    // L-code (odd parity) for digits 0-9, 7 modules each. R = complement of L; G = R reversed.
    private static readonly string[] L = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    private static readonly string[] Parity = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];

    public const int ModuleCount = 95;
    public const int QuietLeft = 11;
    public const int QuietRight = 7;

    public static bool IsDigits(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');

    public static string LPattern(int digit) => L[digit];
    public static string RPattern(int digit) => new(L[digit].Select(c => c == '0' ? '1' : '0').ToArray());
    public static string GPattern(int digit) => new(RPattern(digit).Reverse().ToArray());

    /// <summary>Check digit for the first 12 digits: weights 1,3,1,3… from the left.</summary>
    public static int CheckDigit(string twelveDigits)
    {
        if (twelveDigits is null || twelveDigits.Length != 12 || !IsDigits(twelveDigits)) throw new ArgumentException("12 digits expected.", nameof(twelveDigits));
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (twelveDigits[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10;
    }

    public static bool IsValid(string? code) =>
        code is { Length: 13 } && IsDigits(code) && CheckDigit(code[..12]) == code[12] - '0';

    public static BarcodeSymbol Encode(string code)
    {
        if (!IsValid(code)) throw new ArgumentException("Invalid EAN-13: " + code, nameof(code));
        var sb = new System.Text.StringBuilder(ModuleCount);
        sb.Append("101");
        var parity = Parity[code[0] - '0'];
        for (var i = 1; i <= 6; i++)
        {
            var d = code[i] - '0';
            sb.Append(parity[i - 1] == 'L' ? LPattern(d) : GPattern(d));
        }
        sb.Append("01010");
        for (var i = 7; i <= 12; i++) sb.Append(RPattern(code[i] - '0'));
        sb.Append("101");
        var modules = sb.ToString().Select(c => c == '1').ToArray();
        return new BarcodeSymbol(BarcodeKind.Ean13, code, modules, BarcodeSymbol.Runs(modules, IsGuardModule), QuietLeft, QuietRight);
    }

    /// <summary>Start (0-2), centre (45-49) and end (92-94) guard modules.</summary>
    public static bool IsGuardModule(int m) => m < 3 || (m >= 45 && m < 50) || m >= 92;
}

/// <summary>Code 128 subset B (ASCII 32-127): start B, data, mod-103 checksum, stop.</summary>
public static class Code128
{
    /// <summary>Bar/space widths for values 0-106 (106 = stop, 7 elements).</summary>
    internal static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    public const int StartB = 104;
    public const int Stop = 106;
    public const int QuietZone = 10;

    public static bool CanEncode(string text) => !string.IsNullOrEmpty(text) && text.All(c => c is >= ' ' and <= (char)127);

    /// <summary>Symbol values: start B, data (char - 32), checksum.</summary>
    public static int[] Values(string text)
    {
        if (!CanEncode(text)) throw new ArgumentException("Code 128-B accepts ASCII 32-127 only.", nameof(text));
        var values = new List<int> { StartB };
        values.AddRange(text.Select(c => c - 32));
        values.Add(Checksum(values.Skip(1).ToArray()));
        return [.. values];
    }

    /// <summary>(start + Σ position × value) mod 103, positions starting at 1.</summary>
    public static int Checksum(IReadOnlyList<int> dataValues, int start = StartB)
    {
        var sum = start;
        for (var i = 0; i < dataValues.Count; i++) sum += (i + 1) * dataValues[i];
        return sum % 103;
    }

    public static BarcodeSymbol Encode(string text)
    {
        var modules = new List<bool>();
        foreach (var v in Values(text).Append(Stop))
        {
            var dark = true;
            foreach (var w in Patterns[v])
            {
                for (var k = 0; k < w - '0'; k++) modules.Add(dark);
                dark = !dark;
            }
        }
        var arr = modules.ToArray();
        return new BarcodeSymbol(BarcodeKind.Code128B, text, arr, BarcodeSymbol.Runs(arr, _ => false), QuietZone, QuietZone);
    }
}
