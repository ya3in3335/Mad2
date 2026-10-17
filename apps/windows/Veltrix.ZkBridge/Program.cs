using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Veltrix.ZkBridge;

/// <summary>Request from VELTRIX (the 64-bit app or connector service). The device communication key never appears on the command line.</summary>
public sealed record BridgeRequest(string Op, string Host, int Port = 4370, int CommKey = 0, int MachineNumber = 1, string? Since = null);

public sealed record BridgeLog(string EnrollNumber, string LocalTime, int VerifyMode, int InOutMode, int WorkCode);

public sealed record BridgeReply(bool Ok, string? ErrorKind = null, string? Error = null, string? Serial = null, string? Firmware = null, string? Product = null,
    string? DeviceTime = null, IReadOnlyList<BridgeLog>? Logs = null);

/// <summary>
/// Calls the official ZKTeco Standalone SDK (COM class CZKEM, CLSID 00853A19-BD51-419B-9269-2DABE57EB61F) by late binding.
/// Method names and parameters follow the type library embedded in zkemkeeper.dll 6.2.4.11 (IZKEM): Connect_Net, SetCommPassword,
/// GetSerialNumber, GetFirmwareVersion, GetProductCode, GetDeviceTime, EnableDevice, ReadGeneralLogData, SSR_GetGeneralLogData,
/// GetLastError, Disconnect. Read-only: nothing is written to the terminal (logs are not cleared).
/// </summary>
public static class Program
{
    public static readonly Guid ZkemClsid = new("00853A19-BD51-419B-9269-2DABE57EB61F");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [STAThread] // the SDK's COM class is apartment-threaded
    public static int Main()
    {
        BridgeReply reply;
        try
        {
            var request = JsonSerializer.Deserialize<BridgeRequest>(Console.In.ReadToEnd(), Json) ?? throw new FormatException("empty request");
            reply = Run(request);
        }
        catch (Exception e) when (e is JsonException or FormatException) { reply = new BridgeReply(false, "ProtocolError", "Invalid request: " + e.Message); }
        catch (Exception e) { reply = new BridgeReply(false, "ProtocolError", e.GetType().Name + ": " + e.Message); }
        Console.Out.Write(JsonSerializer.Serialize(reply, Json));
        return reply.Ok ? 0 : 1;
    }

    private static BridgeReply Run(BridgeRequest r)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Unsupported", "The ZKTeco SDK runs on Windows only.");
        var type = Type.GetTypeFromCLSID(ZkemClsid, throwOnError: false);
        object? zk = null;
        try { zk = type is null ? null : Activator.CreateInstance(type); }
        catch (Exception) { zk = null; }
        if (zk is null)
            return new(false, "Unsupported", "ZKTeco SDK not installed: use \"Download ZKTeco SDK\" in Attendance terminals (or reinstall VELTRIX OS while online).");
        var m = r.MachineNumber;
        try
        {
            if (r.CommKey != 0) Call(zk, "SetCommPassword", r.CommKey);
            if (!Call(zk, "Connect_Net", r.Host, r.Port))
                return new(false, "Unreachable", $"Connect_Net({r.Host}:{r.Port}) failed (SDK error {LastError(zk)}). Check the IP address, port and communication key.");
            return r.Op switch
            {
                "info" => Info(zk, m),
                "logs" => Logs(zk, m, r.Since),
                _ => new(false, "ProtocolError", "Unknown op " + r.Op),
            };
        }
        finally
        {
            try { Call(zk, "Disconnect"); } catch (Exception) { /* closing */ }
        }
    }

    private static BridgeReply Info(object zk, int m)
    {
        var serial = new object?[] { m, "" };
        var fw = new object?[] { m, "" };
        var product = new object?[] { m, "" };
        var t = new object?[] { m, 0, 0, 0, 0, 0, 0 };
        string? S(object?[] a, string method) => Invoke(zk, method, a, byRefFrom: 1) is true ? a[^1] as string : null;
        var deviceTime = Invoke(zk, "GetDeviceTime", t, byRefFrom: 1) is true ? Stamp(t[1], t[2], t[3], t[4], t[5], t[6]) : null;
        return new(true, Serial: S(serial, "GetSerialNumber"), Firmware: S(fw, "GetFirmwareVersion"), Product: S(product, "GetProductCode"), DeviceTime: deviceTime);
    }

    private static BridgeReply Logs(object zk, int m, string? since)
    {
        var info = Info(zk, m);
        var logs = new List<BridgeLog>();
        Call(zk, "EnableDevice", m, false); // the SDK reads the log while the terminal is briefly disabled
        try
        {
            if (!Call(zk, "ReadGeneralLogData", m))
            {
                var err = LastError(zk);
                // No record on the terminal is reported as a failure with code 0 by the SDK.
                if (err != 0) return new(false, "ProtocolError", $"ReadGeneralLogData failed (SDK error {err}).");
                return info with { Logs = logs };
            }
            while (true)
            {
                var a = new object?[] { m, "", 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                if (Invoke(zk, "SSR_GetGeneralLogData", a, byRefFrom: 1) is not true) break;
                var stamp = Stamp(a[4], a[5], a[6], a[7], a[8], a[9]);
                if (stamp is null || since is not null && string.CompareOrdinal(stamp, since) < 0) continue;
                logs.Add(new BridgeLog(Convert.ToString(a[1], CultureInfo.InvariantCulture) ?? "", stamp, Convert.ToInt32(a[2], CultureInfo.InvariantCulture),
                    Convert.ToInt32(a[3], CultureInfo.InvariantCulture), Convert.ToInt32(a[10], CultureInfo.InvariantCulture)));
            }
        }
        finally
        {
            Call(zk, "EnableDevice", m, true);
        }
        return info with { Logs = logs };
    }

    private static string? Stamp(object? y, object? mo, object? d, object? h, object? mi, object? s)
    {
        try
        {
            var t = new DateTime(Convert.ToInt32(y, CultureInfo.InvariantCulture), Convert.ToInt32(mo, CultureInfo.InvariantCulture), Convert.ToInt32(d, CultureInfo.InvariantCulture),
                Convert.ToInt32(h, CultureInfo.InvariantCulture), Convert.ToInt32(mi, CultureInfo.InvariantCulture), Convert.ToInt32(s, CultureInfo.InvariantCulture));
            return t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static int LastError(object zk)
    {
        var a = new object?[] { 0 };
        try { Invoke(zk, "GetLastError", a, byRefFrom: 0); } catch (Exception) { return int.MinValue; }
        return Convert.ToInt32(a[0], CultureInfo.InvariantCulture);
    }

    private static bool Call(object zk, string method, params object?[] args) => Invoke(zk, method, args, byRefFrom: int.MaxValue) is true;

    /// <summary>IDispatch call; arguments from <paramref name="byRefFrom"/> on are passed by reference and written back into <paramref name="args"/>.</summary>
    private static object? Invoke(object zk, string method, object?[] args, int byRefFrom)
    {
        ParameterModifier[]? mods = null;
        if (byRefFrom < args.Length)
        {
            var pm = new ParameterModifier(args.Length);
            for (var i = byRefFrom; i < args.Length; i++) pm[i] = true;
            mods = [pm];
        }
        return zk.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, zk, args, mods, CultureInfo.InvariantCulture, null);
    }
}
