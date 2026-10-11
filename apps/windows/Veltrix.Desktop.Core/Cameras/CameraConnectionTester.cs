using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Veltrix.Desktop.Core.Cameras;

public enum CameraTestStatus { Ok, AuthFailed, NotFound, Unreachable, NotLan, InvalidUrl, Error }

public sealed record CameraTestResult(CameraTestStatus Status, string? Detail = null);

/// <summary>"Test connection": RTSP DESCRIBE (digest or basic auth) against the stream URL, LAN hosts only, 5 s budget.</summary>
public static class CameraConnectionTester
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<CameraTestResult> TestAsync(CameraConfig cam, CameraStream stream, CancellationToken ct = default)
    {
        var url = RtspUrlBuilder.Build(cam, stream, includeCredentials: false);
        if (!RtspUrlBuilder.TryGetEndpoint(url, out var host, out var port)) return new(CameraTestStatus.InvalidUrl);
        var ip = await LanAddress.ResolveLocalAsync(host, ct);
        if (ip is null) return new(LanAddress.IsAcceptableHostText(host) ? CameraTestStatus.Unreachable : CameraTestStatus.NotLan);
        var (user, pass) = cam.Brand == CameraBrand.Generic ? GenericCredentials(cam) : (cam.Username, cam.Password);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var tcp = new TcpClient(ip.AddressFamily);
            await tcp.ConnectAsync(ip, port, cts.Token);
            if (!url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) return new(CameraTestStatus.Ok); // HTTP/MJPEG: reachability only
            var stream_ = tcp.GetStream();
            var (status, headers) = await SendAsync(stream_, Describe(url, 1, null), cts.Token);
            if (status == 401 && user.Length > 0 && headers.TryGetValue("www-authenticate", out var challenges))
            {
                var auth = Authorization(challenges, "DESCRIBE", url, user, pass);
                if (auth is not null) (status, _) = await SendAsync(stream_, Describe(url, 2, auth), cts.Token);
            }
            return status switch
            {
                >= 200 and < 300 => new(CameraTestStatus.Ok),
                401 or 403 => new(CameraTestStatus.AuthFailed),
                404 => new(CameraTestStatus.NotFound),
                _ => new(CameraTestStatus.Error, "RTSP " + status.ToString(CultureInfo.InvariantCulture)),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(CameraTestStatus.Unreachable); }
        catch (SocketException) { return new(CameraTestStatus.Unreachable); }
        catch (IOException) { return new(CameraTestStatus.Unreachable); }
    }

    private static (string User, string Pass) GenericCredentials(CameraConfig cam)
    {
        if (Uri.TryCreate(cam.CustomUrl, UriKind.Absolute, out var u) && u.UserInfo.Length > 0)
        {
            var parts = u.UserInfo.Split(':', 2);
            return (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return (cam.Username, cam.Password);
    }

    private static string Describe(string url, int cseq, string? authorization) =>
        $"DESCRIBE {url} RTSP/1.0\r\nCSeq: {cseq}\r\nAccept: application/sdp\r\nUser-Agent: VeltrixOS\r\n" +
        (authorization is null ? "" : $"Authorization: {authorization}\r\n") + "\r\n";

    private static async Task<(int Status, Dictionary<string, string> Headers)> SendAsync(NetworkStream s, string request, CancellationToken ct)
    {
        await s.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        var buffer = new byte[8192];
        var total = 0;
        string text;
        while (true)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) throw new IOException("Connection closed.");
            total += n;
            text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                var (status, headers) = ParseResponseHead(text[..end]);
                // Drain the SDP body so the next request on this connection starts clean.
                if (headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out var len))
                {
                    var have = total - (end + 4);
                    while (have < len && have >= 0)
                    {
                        var m = await s.ReadAsync(buffer, ct);
                        if (m == 0) break;
                        have += m;
                    }
                }
                return (status, headers);
            }
            if (total == buffer.Length) throw new IOException("Response header too large.");
        }
    }

    internal static (int Status, Dictionary<string, string> Headers) ParseResponseHead(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ', 3);
        var status = parts.Length > 1 && parts[0].StartsWith("RTSP/", StringComparison.Ordinal) && int.TryParse(parts[1], out var st) ? st : 0;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            // Several WWW-Authenticate headers: keep them all, newline separated.
            headers[key] = headers.TryGetValue(key, out var prev) ? prev + "\n" + value : value;
        }
        return (status, headers);
    }

    /// <summary>Authorization header for the strongest offered scheme (Digest MD5, else Basic), or null.</summary>
    public static string? Authorization(string challenges, string method, string uri, string user, string pass, string? cnonce = null)
    {
        var list = challenges.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var digest = list.FirstOrDefault(c => c.StartsWith("Digest", StringComparison.OrdinalIgnoreCase));
        if (digest is not null)
        {
            var p = ParseParams(digest[6..]);
            p.TryGetValue("realm", out var realm);
            p.TryGetValue("nonce", out var nonce);
            p.TryGetValue("opaque", out var opaque);
            p.TryGetValue("qop", out var qopOffer);
            var qop = qopOffer?.Split(',').Select(q => q.Trim()).FirstOrDefault(q => q == "auth");
            cnonce ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var response = DigestResponse(user, realm ?? "", pass, method, uri, nonce ?? "", qop, "00000001", cnonce);
            var sb = new StringBuilder($"Digest username=\"{user}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\"");
            if (qop is not null) sb.Append($", qop={qop}, nc=00000001, cnonce=\"{cnonce}\"");
            if (opaque is not null) sb.Append($", opaque=\"{opaque}\"");
            return sb.ToString();
        }
        if (list.Any(c => c.StartsWith("Basic", StringComparison.OrdinalIgnoreCase)))
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass));
        return null;
    }

    /// <summary>RFC 2617 digest response (MD5; with or without qop=auth).</summary>
    public static string DigestResponse(string user, string realm, string pass, string method, string uri, string nonce, string? qop, string nc, string cnonce)
    {
        static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));
        var ha1 = Md5($"{user}:{realm}:{pass}");
        var ha2 = Md5($"{method}:{uri}");
        return qop is null ? Md5($"{ha1}:{nonce}:{ha2}") : Md5($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
    }

    private static Dictionary<string, string> ParseParams(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i]))) i++;
            var eq = text.IndexOf('=', i);
            if (eq < 0) break;
            var key = text[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < text.Length && text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                if (close < 0) close = text.Length;
                value = text[(i + 1)..close];
                i = close + 1;
            }
            else
            {
                var comma = text.IndexOf(',', i);
                if (comma < 0) comma = text.Length;
                value = text[i..comma].Trim();
                i = comma;
            }
            result[key] = value;
        }
        return result;
    }
}
