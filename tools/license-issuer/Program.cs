using Veltrix.Contracts.Licensing;
using Veltrix.LicenseIssuer;

const string Usage = """
    VELTRIX OS offline license issuer

      keygen --out <dir>
          Creates vendor-private-key.pem (keep secret, back up offline) and vendor-public-key.pem
          (copy to licensing/vendor-public-key.pem and rebuild the apps). Never overwrites.

      issue --key <vendor-private-key.pem> --device-code <code> --name "<shop>" --days <n> [--out <dir>]
          Prints an activation code for that device and writes <shop>-activation.txt.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--") || i + 1 >= args.Length) return Fail($"Unexpected argument '{args[i]}'.");
    opts[args[i][2..]] = args[++i];
}

try
{
    switch (args[0])
    {
        case "keygen":
        {
            if (!opts.TryGetValue("out", out var dir)) return Fail("--out is required.");
            var (priv, pub) = OfflineIssuer.GenerateKeys(dir);
            Console.WriteLine($"Private key: {Path.GetFullPath(priv)}  (SECRET: back it up offline, never commit it)");
            Console.WriteLine($"Public key:  {Path.GetFullPath(pub)}  (copy to licensing/vendor-public-key.pem, then rebuild)");
            return 0;
        }
        case "issue":
        {
            if (!opts.TryGetValue("key", out var keyPath) || !opts.TryGetValue("device-code", out var code)
                || !opts.TryGetValue("name", out var name) || !opts.TryGetValue("days", out var daysText))
                return Fail("--key, --device-code, --name and --days are required.");
            if (!int.TryParse(daysText, out var days) || days <= 0 || days > 36500) return Fail("--days must be between 1 and 36500.");
            if (!OfflineLicensing.TryDecodeDeviceCode(code, out _, out _)) return Fail("Invalid device code (expected 64 characters, spaces allowed).");
            var payload = OfflineIssuer.BuildPayload(code, name, days, DateTimeOffset.UtcNow);
            var cert = OfflineIssuer.Sign(payload, File.ReadAllText(keyPath));
            var outDir = opts.GetValueOrDefault("out") ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outDir);
            var file = Path.Combine(outDir, OfflineIssuer.SafeFileName(name) + "-activation.txt");
            File.WriteAllText(file, OfflineIssuer.ActivationFileText(payload, cert));
            Console.WriteLine($"Shop: {payload.TenantName}   Valid until: {DateTimeOffset.FromUnixTimeSeconds(payload.Exp):yyyy-MM-dd} UTC");
            Console.WriteLine("Activation code:");
            Console.WriteLine(cert);
            Console.WriteLine($"Written to {Path.GetFullPath(file)}");
            return 0;
        }
        default:
            return Fail($"Unknown command '{args[0]}'.");
    }
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException
                              or System.Security.Cryptography.CryptographicException)
{
    return Fail(e.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine("Error: " + message);
    Console.Error.WriteLine("Run with --help for usage.");
    return 2;
}
