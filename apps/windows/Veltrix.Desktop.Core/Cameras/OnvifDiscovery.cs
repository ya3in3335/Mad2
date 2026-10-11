using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Veltrix.Desktop.Core.Cameras;

public sealed record DiscoveredDevice(string Address, IReadOnlyList<string> XAddrs, string? Name, string? Hardware, CameraBrand BrandGuess)
{
    public string Display => string.IsNullOrEmpty(Name) ? Address : $"{Name} ({Address})";
}

/// <summary>
/// ONVIF WS-Discovery: one Probe for NetworkVideoTransmitter sent to 239.255.255.250:3702 from every local IPv4 interface
/// (wired and Wi-Fi), answers collected for a few seconds. Only devices on private/link-local addresses are returned.
/// </summary>
public static class OnvifDiscovery
{
    public const string MulticastAddress = "239.255.255.250";
    public const int Port = 3702;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public static string BuildProbe(Guid messageId) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope" xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
        <e:Header><w:MessageID>uuid:{messageId:D}</w:MessageID><w:To e:mustUnderstand="true">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To><w:Action e:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action></e:Header>
        <e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body>
        </e:Envelope>
        """;

    /// <summary>Parses a ProbeMatches response. <paramref name="sender"/> is used when the device lists no usable XAddr. Returns an empty list for anything else.</summary>
    public static IReadOnlyList<DiscoveredDevice> ParseProbeMatches(string xml, string? sender = null)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.None); }
        catch (XmlException) { return []; }
        var list = new List<DiscoveredDevice>();
        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            string? Child(string name) => match.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            var xaddrs = (Child("XAddrs") ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")).ToList();
            var scopes = (Child("Scopes") ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string? Scope(string kind) => scopes.Select(s => s.Trim()).Where(s => s.StartsWith($"onvif://www.onvif.org/{kind}/", StringComparison.OrdinalIgnoreCase))
                .Select(s => Uri.UnescapeDataString(s[$"onvif://www.onvif.org/{kind}/".Length..])).FirstOrDefault(s => s.Length > 0);
            var name = Scope("name");
            var hardware = Scope("hardware");
            // Prefer a local IPv4 XAddr host; fall back to the UDP sender.
            var v4 = xaddrs.Select(a => new Uri(a)).Where(u => u.HostNameType == UriHostNameType.IPv4).Select(u => u.Host).ToList();
            // A device advertising only public addresses is not a LAN camera: skip it rather than trusting the UDP sender.
            var host = v4.Count > 0 ? v4.FirstOrDefault(h => IPAddress.TryParse(h, out var ip) && LanAddress.IsLocal(ip)) : sender;
            if (host is null || !IPAddress.TryParse(host, out var hip) || !LanAddress.IsLocal(hip)) continue;
            var hint = string.Join(' ', scopes) + " " + name + " " + hardware;
            var brand = hint.Contains("hikvision", StringComparison.OrdinalIgnoreCase) || hint.Contains("hikvison", StringComparison.OrdinalIgnoreCase) ? CameraBrand.Hikvision
                : hint.Contains("dahua", StringComparison.OrdinalIgnoreCase) ? CameraBrand.Dahua : CameraBrand.Generic;
            list.Add(new DiscoveredDevice(host, xaddrs, name, hardware, brand));
        }
        return list;
    }

    /// <summary>Merges answers by address (devices often answer once per interface or repeat the reply).</summary>
    public static IReadOnlyList<DiscoveredDevice> Merge(IEnumerable<DiscoveredDevice> devices) =>
        devices.GroupBy(d => d.Address).Select(g => g.First() with { XAddrs = g.SelectMany(d => d.XAddrs).Distinct().ToList() })
            .OrderBy(d => IPAddress.Parse(d.Address).GetAddressBytes(), Comparer<byte[]>.Create((a, b) =>
            {
                for (var i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
                return a.Length.CompareTo(b.Length);
            })).ToList();

    /// <summary>Probes the local network. Never throws for network errors; returns what was found before <paramref name="timeout"/>.</summary>
    public static async Task<IReadOnlyList<DiscoveredDevice>> ProbeAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var found = new List<DiscoveredDevice>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        var tasks = LocalInterfaceAddresses().Select(a => ProbeFromAsync(a, found, cts.Token)).ToList();
        if (tasks.Count == 0) tasks.Add(ProbeFromAsync(IPAddress.Any, found, cts.Token));
        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        lock (found) return Merge(found);
    }

    /// <summary>IPv4 addresses of every operational LAN interface (Ethernet, Wi-Fi…), private/link-local only.</summary>
    public static IReadOnlyList<IPAddress> LocalInterfaceAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.SupportsMulticast)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && LanAddress.IsLocal(a) && !IPAddress.IsLoopback(a))
                .Distinct().ToList();
        }
        catch (NetworkInformationException) { return []; }
    }

    private static async Task ProbeFromAsync(IPAddress local, List<DiscoveredDevice> found, CancellationToken ct)
    {
        var messageId = Guid.NewGuid();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1); // never leaves the LAN segment
            if (!local.Equals(IPAddress.Any)) udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            var probe = Encoding.UTF8.GetBytes(BuildProbe(messageId));
            var target = new IPEndPoint(IPAddress.Parse(MulticastAddress), Port);
            await udp.SendAsync(probe, target, ct);
            while (!ct.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(ct);
                var xml = Encoding.UTF8.GetString(r.Buffer);
                if (!xml.Contains(messageId.ToString("D"), StringComparison.OrdinalIgnoreCase)) continue; // RelatesTo another probe
                var devices = ParseProbeMatches(xml, r.RemoteEndPoint.Address.ToString());
                lock (found) found.AddRange(devices);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }
}
