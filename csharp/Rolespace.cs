// Rolespace C# SDK
// =================
//
// A small client for the Rolespace bot API. Targets .NET 6+ (uses HttpClient,
// System.Text.Json, IAsyncEnumerable).
//
// Quick start:
//   using Rolespace.Sdk;
//   var rs = RolespaceClient.FromEnv();          // reads ROLESPACE_BOT_TOKEN
//   var me = await rs.MeAsync();
//   Console.WriteLine($"Logged in as {me.GetProperty("bot").GetProperty("username").GetString()}");
//
// What this SDK gives you that raw HttpClient doesn't:
//   - Token is loaded from env by default (no hardcoded tokens in source)
//   - 429 rate-limit retries with exponential backoff + Retry-After
//   - IAsyncEnumerable<JsonElement> over interactions (no manual polling loop)
//   - Constant-time webhook signature verification (RolespaceClient.VerifyWebhook)
//   - TLS verification is enforced; can only be disabled with an explicit, scary opt-in
//   - ToString() never leaks the token

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rolespace.Sdk;

public class RolespaceException : Exception
{
    public int Status { get; }
    public string Body { get; }
    public RolespaceException(string message, int status, string body) : base(message)
    {
        Status = status;
        Body = body;
    }
}

public sealed class RolespaceClient : IDisposable
{
    private const string DefaultBase = "https://rolespace.net";
    private const string SdkVersion = "0.1.0";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly int _maxRetries;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public RolespaceClient(string token, string? baseUrl = null, int maxRetries = 5, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("rsp_"))
            throw new ArgumentException("Rolespace: token is required and must start with \"rsp_\"", nameof(token));

        _baseUrl = (baseUrl ?? DefaultBase).TrimEnd('/');
        _maxRetries = maxRetries;

        if (httpClient == null)
        {
            _http = new HttpClient();
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }

        _http.BaseAddress ??= new Uri(_baseUrl + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"rolespace-dotnet/{SdkVersion}");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Build a client from environment vars: ROLESPACE_BOT_TOKEN (required) and ROLESPACE_API_BASE (optional).</summary>
    public static RolespaceClient FromEnv()
    {
        var token = Environment.GetEnvironmentVariable("ROLESPACE_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("RolespaceClient.FromEnv: set ROLESPACE_BOT_TOKEN in your environment");
        return new RolespaceClient(token, Environment.GetEnvironmentVariable("ROLESPACE_API_BASE"));
    }

    public override string ToString() => $"RolespaceClient(base={_baseUrl}, token=[redacted])";

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }

    // ---- HTTP primitives ----
    public async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var url = path.StartsWith("http") ? path : path.TrimStart('/').StartsWith("api/v1/") ? path.TrimStart('/') : "api/v1/" + path.TrimStart('/');

        int attempt = 0;
        while (true)
        {
            using var req = new HttpRequestMessage(method, url);
            if (body != null) req.Content = JsonContent.Create(body, options: JsonOpts);

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (res.StatusCode == (HttpStatusCode)429 && attempt < _maxRetries)
            {
                TimeSpan wait = res.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromMilliseconds(Math.Min(30000, 500 * Math.Pow(2, attempt)));
                await Task.Delay(wait, ct).ConfigureAwait(false);
                attempt++;
                continue;
            }

            if (!res.IsSuccessStatusCode)
            {
                string err = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new RolespaceException(
                    $"Rolespace API {(int)res.StatusCode} on {method.Method} {path}: {err[..Math.Min(300, err.Length)]}",
                    (int)res.StatusCode, err);
            }

            if (res.StatusCode == HttpStatusCode.NoContent) return default;
            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
    }

