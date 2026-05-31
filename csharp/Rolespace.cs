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
using System.Linq;
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

    /// <summary>Send text plus one or more embeds (rich cards). The text is optional — pass <c>""</c> for embeds-only.</summary>
    /// <example>
    /// <code>
    /// var card = new RolespaceEmbed()
    ///     .WithTitle("Patch 2.4")
    ///     .WithColor("#5f85f7")
    ///     .AddField("Author", "@lynn", inline: true);
    /// await rs.SendMessageAsync(serverId, channelId, "Heads up:", card);
    /// </code>
    /// </example>
    public async Task<RolespaceMessage> SendMessageAsync(long serverId, long channelId, string text, params RolespaceEmbed[] embeds)
        => await SendMessageAsync(serverId, channelId, text, embeds, default).ConfigureAwait(false);

    /// <summary>Cancellation-aware overload of the text+embeds variant.</summary>
    public async Task<RolespaceMessage> SendMessageAsync(long serverId, long channelId, string text, RolespaceEmbed[] embeds, CancellationToken ct)
    {
        object payload = embeds is { Length: > 0 }
            ? new { content = text, embeds }
            : new { content = text };
        return new(await PostAsync($"/servers/{serverId}/channels/{channelId}/messages", payload, ct).ConfigureAwait(false));
    }

    /// <summary>Send a single embed with no message text.</summary>
    public Task<RolespaceMessage> SendMessageAsync(long serverId, long channelId, RolespaceEmbed embed, CancellationToken ct = default)
        => SendMessageAsync(serverId, channelId, "", new[] { embed }, ct);

    /// <summary>Send a richer message — pass an anonymous object with content/embeds/components/replyToMessageId.</summary>
    public async Task<RolespaceMessage> SendMessageAsync(long serverId, long channelId, object payload, CancellationToken ct = default)
        => new(await PostAsync($"/servers/{serverId}/channels/{channelId}/messages",
            payload, ct).ConfigureAwait(false));

    /// <summary>List recent messages, oldest → newest. <paramref name="before"/> pages backwards.</summary>
    public async Task<List<RolespaceMessage>> ListMessagesAsync(long serverId, long channelId,
        int limit = 50, string? before = null, CancellationToken ct = default)
    {
        var url = $"/servers/{serverId}/channels/{channelId}/messages?limit={limit}"
                  + (before != null ? $"&before={Uri.EscapeDataString(before)}" : "");
        return await GetListAsync(url, el => new RolespaceMessage(el), ct).ConfigureAwait(false);
    }

    /// <summary>Fetch a single message by id.</summary>
    public async Task<RolespaceMessage> GetMessageAsync(long serverId, long channelId, string messageId, CancellationToken ct = default)
        => new(await GetAsync($"/servers/{serverId}/channels/{channelId}/messages/{messageId}", ct).ConfigureAwait(false));

    /// <summary>Edit the bot's own message. Returns the updated message.</summary>
    public async Task<RolespaceMessage> EditMessageAsync(long serverId, long channelId, string messageId, string newContent, CancellationToken ct = default)
        => new(await PatchAsync($"/servers/{serverId}/channels/{channelId}/messages/{messageId}",
            new { content = newContent }, ct).ConfigureAwait(false));

    /// <summary>Delete a message (own message OR any with ManageMessages).</summary>
    public Task<JsonElement> DeleteMessageAsync(long serverId, long channelId, string messageId, CancellationToken ct = default)
        => DeleteAsync($"/servers/{serverId}/channels/{channelId}/messages/{messageId}", ct);

    /// <summary>Pin a message. Requires ManageMessages.</summary>
    public Task<JsonElement> PinMessageAsync(long serverId, long channelId, string messageId, CancellationToken ct = default)
        => PutAsync($"/servers/{serverId}/channels/{channelId}/messages/{messageId}/pin", null, ct);

    /// <summary>Unpin a message. Requires ManageMessages.</summary>
    public Task<JsonElement> UnpinMessageAsync(long serverId, long channelId, string messageId, CancellationToken ct = default)
        => DeleteAsync($"/servers/{serverId}/channels/{channelId}/messages/{messageId}/pin", ct);

    /// <summary>React to a message. Emoji is automatically URL-encoded.</summary>
    public Task<JsonElement> AddReactionAsync(long serverId, long channelId, string messageId, string emoji, CancellationToken ct = default)
        => PutAsync($"/servers/{serverId}/channels/{channelId}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}", null, ct);

    /// <summary>Remove the bot's own reaction.</summary>
    public Task<JsonElement> RemoveReactionAsync(long serverId, long channelId, string messageId, string emoji, CancellationToken ct = default)
        => DeleteAsync($"/servers/{serverId}/channels/{channelId}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}", ct);

    // ---- Channel + category management ─────────────────────────────────────

    /// <summary>Create a channel. <paramref name="type"/> is one of: text, voice, announcement, forum, rules.</summary>
    public async Task<RolespaceChannel> CreateChannelAsync(long serverId, string name, string type = "text",
        long? categoryId = null, string? topic = null, bool isPrivate = false, CancellationToken ct = default)
        => new(await PostAsync($"/servers/{serverId}/channels",
            new { name, type, categoryId, topic, isPrivate }, ct).ConfigureAwait(false));

    /// <summary>Rename and/or change a channel's topic. Pass null for fields you don't want to touch.</summary>
    public Task<JsonElement> UpdateChannelAsync(long serverId, long channelId, string? name = null, string? topic = null, CancellationToken ct = default)
        => PatchAsync($"/servers/{serverId}/channels/{channelId}", new { name, topic }, ct);

    /// <summary>Delete a channel and its contents. Requires ManageChannels.</summary>
    public Task<JsonElement> DeleteChannelAsync(long serverId, long channelId, CancellationToken ct = default)
        => DeleteAsync($"/servers/{serverId}/channels/{channelId}", ct);

    /// <summary>Create a category. Requires ManageChannels.</summary>
    public Task<JsonElement> CreateCategoryAsync(long serverId, string name, CancellationToken ct = default)
        => PostAsync($"/servers/{serverId}/categories", new { name }, ct);

    /// <summary>Rename a category. Requires ManageChannels.</summary>
    public Task<JsonElement> UpdateCategoryAsync(long serverId, long categoryId, string name, CancellationToken ct = default)
        => PatchAsync($"/servers/{serverId}/categories/{categoryId}", new { name }, ct);

    /// <summary>Delete a category. With <paramref name="deleteChannels"/>=true, also deletes every channel inside it.</summary>
    public Task<JsonElement> DeleteCategoryAsync(long serverId, long categoryId, bool deleteChannels = false, CancellationToken ct = default)
        => DeleteAsync($"/servers/{serverId}/categories/{categoryId}?deleteChannels={(deleteChannels ? "true" : "false")}", ct);

    // ---- Member moderation ────────────────────────────────────────────────

    /// <summary>Kick a member. Requires the bot's KickMembers permission.</summary>
    public Task<JsonElement> KickMemberAsync(long serverId, long userId, string? reason = null, CancellationToken ct = default)
    {
        var url = $"/servers/{serverId}/members/{userId}";
        if (!string.IsNullOrEmpty(reason)) url += "?reason=" + Uri.EscapeDataString(reason);
        return DeleteAsync(url, ct);
    }

    /// <summary>Ban a member. Requires BanMembers.</summary>
    public Task<JsonElement> BanMemberAsync(long serverId, long userId, string? reason = null, CancellationToken ct = default)
        => PostAsync($"/servers/{serverId}/members/{userId}/ban", new { reason }, ct);

    /// <summary>Lift a ban.</summary>
    public Task<JsonElement> UnbanMemberAsync(long serverId, long userId, CancellationToken ct = default)
        => DeleteAsync($"/servers/{serverId}/members/{userId}/ban", ct);

    /// <summary>Set or clear a member's server nickname. Pass null/empty to clear.</summary>
    public Task<JsonElement> SetNicknameAsync(long serverId, long userId, string? nickname, CancellationToken ct = default)
        => PatchAsync($"/servers/{serverId}/members/{userId}/nickname", new { nickname }, ct);

    /// <summary>Assign a role to a member. Requires ManageRoles.</summary>
    public Task<JsonElement> AssignRoleAsync(long serverId, long userId, long roleId, CancellationToken ct = default)
        => PutAsync($"/servers/{serverId}/members/{userId}/roles/{roleId}", null, ct);

    /// <summary>Remove a role from a member.</summary>
    public Task<JsonElement> RemoveRoleAsync(long serverId, long userId, long roleId, CancellationToken ct = default)
        => DeleteAsync($"/servers/{serverId}/members/{userId}/roles/{roleId}", ct);

    // ---- Forum threads ────────────────────────────────────────────────────

    /// <summary>List threads in a forum channel.</summary>
    public Task<List<JsonElement>> ListThreadsAsync(long serverId, long channelId, CancellationToken ct = default)
        => GetListAsync($"/servers/{serverId}/channels/{channelId}/threads", el => el, ct);

    /// <summary>Fetch a thread + its posts.</summary>
    public Task<JsonElement> GetThreadAsync(long serverId, long channelId, long threadId, CancellationToken ct = default)
        => GetAsync($"/servers/{serverId}/channels/{channelId}/threads/{threadId}", ct);

    /// <summary>Start a new thread in a forum channel.</summary>
    public Task<JsonElement> CreateThreadAsync(long serverId, long channelId, string title, string content,
        IEnumerable<string>? tags = null, CancellationToken ct = default)
        => PostAsync($"/servers/{serverId}/channels/{channelId}/threads",
            new { title, content, tags = tags?.ToArray() }, ct);

    /// <summary>Reply in a thread.</summary>
    public Task<JsonElement> ReplyToThreadAsync(long serverId, long channelId, long threadId, string content,
        long? replyToPostId = null, CancellationToken ct = default)
        => PostAsync($"/servers/{serverId}/channels/{channelId}/threads/{threadId}/posts",
            new { content, replyToPostId }, ct);

    // ---- Streams (read) ───────────────────────────────────────────────────

    /// <summary>The bot's own channel status: live, viewers, title, game, HLS URL.</summary>
    public Task<JsonElement> MyStreamAsync(CancellationToken ct = default) => GetAsync("/streams/me", ct);

    /// <summary>The current live directory (public).</summary>
    public Task<List<JsonElement>> LiveStreamsAsync(CancellationToken ct = default)
        => GetListAsync("/streams/live", el => el, ct);

    /// <summary>Public live status for any account.</summary>
    public Task<JsonElement> StreamForAsync(long accountId, CancellationToken ct = default)
        => GetAsync($"/streams/{accountId}", ct);

    /// <summary>The bot's stream chat moderators. Owner-only.</summary>
    public Task<List<JsonElement>> StreamModeratorsAsync(CancellationToken ct = default)
        => GetListAsync("/streams/me/moderators", el => el, ct);

    /// <summary>The bot's stream chat bans + timeouts. Owner-only.</summary>
    public Task<List<JsonElement>> StreamBansAsync(CancellationToken ct = default)
        => GetListAsync("/streams/me/bans", el => el, ct);

    // ---- Stream moderation ────────────────────────────────────────────────

    /// <summary>Promote a chat moderator on the bot's channel.</summary>
    public Task<JsonElement> AddStreamModeratorAsync(long accountId, CancellationToken ct = default)
        => PostAsync("/streams/me/moderators", new { accountId }, ct);

    /// <summary>Demote a chat moderator.</summary>
    public Task<JsonElement> RemoveStreamModeratorAsync(long accountId, CancellationToken ct = default)
        => DeleteAsync($"/streams/me/moderators/{accountId}", ct);

    /// <summary>Permanently ban a chatter.</summary>
    public Task<JsonElement> BanStreamChatterAsync(long accountId, string? reason = null, CancellationToken ct = default)
        => PostAsync("/streams/me/bans", new { accountId, reason }, ct);

    /// <summary>Temporarily ban a chatter for <paramref name="durationSeconds"/>.</summary>
    public Task<JsonElement> TimeoutStreamChatterAsync(long accountId, int durationSeconds, string? reason = null, CancellationToken ct = default)
        => PostAsync("/streams/me/timeouts", new { accountId, durationSeconds, reason }, ct);

    /// <summary>Lift a ban or timeout.</summary>
    public Task<JsonElement> LiftStreamBanAsync(long accountId, CancellationToken ct = default)
        => DeleteAsync($"/streams/me/bans/{accountId}", ct);

    /// <summary>Set chat mode (subscribers only, URL allow).</summary>
    public Task<JsonElement> UpdateStreamChatSettingsAsync(bool allowUrls, bool subscribersOnly, CancellationToken ct = default)
        => PatchAsync("/streams/me/chat-settings", new { allowUrls, subscribersOnly }, ct);

    // ---- Webhooks (outgoing — event delivery) ─────────────────────────────

    /// <summary>List the bot's outgoing (event-delivery) webhooks.</summary>
    public Task<List<JsonElement>> ListOutgoingWebhooksAsync(CancellationToken ct = default)
        => GetListAsync("/webhooks/outgoing", el => el, ct);

    /// <summary>Register an outgoing webhook. The returned object includes a one-time <c>secret</c> — store it.</summary>
    /// <param name="targetType">"server" or "stream".</param>
    /// <param name="targetId">Server id (for "server") or the bot's own account id (for "stream").</param>
    /// <param name="url">Public HTTPS endpoint that will receive POSTs.</param>
    /// <param name="events">List of event names (e.g. "message.created", "stream.online").</param>
    public Task<JsonElement> CreateOutgoingWebhookAsync(string targetType, long targetId, string url,
        IEnumerable<string> events, CancellationToken ct = default)
        => PostAsync("/webhooks/outgoing", new { targetType, targetId, url, events = events.ToArray() }, ct);

    /// <summary>Delete an outgoing webhook.</summary>
    public Task<JsonElement> DeleteOutgoingWebhookAsync(long webhookId, CancellationToken ct = default)
        => DeleteAsync($"/webhooks/outgoing/{webhookId}", ct);

    // ---- Webhooks (incoming — post-to-channel URL) ────────────────────────

    /// <summary>List the bot's incoming webhooks.</summary>
    public Task<List<JsonElement>> ListIncomingWebhooksAsync(CancellationToken ct = default)
        => GetListAsync("/webhooks/incoming", el => el, ct);

    /// <summary>Create an incoming webhook bound to a channel. Response includes the one-time POST URL with its token.</summary>
    public Task<JsonElement> CreateIncomingWebhookAsync(long serverId, long channelId, string? name = null, CancellationToken ct = default)
        => PostAsync("/webhooks/incoming", new { serverId, channelId, name }, ct);

    /// <summary>Delete an incoming webhook.</summary>
    public Task<JsonElement> DeleteIncomingWebhookAsync(long webhookId, CancellationToken ct = default)
        => DeleteAsync($"/webhooks/incoming/{webhookId}", ct);

    /// <summary>
    /// Post to an incoming webhook URL — anonymous (no bot token needed; the URL token IS the auth).
    /// Static so you can call it without instantiating a <see cref="RolespaceClient"/>:
    /// <code>await RolespaceClient.PostIncomingWebhookAsync(url, new { content = "Deploy done!" });</code>
    /// </summary>
    public static async Task<bool> PostIncomingWebhookAsync(string webhookUrl, object payload, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var resp = await http.PostAsJsonAsync(webhookUrl, payload, JsonOpts, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Send a direct message to a user.</summary>
    public async Task<RolespaceMessage> SendDmAsync(long recipientId, string text, CancellationToken ct = default)
        => new(await PostAsync("/dm", new { recipientId, content = text }, ct).ConfigureAwait(false));

    /// <summary>Send a DM with embeds (rich cards). Text may be empty.</summary>
    public async Task<RolespaceMessage> SendDmAsync(long recipientId, string text, params RolespaceEmbed[] embeds)
    {
        object payload = embeds is { Length: > 0 }
            ? new { recipientId, content = text, embeds }
            : new { recipientId, content = text };
        return new(await PostAsync("/dm", payload).ConfigureAwait(false));
    }

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

    // ---- Listening for new messages in a channel ───────────────────────────
    /// <summary>
    /// Async stream that yields new <see cref="RolespaceMessage"/> objects as they appear
    /// in a channel. Wraps the polling loop, cursor bookkeeping, and graceful error backoff:
    /// <code>
    /// var me = await rs.MeAsync();
    /// await foreach (var msg in rs.WatchMessagesAsync(serverId, channelId, ownAccountId: me.Bot.Id))
    /// {
    ///     if (msg.Content.StartsWith("!ping"))
    ///         await rs.SendMessageAsync(serverId, channelId, "pong");
    /// }
    /// </code>
    /// For high-volume / production bots, prefer outgoing webhooks (push) over polling.
    /// Polling is fine for low-traffic channels, dev/testing, or environments where you
    /// can't expose a public HTTP receiver.
    /// </summary>
    /// <param name="serverId">Server containing the channel.</param>
    /// <param name="channelId">Channel to watch.</param>
    /// <param name="idleDelayMs">Wait between polls (default 2000).</param>
    /// <param name="batchSize">Max messages per poll (default 50).</param>
    /// <param name="since">Only yield messages newer than this. Defaults to "now" so existing history is skipped.</param>
    /// <param name="includeOwn">When false (default), messages posted by THIS bot are filtered out.</param>
    /// <param name="ownAccountId">Required when includeOwn is false — pass <c>(await rs.MeAsync()).Bot.Id</c>.</param>
    /// <param name="onError">Optional callback invoked on each polling failure. Default is silent.</param>
    /// <param name="ct">Cancellation token — use this to stop the iterator.</param>
    public async IAsyncEnumerable<RolespaceMessage> WatchMessagesAsync(
        long serverId, long channelId,
        int idleDelayMs = 2000,
        int batchSize = 50,
        DateTime? since = null,
        bool includeOwn = false,
        long? ownAccountId = null,
        Action<Exception>? onError = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Bootstrap the cursor so we skip everything that's already in the channel.
        DateTime lastSeen;
        if (since.HasValue)
        {
            lastSeen = since.Value.ToUniversalTime();
        }
        else
        {
            lastSeen = DateTime.UtcNow;
            try
            {
                var seed = await GetAsync($"/servers/{serverId}/channels/{channelId}/messages?limit=1", ct).ConfigureAwait(false);
                if (seed.ValueKind == JsonValueKind.Object
                    && seed.TryGetProperty("data", out var seedData)
                    && seedData.ValueKind == JsonValueKind.Array
                    && seedData.GetArrayLength() > 0)
                {
                    var lastEl = seedData[seedData.GetArrayLength() - 1];
                    if (lastEl.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                        && tsEl.TryGetDateTime(out var ts))
                    {
                        lastSeen = ts.ToUniversalTime();
                    }
                }
            }
            catch (Exception ex) { onError?.Invoke(ex); }
        }

        while (!ct.IsCancellationRequested)
        {
            JsonElement? page = null;
            try
            {
                page = await GetAsync($"/servers/{serverId}/channels/{channelId}/messages?limit={batchSize}", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                // Back off harder on transient failures so we don't hammer a flaky API.
                try { await Task.Delay(Math.Min(30000, idleDelayMs * 4), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                continue;
            }

            if (page is JsonElement p
                && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                // API returns oldest → newest within the page; iterate in order so we yield in order too.
                foreach (var m in data.EnumerateArray())
                {
                    if (!m.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String) continue;
                    if (!tsEl.TryGetDateTime(out var ts)) continue;
                    var tsUtc = ts.ToUniversalTime();
                    if (tsUtc <= lastSeen) continue;

                    if (!includeOwn && ownAccountId.HasValue
                        && m.TryGetProperty("author", out var author)
                        && author.ValueKind == JsonValueKind.Object
                        && author.TryGetProperty("id", out var authorId)
                        && authorId.ValueKind == JsonValueKind.Number
                        && authorId.GetInt64() == ownAccountId.Value)
                    {
                        lastSeen = tsUtc;
                        continue;
                    }

                    yield return new RolespaceMessage(m.Clone());
                    lastSeen = tsUtc;
                }
            }

            try { await Task.Delay(idleDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }

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
