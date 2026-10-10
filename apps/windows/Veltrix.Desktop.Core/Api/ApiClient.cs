using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Veltrix.Contracts;
using Veltrix.Contracts.Identity;

namespace Veltrix.Desktop.Core.Api;

/// <summary>Persists the session tokens (DPAPI-protected on Windows).</summary>
public interface ITokenStore
{
    string? AccessToken { get; }
    string? RefreshToken { get; }
    void Save(TokenResponse tokens);
    void Clear();
}

public interface IServerAddress
{
    string? ServerUrl { get; }
}

/// <summary>Problem-details error returned by the server.</summary>
public class ApiException(int status, string? code, string message, IDictionary<string, string[]>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    public IDictionary<string, string[]>? Errors { get; } = errors;
}

/// <summary>The server could not be reached (no network, DNS, TLS or timeout).</summary>
public sealed class ApiOfflineException(Exception inner) : Exception("The server is unreachable.", inner);

public sealed class LoginOutcome
{
    public TokenResponse? Tokens { get; init; }
    public bool MfaRequired { get; init; }
}

/// <summary>
/// Thin JSON client for the VELTRIX API: bearer authentication, single-flight refresh-token rotation on 401,
/// RFC 7807 error mapping and connectivity tracking.
/// </summary>
public sealed class ApiClient(HttpClient http, ITokenStore tokens, IServerAddress server)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _online = true;

    public Guid? DeviceId { get; set; }
    public bool IsOnline => _online;
    public event EventHandler<bool>? ConnectivityChanged;
    /// <summary>Raised when the refresh token is rejected: the user has to sign in again.</summary>
    public event EventHandler? SessionExpired;

    public Task<T> GetAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Get, path, null, ct);
    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Post, path, body, ct);
    public Task<T> PutAsync<T>(string path, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Put, path, body, ct);
    public Task PostAsync(string path, object? body, CancellationToken ct = default) => SendAsync<JsonElement?>(HttpMethod.Post, path, body, ct);
    public Task DeleteAsync(string path, CancellationToken ct = default) => SendAsync<JsonElement?>(HttpMethod.Delete, path, null, ct);

    public async Task<byte[]> GetBytesAsync(string path, CancellationToken ct = default)
    {
        using var res = await SendRawAsync(HttpMethod.Get, path, null, true, ct);
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<LoginOutcome> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var json = await SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/auth/login", request, ct, authenticate: false);
        if (json.TryGetProperty("mfaRequired", out var m) && m.ValueKind == JsonValueKind.True) return new LoginOutcome { MfaRequired = true };
        var t = json.Deserialize<TokenResponse>(Json) ?? throw new ApiException(500, null, "Empty login response.");
        tokens.Save(t);
        return new LoginOutcome { Tokens = t };
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var rt = tokens.RefreshToken;
        tokens.Clear();
        if (rt is null) return;
        try { await SendAsync<JsonElement?>(HttpMethod.Post, "/api/v1/auth/logout", new LogoutRequest(rt), ct, authenticate: false); }
        catch (Exception e) when (e is ApiException or ApiOfflineException) { /* the local session is gone either way */ }
    }

    /// <summary>Checks reachability with the anonymous version endpoint.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try { await SendAsync<JsonElement>(HttpMethod.Get, "/api/v1/version", null, ct, authenticate: false); return true; }
        catch (ApiOfflineException) { return false; }
        catch (ApiException) { return true; }
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct = default, bool authenticate = true)
    {
        using var res = await SendRawAsync(method, path, body, authenticate, ct);
        if (res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0) return default!;
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, bool authenticate, CancellationToken ct)
    {
        var res = await TransmitAsync(method, path, body, authenticate, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized && authenticate && tokens.RefreshToken is not null)
        {
            var sentWith = res.RequestMessage?.Headers.Authorization?.Parameter;
            res.Dispose();
            if (await TryRefreshAsync(sentWith, ct)) res = await TransmitAsync(method, path, body, authenticate, ct);
            else throw new ApiException(401, "auth.session_expired", "Your session has expired. Sign in again.");
        }
        if (!res.IsSuccessStatusCode)
        {
            using (res) throw await ToExceptionAsync(res, ct);
        }
        return res;
    }

    private async Task<HttpResponseMessage> TransmitAsync(HttpMethod method, string path, object? body, bool authenticate, CancellationToken ct)
    {
        var baseUrl = server.ServerUrl?.TrimEnd('/') ?? throw new ApiException(0, "client.no_server", "No server address is configured.");
        using var req = new HttpRequestMessage(method, baseUrl + path);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        if (authenticate && tokens.AccessToken is { } at) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", at);
        if (path.StartsWith("/api/v1/me/sessions", StringComparison.Ordinal) && tokens.RefreshToken is { } rt) req.Headers.Add("X-Refresh-Token", rt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            SetOnline(true);
            return res;
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            SetOnline(false);
            throw new ApiOfflineException(e);
        }
    }

    private async Task<bool> TryRefreshAsync(string? failedAccessToken, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Another caller already rotated the tokens while we waited.
            if (tokens.AccessToken is { } current && current != failedAccessToken) return true;
            if (tokens.RefreshToken is not { } rt) return false;
            using var res = await TransmitAsync(HttpMethod.Post, "/api/v1/auth/refresh", new RefreshRequest(rt), false, ct);
            if (!res.IsSuccessStatusCode)
            {
                if ((int)res.StatusCode is 400 or 401 or 403)
                {
                    tokens.Clear();
                    SessionExpired?.Invoke(this, EventArgs.Empty);
                }
                return false;
            }
            var t = await res.Content.ReadFromJsonAsync<TokenResponse>(Json, ct);
            if (t is null) return false;
            tokens.Save(t);
            return true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<ApiException> ToExceptionAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var status = (int)res.StatusCode;
        try
        {
            var p = await res.Content.ReadFromJsonAsync<ProblemResponse>(Json, ct);
            if (p is not null)
            {
                var message = p.Errors is { Count: > 0 } errs
                    ? string.Join(Environment.NewLine, errs.SelectMany(kv => kv.Value.Select(v => $"{kv.Key}: {v}")))
                    : p.Detail ?? p.Title;
                return new ApiException(status, p.Code, string.IsNullOrWhiteSpace(message) ? res.ReasonPhrase ?? "Error" : message, p.Errors);
            }
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { }
        return new ApiException(status, null, $"{status} {res.ReasonPhrase}");
    }

    private void SetOnline(bool online)
    {
        if (_online == online) return;
        _online = online;
        ConnectivityChanged?.Invoke(this, online);
    }

    public static string Query(params (string Key, object? Value)[] parts)
    {
        var items = parts.Where(p => p.Value is not null && p.Value.ToString() != "")
            .Select(p => $"{p.Key}={Uri.EscapeDataString(Format(p.Value!))}");
        var q = string.Join('&', items);
        return q.Length == 0 ? "" : "?" + q;
    }

    private static string Format(object value) => value switch
    {
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