    public Task<JsonElement> GetAsync(string path, CancellationToken ct = default)               => RequestAsync(HttpMethod.Get,    path, null, ct);
    public Task<JsonElement> PostAsync(string path, object? body = null, CancellationToken ct = default)  => RequestAsync(HttpMethod.Post,   path, body ?? new { }, ct);
    public Task<JsonElement> PatchAsync(string path, object? body = null, CancellationToken ct = default) => RequestAsync(new HttpMethod("PATCH"), path, body ?? new { }, ct);
    public Task<JsonElement> PutAsync(string path, object? body = null, CancellationToken ct = default)   => RequestAsync(HttpMethod.Put,    path, body ?? new { }, ct);
    public Task<JsonElement> DeleteAsync(string path, CancellationToken ct = default)            => RequestAsync(HttpMethod.Delete, path, null, ct);

    // ---- Typed convenience helpers ----
    public Task<JsonElement> MeAsync(CancellationToken ct = default)                                  => GetAsync("/me", ct);
    public Task<JsonElement> ServersAsync(CancellationToken ct = default)                             => GetAsync("/servers", ct);
    public Task<JsonElement> ServerAsync(long id, CancellationToken ct = default)                     => GetAsync($"/servers/{id}", ct);
    public Task<JsonElement> ServerChannelsAsync(long id, CancellationToken ct = default)             => GetAsync($"/servers/{id}/channels", ct);
    public Task<JsonElement> ServerMembersAsync(long id, CancellationToken ct = default)              => GetAsync($"/servers/{id}/members", ct);

    public Task<JsonElement> SendMessageAsync(long serverId, long channelId, string text, CancellationToken ct = default)
        => PostAsync($"/servers/{serverId}/channels/{channelId}/messages", new { content = text }, ct);
    public Task<JsonElement> SendMessageAsync(long serverId, long channelId, object payload, CancellationToken ct = default)
        => PostAsync($"/servers/{serverId}/channels/{channelId}/messages", payload, ct);

    public Task<JsonElement> SendDmAsync(long recipientId, string text, CancellationToken ct = default)
        => PostAsync("/dm", new { recipientId, content = text }, ct);

    // ---- Interaction polling ----
    /// <summary>
    /// Async stream of interactions. Resolves the polling loop, backoff, and cursor for you:
    /// <code>await foreach (var ix in rs.InteractionsAsync()) { ... }</code>
    /// </summary>
    public async IAsyncEnumerable<JsonElement> InteractionsAsync(int idleDelayMs = 1000, [EnumeratorCancellation] CancellationToken ct = default)
    {
        long after = 0;
        while (!ct.IsCancellationRequested)
        {
            var page = await GetAsync($"/interactions?after={after}", ct).ConfigureAwait(false);
            int yielded = 0;
            if (page.ValueKind == JsonValueKind.Object && page.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var ix in data.EnumerateArray())
                {
                    yield return ix.Clone();
                    yielded++;
                }
            }
            if (page.ValueKind == JsonValueKind.Object && page.TryGetProperty("lastId", out var lid) && lid.ValueKind == JsonValueKind.Number)
                after = lid.GetInt64();
            if (yielded == 0)
                await Task.Delay(idleDelayMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Respond to an interaction. <paramref name="reply"/> is anonymous-object: <c>new { type = "message", content = "hi", ephemeral = true }</c>.</summary>
    public Task<JsonElement> RespondAsync(long interactionId, object reply, CancellationToken ct = default)
        => PostAsync($"/interactions/{interactionId}/callback", reply, ct);

    // ---- Webhook signature verification ----
    /// <summary>
    /// Verify an X-Rolespace-Signature header against a raw request body.
    /// IMPORTANT: pass the RAW request body bytes — if you parsed JSON first
    /// the byte order changed and the signature will never match.
    /// In ASP.NET Core, read with <c>HttpContext.Request.BodyReader</c> or
    /// <c>await reader.ReadToEndAsync()</c> on a fresh stream.
    /// </summary>
    public static bool VerifyWebhook(byte[] rawBody, string signatureHeader, string secret)
    {
        if (rawBody == null || rawBody.Length == 0 || string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(secret))
            return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var digest = hmac.ComputeHash(rawBody);
        var expected = "sha256=" + Convert.ToHexString(digest).ToLowerInvariant();
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(signatureHeader);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
