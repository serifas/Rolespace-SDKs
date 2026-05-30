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
//   Console.WriteLine($"Logged in as {me.Bot.Username}");
//
// What this SDK gives you that raw HttpClient doesn't:
//   - Strongly-typed responses: msg.Content instead of msg.GetProperty("content").GetString()
//   - Token is loaded from env by default (no hardcoded tokens in source)
//   - 429 rate-limit retries with exponential backoff + Retry-After
//   - IAsyncEnumerable<RolespaceInteraction> over interactions (no manual polling loop)
//   - Constant-time webhook signature verification (RolespaceClient.VerifyWebhook)
//   - TLS verification is enforced; can only be disabled with an explicit, scary opt-in
//   - ToString() never leaks the token
//
// Escape hatch: every typed model exposes .Raw — the original JsonElement — so any
// field that isn't modelled yet can still be reached without forking the SDK.

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
    private const string SdkVersion = "0.2.0";

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

    // ---- Typed convenience helpers ────────────────────────────────────────
    // These return strongly-typed wrapper classes so callers can write
    //   var me = await rs.MeAsync();
    //   Console.WriteLine(me.Bot.Username);
    // instead of digging through JsonElement.GetProperty(...) chains. Every wrapper
    // exposes .Raw if you need the underlying JsonElement (e.g. for an unmodelled field).

    /// <summary>The authenticated application + bot identity + owner + scopes.</summary>
    public async Task<RolespaceMe> MeAsync(CancellationToken ct = default)
        => new(await GetAsync("/me", ct).ConfigureAwait(false));

    /// <summary>All servers the bot has been added to (summary objects — no channels).</summary>
    public Task<List<RolespaceServer>> ServersAsync(CancellationToken ct = default)
        => GetListAsync("/servers", el => new RolespaceServer(el), ct);

    /// <summary>One server with its categories and visible channels.</summary>
    public async Task<RolespaceServer> ServerAsync(long id, CancellationToken ct = default)
        => new(await GetAsync($"/servers/{id}", ct).ConfigureAwait(false));

    /// <summary>Flat list of channels the bot can view in the server.</summary>
    public Task<List<RolespaceChannel>> ServerChannelsAsync(long id, CancellationToken ct = default)
        => GetListAsync($"/servers/{id}/channels", el => new RolespaceChannel(el), ct);

    /// <summary>All members of the server.</summary>
    public Task<List<RolespaceMember>> ServerMembersAsync(long id, CancellationToken ct = default)
        => GetListAsync($"/servers/{id}/members", el => new RolespaceMember(el), ct);

    /// <summary>One member of the server.</summary>
    public async Task<RolespaceMember> ServerMemberAsync(long serverId, long userId, CancellationToken ct = default)
        => new(await GetAsync($"/servers/{serverId}/members/{userId}", ct).ConfigureAwait(false));

    /// <summary>All roles in the server, highest position first.</summary>
    public Task<List<RolespaceRole>> ServerRolesAsync(long id, CancellationToken ct = default)
        => GetListAsync($"/servers/{id}/roles", el => new RolespaceRole(el), ct);

    /// <summary>Send a plain-text message. Returns the message including its server-assigned id.</summary>
    public async Task<RolespaceMessage> SendMessageAsync(long serverId, long channelId, string text, CancellationToken ct = default)
        => new(await PostAsync($"/servers/{serverId}/channels/{channelId}/messages",
            new { content = text }, ct).ConfigureAwait(false));

    /// <summary>Send a richer message — pass an anonymous object with content/embeds/components/replyToMessageId.</summary>
    public async Task<RolespaceMessage> SendMessageAsync(long serverId, long channelId, object payload, CancellationToken ct = default)
        => new(await PostAsync($"/servers/{serverId}/channels/{channelId}/messages",
            payload, ct).ConfigureAwait(false));

    /// <summary>Fetch a single message by id.</summary>
    public async Task<RolespaceMessage> GetMessageAsync(long serverId, long channelId, string messageId, CancellationToken ct = default)
        => new(await GetAsync($"/servers/{serverId}/channels/{channelId}/messages/{messageId}", ct).ConfigureAwait(false));

    /// <summary>Send a direct message to a user.</summary>
    public async Task<RolespaceMessage> SendDmAsync(long recipientId, string text, CancellationToken ct = default)
        => new(await PostAsync("/dm", new { recipientId, content = text }, ct).ConfigureAwait(false));

    // ---- Interaction polling ──────────────────────────────────────────────
    /// <summary>
    /// Async stream of interactions (button clicks, select choices, modal submits).
    /// Resolves the polling loop, backoff, and cursor for you:
    /// <code>
    /// await foreach (var ix in rs.InteractionsAsync())
    /// {
    ///     if (ix.CustomId == "book") await rs.RespondAsync(ix.Id, new { type = "message", content = "Booked!" });
    /// }
    /// </code>
    /// </summary>
    public async IAsyncEnumerable<RolespaceInteraction> InteractionsAsync(int idleDelayMs = 1000, [EnumeratorCancellation] CancellationToken ct = default)
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
                    yield return new RolespaceInteraction(ix.Clone());
                    yielded++;
                }
            }
            if (page.ValueKind == JsonValueKind.Object && page.TryGetProperty("lastId", out var lid) && lid.ValueKind == JsonValueKind.Number)
                after = lid.GetInt64();
            if (yielded == 0)
                await Task.Delay(idleDelayMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Respond to an interaction. <paramref name="reply"/> is an anonymous object:
    /// <c>new { type = "message", content = "hi", ephemeral = true }</c>.
    /// Response shape varies by reply type, so this stays raw JsonElement.</summary>
    public Task<JsonElement> RespondAsync(long interactionId, object reply, CancellationToken ct = default)
        => PostAsync($"/interactions/{interactionId}/callback", reply, ct);

    // ─── internal: unwrap "{ data: [...] }" list responses ──────────────────
    private async Task<List<T>> GetListAsync<T>(string path, Func<JsonElement, T> map, CancellationToken ct)
    {
        var resp = await GetAsync(path, ct).ConfigureAwait(false);
        var list = new List<T>();
        // Some endpoints return { data: [...] }; older ones returned a bare array. Handle both.
        JsonElement arr = default;
        if (resp.ValueKind == JsonValueKind.Array)
            arr = resp;
        else if (resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            arr = data;
        else
            return list;
        foreach (var item in arr.EnumerateArray())
            list.Add(map(item.Clone()));
        return list;
    }

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
