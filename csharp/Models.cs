// Rolespace C# SDK — typed models
// ================================
//
// Strongly-typed wrappers over the raw JsonElement responses so callers can write
//   msg.Content   instead of   msg.GetProperty("content").GetString()
//   msg.Author.DisplayName   instead of   msg.GetProperty("author").GetProperty("displayName").GetString()
//
// Each model holds the underlying JsonElement and exposes it via .Raw, so any field
// not yet modelled (or any new field the server adds before this SDK is updated)
// can still be reached without forking the SDK:
//   long fancy = msg.Raw.GetProperty("brandNewField").GetInt64();
//
// All accessors are defensive: missing/null/wrong-type fields return sensible defaults
// (empty string, 0, false, null) rather than throwing. That keeps a server-side rename
// from blowing up your bot — you'll just see a default instead of crashing mid-loop.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Rolespace.Sdk;

/// <summary>
/// Base for every typed wrapper. Holds the underlying JsonElement and provides
/// safe accessors so subclasses can pull fields without try/catch boilerplate.
/// </summary>
public abstract class RolespaceObject
{
    protected readonly JsonElement El;
    protected RolespaceObject(JsonElement el) { El = el; }

    /// <summary>The underlying JSON. Use for fields not yet modelled, or for debugging.</summary>
    public JsonElement Raw => El;

    public override string ToString() => El.ToString();

    // ── safe accessors used by subclasses ─────────────────────────────────

    protected string Str(string name) =>
        El.ValueKind == JsonValueKind.Object && El.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    protected string? StrOrNull(string name) =>
        El.ValueKind == JsonValueKind.Object && El.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    protected long Long(string name) =>
        El.ValueKind == JsonValueKind.Object && El.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : 0L;

    protected long? LongOrNull(string name) =>
        El.ValueKind == JsonValueKind.Object && El.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : null;

    protected int Int(string name) =>
        El.ValueKind == JsonValueKind.Object && El.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    protected bool Bool(string name) =>
        El.ValueKind == JsonValueKind.Object && El.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    protected DateTime? DateTimeOrNull(string name)
    {
        if (El.ValueKind != JsonValueKind.Object || !El.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        return v.TryGetDateTime(out var dt) ? dt : null;
    }

    protected JsonElement SubObject(string name) =>
        El.ValueKind == JsonValueKind.Object && El.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : default;

    protected JsonElement? SubObjectOrNull(string name) =>
        El.ValueKind == JsonValueKind.Object && El.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : null;

    protected List<T> SubArray<T>(string name, Func<JsonElement, T> map)
    {
        var list = new List<T>();
        if (El.ValueKind != JsonValueKind.Object || !El.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in arr.EnumerateArray())
            list.Add(map(item));
        return list;
    }

    protected List<long> SubLongArray(string name)
    {
        var list = new List<long>();
        if (El.ValueKind != JsonValueKind.Object || !El.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number) list.Add(item.GetInt64());
        return list;
    }

    protected List<string> SubStringArray(string name)
    {
        var list = new List<string>();
        if (El.ValueKind != JsonValueKind.Object || !El.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
        return list;
    }
}

// ═══════════════════════════════ /me ════════════════════════════════════

/// <summary>The authenticated application: bot identity + owner + scopes.</summary>
public sealed class RolespaceMe : RolespaceObject
{
    public RolespaceMe(JsonElement el) : base(el) { }

    /// <summary>The bot account — author of anything the bot does.</summary>
    public RolespaceUser Bot => new(SubObject("bot"));

    /// <summary>The human owner of the application, if visible.</summary>
    public RolespaceUser? Owner =>
        SubObjectOrNull("owner") is { } o ? new RolespaceUser(o) : null;

    /// <summary>Granted OAuth-style scopes (e.g. <c>messages.write</c>, <c>members.manage</c>).</summary>
    public List<string> Scopes
    {
        get
        {
            var app = SubObjectOrNull("application");
            if (app is { } a && a.TryGetProperty("scopes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in arr.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
                return list;
            }
            return new List<string>();
        }
    }

    /// <summary>Numeric id of the application registration.</summary>
    public long ApplicationId =>
        SubObjectOrNull("application") is { } a && a.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : 0;
}

// ═════════════════════════ Users / members ══════════════════════════════

/// <summary>A user-shaped object. Used both for stand-alone users (bot/owner in /me,
/// author in messages) and as the embedded core of a server member.</summary>
public class RolespaceUser : RolespaceObject
{
    public RolespaceUser(JsonElement el) : base(el) { }

    /// <summary>Numeric account id. Same id everywhere in the API.</summary>
    public long Id => Long("id") != 0 ? Long("id") : Long("userId");

    public string Username => Str("username");
    public string DisplayName => Str("displayName");
    public string? Nickname => StrOrNull("nickname");
    public string? AvatarUrl => StrOrNull("avatar") ?? StrOrNull("avatarUrl");
}

/// <summary>A member of a server: user fields plus server-scoped role assignments.</summary>
public sealed class RolespaceMember : RolespaceUser
{
    public RolespaceMember(JsonElement el) : base(el) { }

    /// <summary>True if this member owns the server.</summary>
    public bool IsOwner => Bool("isOwner");

    /// <summary>Role ids assigned to this member in this server.</summary>
    public List<long> RoleIds => SubLongArray("roleIds");
}

// ═════════════════════════════ Servers ══════════════════════════════════

/// <summary>A server (a.k.a. guild). Categories+channels are populated when fetched
/// via <see cref="RolespaceClient.ServerAsync"/>; lists from <see cref="RolespaceClient.ServersAsync"/>
/// give summary objects with empty Categories.</summary>
public sealed class RolespaceServer : RolespaceObject
{
    public RolespaceServer(JsonElement el) : base(el) { }

    public long Id => Long("id");
    public string Name => Str("name");
    public string? Description => StrOrNull("description");
    public string? IconUrl => StrOrNull("iconUrl");
    public string? BannerUrl => StrOrNull("bannerUrl");
    public bool IsPublic => Bool("isPublic");
    public long OwnerId => Long("ownerId");
    public int MemberCount => Int("memberCount");
    public DateTime? CreatedAt => DateTimeOrNull("createdAt");

    /// <summary>Categories with their channels. Empty in summary responses (list /servers);
    /// populated when fetched via /servers/{id}.</summary>
    public List<RolespaceCategory> Categories => SubArray("categories", el => new RolespaceCategory(el));

    /// <summary>Flatten the category tree into a single channel list.</summary>
    public IEnumerable<RolespaceChannel> AllChannels()
    {
        foreach (var cat in Categories)
            foreach (var ch in cat.Channels)
                yield return ch;
    }
}

public sealed class RolespaceCategory : RolespaceObject
{
    public RolespaceCategory(JsonElement el) : base(el) { }
    public long Id => Long("id");
    public string Name => Str("name");
    public int Position => Int("position");
    public List<RolespaceChannel> Channels => SubArray("channels", el => new RolespaceChannel(el));
}

public sealed class RolespaceChannel : RolespaceObject
{
    public RolespaceChannel(JsonElement el) : base(el) { }

    public long Id => Long("id");
    public long ServerId => Long("serverId");
    public long? CategoryId => LongOrNull("categoryId");
    public string? CategoryName => StrOrNull("categoryName");
    public string Name => Str("name");
    public string? Topic => StrOrNull("topic");
    /// <summary>Lowercase string: <c>text</c>, <c>voice</c>, <c>announcement</c>, <c>forum</c>, <c>rules</c>.</summary>
    public string Type => Str("type");
    public int Position => Int("position");
    public bool IsNsfw => Bool("isNsfw");
    public bool IsPrivate => Bool("isPrivate");
}

public sealed class RolespaceRole : RolespaceObject
{
    public RolespaceRole(JsonElement el) : base(el) { }
    public long Id => Long("id");
    public string Name => Str("name");
    public string? Color => StrOrNull("color");
    public int Position => Int("position");
    public bool IsEveryone => Bool("isEveryone");
    public bool IsDefault => Bool("isDefault");
    public RolespaceRolePermissions Permissions => new(SubObject("permissions"));
}

public sealed class RolespaceRolePermissions : RolespaceObject
{
    public RolespaceRolePermissions(JsonElement el) : base(el) { }
    public bool Administrator => Bool("administrator");
    public bool ManageServer => Bool("manageServer");
    public bool ManageRoles => Bool("manageRoles");
    public bool ManageChannels => Bool("manageChannels");
    public bool ManageMessages => Bool("manageMessages");
    public bool KickMembers => Bool("kickMembers");
    public bool BanMembers => Bool("banMembers");
    public bool SendMessages => Bool("sendMessages");
    public bool ViewChannels => Bool("viewChannels");
    public bool AddReactions => Bool("addReactions");
}

// ═══════════════════════════ Messages ═══════════════════════════════════

public sealed class RolespaceMessage : RolespaceObject
{
    public RolespaceMessage(JsonElement el) : base(el) { }

    /// <summary>String id (GUID). Message ids are strings, NOT numeric like channel/user ids.</summary>
    public string Id => Str("id");
    public long ChannelId => Long("channelId");
    public long ServerId => Long("serverId");

    /// <summary>Message body text. Empty string when the message is embeds/components only.</summary>
    public string Content => Str("content");

    public DateTime? Timestamp => DateTimeOrNull("timestamp");
    public DateTime? EditedAt => DateTimeOrNull("editedAt");

    public bool IsPinned => Bool("isPinned");

    /// <summary>Id of the message this is a reply to, if any.</summary>
    public string? ReplyToMessageId => StrOrNull("replyToMessageId");

    /// <summary>The author of the message (user + display fields). Always present on send-response
    /// for messages POSTed by your bot, may be a stripped version for older messages.</summary>
    public RolespaceUser Author => new(SubObject("author"));

    public List<RolespaceReaction> Reactions => SubArray("reactions", el => new RolespaceReaction(el));
    public List<RolespaceAttachment> Attachments => SubArray("attachments", el => new RolespaceAttachment(el));
}

public sealed class RolespaceReaction : RolespaceObject
{
    public RolespaceReaction(JsonElement el) : base(el) { }
    public string Emoji => Str("emoji");
    public int Count => Int("count");
    public List<long> UserIds => SubLongArray("userIds");
}

public sealed class RolespaceAttachment : RolespaceObject
{
    public RolespaceAttachment(JsonElement el) : base(el) { }
    public long Id => Long("id");
    public string FileName => Str("fileName");
    public string Url => Str("url");
    public long FileSize => Long("fileSize");
    public string? ContentType => StrOrNull("contentType");
    public int? Width => El.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : null;
    public int? Height => El.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : null;
}

// ═══════════════════════ Interactions ═══════════════════════════════════

/// <summary>A user interaction with one of your bot's components — a button click,
/// select-menu choice, or modal submission. Yielded by the
/// <see cref="RolespaceClient.InteractionsAsync"/> async stream.</summary>
public sealed class RolespaceInteraction : RolespaceObject
{
    public RolespaceInteraction(JsonElement el) : base(el) { }

    /// <summary>Numeric interaction id. Pass this to <see cref="RolespaceClient.RespondAsync"/>.</summary>
    public long Id => Long("id");

    /// <summary>One of <c>button</c>, <c>select</c>, <c>modal_submit</c>.</summary>
    public string Type => Str("type");

    /// <summary>The <c>customId</c> the bot set on the component. Use this to dispatch.</summary>
    public string CustomId => Str("customId");

    public long ServerId => Long("serverId");
    public long ChannelId => Long("channelId");

    /// <summary>String id of the source message (the panel the user clicked on).</summary>
    public string? MessageId => StrOrNull("messageId");

    /// <summary>Who clicked / submitted.</summary>
    public RolespaceUser User => new(SubObject("user"));

    /// <summary>Extra payload — for <c>select</c> this carries the chosen option(s);
    /// for <c>modal_submit</c> it carries <c>fields</c>. Shape varies by Type.</summary>
    public JsonElement? Data => SubObjectOrNull("data");

    public DateTime? CreatedAt => DateTimeOrNull("createdAt");
}

// ════════════════════════ Embeds (rich cards) ═══════════════════════════
//
// Embeds are OUTBOUND objects — you build them and pass them to SendMessageAsync.
// They serialize to JSON via the SDK's camelCase + ignore-null serializer, so a
// property left at its default just isn't sent. That's why every field is nullable
// (or a collection that lazy-allocates).
//
// Two styles work — pick whichever reads better:
//
//   // Object-initializer style:
//   var card = new RolespaceEmbed {
//       Title = "Patch 2.4",
//       Description = "Snappier sidebar.",
//       Color = "#5f85f7",
//   };
//   card.AddField("Author", "@lynn", inline: true);
//
//   // Fluent builder style:
//   var card = new RolespaceEmbed()
//       .WithTitle("Patch 2.4")
//       .WithDescription("Snappier sidebar.")
//       .WithColor("#5f85f7")
//       .AddField("Author", "@lynn", inline: true);
//
//   await rs.SendMessageAsync(serverId, channelId, "Heads up:", card);

/// <summary>A rich card you can attach to a message — title/description/fields/image/etc.
/// Pass one or more to <see cref="RolespaceClient.SendMessageAsync(long, long, string, RolespaceEmbed[])"/>.</summary>
public sealed class RolespaceEmbed
{
    public string? Title { get; set; }
    /// <summary>Optional URL the title hyperlinks to.</summary>
    public string? Url { get; set; }
    public string? Description { get; set; }
    /// <summary>Hex string like <c>"#5f85f7"</c>.</summary>
    public string? Color { get; set; }
    /// <summary>Main image URL.</summary>
    public string? Image { get; set; }
    /// <summary>Sensitivity flag for the main image — one of <c>"nsfw"</c>, <c>"triggering"</c>,
    /// <c>"spoiler"</c>. Flagged images render blurred behind a click-to-reveal cover.</summary>
    public string? ImageFlag { get; set; }
    /// <summary>Small thumbnail URL shown in the top-right of the card.</summary>
    public string? Thumbnail { get; set; }
    public RolespaceEmbedAuthor? Author { get; set; }
    /// <summary>Footer text (plain string).</summary>
    public string? Footer { get; set; }
    /// <summary>Inline name/value fields. Null until the first AddField call.</summary>
    public List<RolespaceEmbedField>? Fields { get; set; }
    /// <summary>Extra image strip rendered below the main image (up to 24). Null until first add.</summary>
    public List<RolespaceEmbedGalleryItem>? Gallery { get; set; }

    // ── fluent setters ────────────────────────────────────────────────────
    public RolespaceEmbed WithTitle(string title)               { Title = title; return this; }
    public RolespaceEmbed WithUrl(string url)                   { Url = url; return this; }
    public RolespaceEmbed WithDescription(string description)   { Description = description; return this; }
    public RolespaceEmbed WithColor(string hexColor)            { Color = hexColor; return this; }
    public RolespaceEmbed WithImage(string url, string? flag = null) { Image = url; ImageFlag = flag; return this; }
    public RolespaceEmbed WithThumbnail(string url)             { Thumbnail = url; return this; }
    public RolespaceEmbed WithAuthor(string name, string? iconUrl = null)
    {
        Author = new RolespaceEmbedAuthor { Name = name, IconUrl = iconUrl };
        return this;
    }
    public RolespaceEmbed WithFooter(string footer)             { Footer = footer; return this; }

    /// <summary>Append an inline name/value field. Up to 25 fields per embed.</summary>
    public RolespaceEmbed AddField(string name, string value, bool inline = false)
    {
        (Fields ??= new List<RolespaceEmbedField>()).Add(new RolespaceEmbedField
        {
            Name = name, Value = value, Inline = inline
        });
        return this;
    }

    /// <summary>Append a gallery image. Up to 24 per embed; the strip renders below the main image.</summary>
    public RolespaceEmbed AddGalleryImage(string url, string? flag = null)
    {
        (Gallery ??= new List<RolespaceEmbedGalleryItem>()).Add(new RolespaceEmbedGalleryItem
        {
            Url = url, Flag = flag
        });
        return this;
    }
}

public sealed class RolespaceEmbedField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Inline { get; set; }
}

public sealed class RolespaceEmbedAuthor
{
    public string Name { get; set; } = "";
    /// <summary>Small avatar/icon URL shown next to the author name.</summary>
    public string? IconUrl { get; set; }
}

public sealed class RolespaceEmbedGalleryItem
{
    public string Url { get; set; } = "";
    /// <summary>One of <c>"nsfw"</c>, <c>"triggering"</c>, <c>"spoiler"</c>.</summary>
    public string? Flag { get; set; }
}
