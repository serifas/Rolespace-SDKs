/**
 * Rolespace Node.js SDK
 *
 * A tiny, dependency-free client for the Rolespace bot API.
 * Requires Node 18+ (uses built-in fetch + crypto).
 *
 * Quick start:
 *   const { Rolespace, RolespaceEmbed } = require('rolespace');
 *   const rs = Rolespace.fromEnv();              // reads ROLESPACE_BOT_TOKEN
 *   const me = await rs.me();
 *   console.log(`Logged in as ${me.bot.username}`);
 *
 *   const msg = await rs.sendMessage(serverId, channelId, 'Hello!');
 *   console.log(msg.id, msg.content);
 *
 *   const card = new RolespaceEmbed()
 *       .withTitle('Patch 2.4')
 *       .withColor('#5f85f7')
 *       .addField('Author', '@lynn', true);
 *   await rs.sendMessage(serverId, channelId, 'Heads up:', card);
 *
 * What this SDK gives you that raw fetch doesn't:
 *   - Strongly-typed wrapper classes: msg.content instead of msg.content
 *     (yes, JS is loose anyway — but you get IntelliSense in TypeScript via JSDoc,
 *     plus the wrapper protects from missing fields throwing.)
 *   - RolespaceEmbed builder so you don't hand-shape JSON for rich cards
 *   - Token is loaded from env by default (no hardcoded tokens in source)
 *   - 429 rate-limit retries with exponential backoff + Retry-After
 *   - Async iterator over interactions (no manual polling loop)
 *   - Constant-time webhook signature verification (Rolespace.verifyWebhook)
 *   - TLS verification is enforced; can only be disabled with an explicit, scary opt-in
 *   - Authorization header is never logged
 *
 * Escape hatch: every typed wrapper exposes `.raw` — the underlying parsed JSON
 * object — so any field the server adds before this SDK is updated still works:
 *   const newField = msg.raw.brandNewField;
 */
'use strict';

const crypto = require('crypto');

const DEFAULT_BASE = 'https://rolespace.net';
const SDK_VERSION = '0.2.0';

class RolespaceError extends Error {
    constructor(message, status, body) {
        super(message);
        this.name = 'RolespaceError';
        this.status = status;
        this.body = body;
    }
}

// ═══════════════════ Response wrapper base class ═══════════════════════
// All typed response wrappers extend this. Subclasses just declare getters
// over `this._raw` for the fields they care about. Missing fields return
// undefined (or sensible defaults from coerce helpers) instead of throwing.

class RolespaceObject {
    constructor(raw) {
        // Store the underlying parsed JSON so callers can always reach unmodelled fields.
        Object.defineProperty(this, '_raw', { value: raw || {}, enumerable: false });
    }
    /** The underlying parsed JSON. Use for fields not yet modelled. */
    get raw() { return this._raw; }

    /** Sensible toJSON so JSON.stringify(wrapper) gives back the original shape. */
    toJSON() { return this._raw; }
}

// ═══════════════════════════ /me ═══════════════════════════════════════

class RolespaceMe extends RolespaceObject {
    /** The bot account — author of anything the bot does. */
    get bot() { return new RolespaceUser(this._raw.bot || {}); }
    /** The human owner, if visible. */
    get owner() { return this._raw.owner ? new RolespaceUser(this._raw.owner) : null; }
    /** Granted OAuth-style scopes. */
    get scopes() { return (this._raw.application && this._raw.application.scopes) || []; }
    /** Numeric id of the application registration. */
    get applicationId() { return (this._raw.application && this._raw.application.id) || 0; }
}

// ═══════════════════════ Users / members ═══════════════════════════════

class RolespaceUser extends RolespaceObject {
    /** Numeric account id. Same id everywhere in the API. */
    get id() { return this._raw.id ?? this._raw.userId ?? 0; }
    get username() { return this._raw.username || ''; }
    get displayName() { return this._raw.displayName || ''; }
    get nickname() { return this._raw.nickname || null; }
    get avatarUrl() { return this._raw.avatar || this._raw.avatarUrl || null; }
}

class RolespaceMember extends RolespaceUser {
    /** True if this member owns the server. */
    get isOwner() { return !!this._raw.isOwner; }
    /** Role ids assigned to this member in this server. */
    get roleIds() { return this._raw.roleIds || []; }
}

// ═══════════════════════════ Servers ═══════════════════════════════════

class RolespaceServer extends RolespaceObject {
    get id() { return this._raw.id || 0; }
    get name() { return this._raw.name || ''; }
    get description() { return this._raw.description || null; }
    get iconUrl() { return this._raw.iconUrl || null; }
    get bannerUrl() { return this._raw.bannerUrl || null; }
    get isPublic() { return !!this._raw.isPublic; }
    get ownerId() { return this._raw.ownerId || 0; }
    get memberCount() { return this._raw.memberCount || 0; }
    get createdAt() { return this._raw.createdAt || null; }
    get categories() { return (this._raw.categories || []).map(c => new RolespaceCategory(c)); }
    /** Flatten the category tree into a single channel list. */
    allChannels() {
        const out = [];
        for (const cat of this.categories) for (const ch of cat.channels) out.push(ch);
        return out;
    }
}

class RolespaceCategory extends RolespaceObject {
    get id() { return this._raw.id || 0; }
    get name() { return this._raw.name || ''; }
    get position() { return this._raw.position || 0; }
    get channels() { return (this._raw.channels || []).map(c => new RolespaceChannel(c)); }
}

class RolespaceChannel extends RolespaceObject {
    get id() { return this._raw.id || 0; }
    get serverId() { return this._raw.serverId || 0; }
    get categoryId() { return this._raw.categoryId || null; }
    get categoryName() { return this._raw.categoryName || null; }
    get name() { return this._raw.name || ''; }
    get topic() { return this._raw.topic || null; }
    /** Lowercase string: text, voice, announcement, forum, rules. */
    get type() { return this._raw.type || ''; }
    get position() { return this._raw.position || 0; }
    get isNsfw() { return !!this._raw.isNsfw; }
    get isPrivate() { return !!this._raw.isPrivate; }
}

class RolespaceRole extends RolespaceObject {
    get id() { return this._raw.id || 0; }
    get name() { return this._raw.name || ''; }
    get color() { return this._raw.color || null; }
    get position() { return this._raw.position || 0; }
    get isEveryone() { return !!this._raw.isEveryone; }
    get isDefault() { return !!this._raw.isDefault; }
    get permissions() { return this._raw.permissions || {}; }
}

// ═══════════════════════════ Messages ══════════════════════════════════

class RolespaceMessage extends RolespaceObject {
    /** String id (GUID). Message ids are strings, NOT numeric. */
    get id() { return this._raw.id || ''; }
    get channelId() { return this._raw.channelId || 0; }
    get serverId() { return this._raw.serverId || 0; }
    get content() { return this._raw.content || ''; }
    get timestamp() { return this._raw.timestamp || null; }
    get editedAt() { return this._raw.editedAt || null; }
    get isPinned() { return !!this._raw.isPinned; }
    get replyToMessageId() { return this._raw.replyToMessageId || null; }
    get author() { return new RolespaceUser(this._raw.author || {}); }
    get reactions() { return this._raw.reactions || []; }
    get attachments() { return this._raw.attachments || []; }
}

// ═══════════════════════ Interactions ══════════════════════════════════

class RolespaceInteraction extends RolespaceObject {
    /** Numeric interaction id. Pass to client.respond(...). */
    get id() { return this._raw.id || 0; }
    /** One of 'button', 'select', 'modal_submit'. */
    get type() { return this._raw.type || ''; }
    /** The customId the bot set on the component. Use this to dispatch. */
    get customId() { return this._raw.customId || ''; }
    get serverId() { return this._raw.serverId || 0; }
    get channelId() { return this._raw.channelId || 0; }
    /** String id of the source message. */
    get messageId() { return this._raw.messageId || null; }
    /** Who clicked / submitted. */
    get user() { return new RolespaceUser(this._raw.user || {}); }
    /** Extra payload — shape varies by type. */
    get data() { return this._raw.data || null; }
    get createdAt() { return this._raw.createdAt || null; }
}

// ════════════════════════ Embeds (rich cards) ══════════════════════════
//
// Outbound builder. Two styles work — pick whichever reads better:
//
//   // Plain-object style:
//   const card = new RolespaceEmbed({ title: 'Hi', color: '#5f85f7' });
//   card.addField('Author', '@lynn', true);
//
//   // Fluent builder style:
//   const card = new RolespaceEmbed()
//       .withTitle('Hi')
//       .withColor('#5f85f7')
//       .addField('Author', '@lynn', true);
//
// Pass to sendMessage as the last (or only) argument.

class RolespaceEmbed {
    constructor(initial) {
        // Copy whatever the caller passed in; everything optional.
        Object.assign(this, initial || {});
    }

    withTitle(title)       { this.title = title; return this; }
    withUrl(url)           { this.url = url; return this; }
    withDescription(desc)  { this.description = desc; return this; }
    /** Hex string like "#5f85f7". */
    withColor(hex)         { this.color = hex; return this; }
    /** flag: optional "nsfw" | "triggering" | "spoiler" — render blurred behind a click-to-reveal cover. */
    withImage(url, flag)   { this.image = url; if (flag) this.imageFlag = flag; return this; }
    withThumbnail(url)     { this.thumbnail = url; return this; }
    withAuthor(name, iconUrl) {
        this.author = iconUrl ? { name, iconUrl } : { name };
        return this;
    }
    withFooter(text)       { this.footer = text; return this; }

    /** Append an inline name/value field. Up to 25 per embed. */
    addField(name, value, inline = false) {
        (this.fields ||= []).push({ name, value, inline });
        return this;
    }

    /** Append a gallery image. Up to 24 per embed. */
    addGalleryImage(url, flag) {
        (this.gallery ||= []).push(flag ? { url, flag } : url);
        return this;
    }

    /** Used by JSON.stringify — strips internal stuff. (None here, but kept for future.) */
    toJSON() {
        const out = {};
        for (const k of Object.keys(this)) {
            if (this[k] !== undefined && this[k] !== null) out[k] = this[k];
        }
        return out;
    }
}

// ════════════════════════ Main client ══════════════════════════════════

class Rolespace {
    /**
     * @param {object} opts
     * @param {string} opts.token         - Bot token (rsp_*). Required.
     * @param {string} [opts.baseUrl]     - API base URL (default: https://rolespace.net).
     * @param {number} [opts.maxRetries]  - Max 429 retries before giving up (default: 5).
     */
    constructor(opts) {
        if (!opts || typeof opts.token !== 'string' || !opts.token.startsWith('rsp_')) {
            throw new Error('Rolespace: token is required and must start with "rsp_"');
        }
        this._token = opts.token;
        this._baseUrl = (opts.baseUrl || DEFAULT_BASE).replace(/\/+$/, '');
        this._maxRetries = Number.isInteger(opts.maxRetries) ? opts.maxRetries : 5;
    }

    /** Build a client from env vars. Reads ROLESPACE_BOT_TOKEN and optional ROLESPACE_API_BASE. */
    static fromEnv() {
        const token = process.env.ROLESPACE_BOT_TOKEN;
        if (!token) throw new Error('Rolespace.fromEnv: set ROLESPACE_BOT_TOKEN in your environment');
        return new Rolespace({ token, baseUrl: process.env.ROLESPACE_API_BASE });
    }

    // ---- HTTP primitives ─────────────────────────────────────────────────
    async request(method, path, body) {
        const url = path.startsWith('http') ? path : this._baseUrl + (path.startsWith('/') ? path : '/api/v1/' + path);
        const headers = {
            'Authorization': 'Bearer ' + this._token,
            'Accept': 'application/json',
            'User-Agent': `rolespace-node/${SDK_VERSION}`,
        };
        const init = { method, headers };
        if (body !== undefined) {
            headers['Content-Type'] = 'application/json';
            init.body = typeof body === 'string' ? body : JSON.stringify(body);
        }

        let attempt = 0;
        while (true) {
            const res = await fetch(url, init);
            if (res.status === 429 && attempt < this._maxRetries) {
                const ra = parseFloat(res.headers.get('retry-after') || '0');
                const waitMs = ra > 0 ? ra * 1000 : Math.min(30000, 500 * Math.pow(2, attempt));
                await new Promise(r => setTimeout(r, waitMs));
                attempt++;
                continue;
            }
            if (!res.ok) {
                let text;
                try { text = await res.text(); } catch { text = ''; }
                throw new RolespaceError(
                    `Rolespace API ${res.status} on ${method} ${path}: ${text.slice(0, 300)}`,
                    res.status, text
                );
            }
            if (res.status === 204) return null;
            const ct = res.headers.get('content-type') || '';
            return ct.includes('application/json') ? res.json() : res.text();
        }
    }
    get(path)          { return this.request('GET',    path); }
    post(path, body)   { return this.request('POST',   path, body ?? {}); }
    patch(path, body)  { return this.request('PATCH',  path, body ?? {}); }
    put(path, body)    { return this.request('PUT',    path, body ?? {}); }
    del(path)          { return this.request('DELETE', path); }

    // ---- Typed convenience helpers ───────────────────────────────────────
    // Each wraps the raw response in a typed class so callers get autocompleted
    // accessors (msg.content) instead of raw dict access (msg.content but also
    // msg.weirdMisspelling that silently returns undefined).

    /** The authenticated application + bot identity + owner + scopes. */
    async me() {
        return new RolespaceMe(await this.get('/me'));
    }

    /** All servers the bot has been added to (summary objects). */
    async servers() {
        const resp = await this.get('/servers');
        return _unwrapList(resp).map(s => new RolespaceServer(s));
    }

    /** One server with its categories and visible channels. */
    async server(id) {
        return new RolespaceServer(await this.get(`/servers/${id}`));
    }

    /** Flat list of channels the bot can view in the server. */
    async serverChannels(id) {
        const resp = await this.get(`/servers/${id}/channels`);
        return _unwrapList(resp).map(c => new RolespaceChannel(c));
    }

    /** All members of the server. */
    async serverMembers(id) {
        const resp = await this.get(`/servers/${id}/members`);
        return _unwrapList(resp).map(m => new RolespaceMember(m));
    }

    /** One member of the server. */
    async serverMember(serverId, userId) {
        return new RolespaceMember(await this.get(`/servers/${serverId}/members/${userId}`));
    }

    /** All roles in the server, highest position first. */
    async serverRoles(id) {
        const resp = await this.get(`/servers/${id}/roles`);
        return _unwrapList(resp).map(r => new RolespaceRole(r));
    }

    /**
     * Send a message.
     *
     * Signatures:
     *   sendMessage(serverId, channelId, "plain text")
     *   sendMessage(serverId, channelId, "text", embed)              // RolespaceEmbed
     *   sendMessage(serverId, channelId, "text", embed1, embed2)     // multiple embeds
     *   sendMessage(serverId, channelId, embed)                      // embed only, no text
     *   sendMessage(serverId, channelId, { content, embeds, components, replyToMessageId })
     *
     * Returns a RolespaceMessage.
     */
    async sendMessage(serverId, channelId, ...rest) {
        const payload = _buildMessagePayload(rest);
        return new RolespaceMessage(await this.post(`/servers/${serverId}/channels/${channelId}/messages`, payload));
    }

    /** Fetch a single message by id. */
    async getMessage(serverId, channelId, messageId) {
        return new RolespaceMessage(await this.get(`/servers/${serverId}/channels/${channelId}/messages/${messageId}`));
    }

    /** Recent messages, oldest → newest. Pass `before` (a message id) to page backwards. */
    async listMessages(serverId, channelId, opts) {
        opts = opts || {};
        const limit = opts.limit || 50;
        let url = `/servers/${serverId}/channels/${channelId}/messages?limit=${limit}`;
        if (opts.before) url += `&before=${encodeURIComponent(opts.before)}`;
        const resp = await this.get(url);
        return _unwrapList(resp).map(m => new RolespaceMessage(m));
    }

    /** Edit the bot's own message. Returns the updated message. */
    async editMessage(serverId, channelId, messageId, newContent) {
        return new RolespaceMessage(await this.patch(
            `/servers/${serverId}/channels/${channelId}/messages/${messageId}`,
            { content: newContent }));
    }

    /** Delete a message (own message OR any with ManageMessages). */
    deleteMessage(serverId, channelId, messageId) {
        return this.del(`/servers/${serverId}/channels/${channelId}/messages/${messageId}`);
    }

    /** Pin a message. Requires ManageMessages. */
    pinMessage(serverId, channelId, messageId) {
        return this.put(`/servers/${serverId}/channels/${channelId}/messages/${messageId}/pin`);
    }

    /** Unpin a message. Requires ManageMessages. */
    unpinMessage(serverId, channelId, messageId) {
        return this.del(`/servers/${serverId}/channels/${channelId}/messages/${messageId}/pin`);
    }

    /** React to a message. Emoji is URL-encoded automatically. */
    addReaction(serverId, channelId, messageId, emoji) {
        return this.put(`/servers/${serverId}/channels/${channelId}/messages/${messageId}/reactions/${encodeURIComponent(emoji)}`);
    }

    /** Remove the bot's own reaction. */
    removeReaction(serverId, channelId, messageId, emoji) {
        return this.del(`/servers/${serverId}/channels/${channelId}/messages/${messageId}/reactions/${encodeURIComponent(emoji)}`);
    }

    // ---- Channel + category management ─────────────────────────────────────

    /**
     * Create a channel.
     * @param {object} opts
     * @param {string} opts.type           text | voice | announcement | forum | rules (default "text")
     * @param {number} [opts.categoryId]
     * @param {string} [opts.topic]
     * @param {boolean} [opts.isPrivate]
     */
    async createChannel(serverId, name, opts) {
        opts = opts || {};
        return new RolespaceChannel(await this.post(`/servers/${serverId}/channels`, {
            name,
            type: opts.type || 'text',
            categoryId: opts.categoryId,
            topic: opts.topic,
            isPrivate: !!opts.isPrivate,
        }));
    }

    /** Rename and/or change a channel's topic. Pass null/undefined for fields to leave alone. */
    updateChannel(serverId, channelId, opts) {
        opts = opts || {};
        return this.patch(`/servers/${serverId}/channels/${channelId}`, {
            name: opts.name, topic: opts.topic,
        });
    }

    /** Delete a channel and its contents. Requires ManageChannels. */
    deleteChannel(serverId, channelId) {
        return this.del(`/servers/${serverId}/channels/${channelId}`);
    }

    /** Create a category. Requires ManageChannels. */
    createCategory(serverId, name) {
        return this.post(`/servers/${serverId}/categories`, { name });
    }

    /** Rename a category. */
    updateCategory(serverId, categoryId, name) {
        return this.patch(`/servers/${serverId}/categories/${categoryId}`, { name });
    }

    /** Delete a category. With deleteChannels=true, also deletes every channel inside. */
    deleteCategory(serverId, categoryId, deleteChannels) {
        return this.del(`/servers/${serverId}/categories/${categoryId}?deleteChannels=${deleteChannels ? 'true' : 'false'}`);
    }

    // ---- Member moderation ────────────────────────────────────────────────

    /** Kick a member. Requires KickMembers. */
    kickMember(serverId, userId, reason) {
        let url = `/servers/${serverId}/members/${userId}`;
        if (reason) url += `?reason=${encodeURIComponent(reason)}`;
        return this.del(url);
    }

    /** Ban a member. Requires BanMembers. */
    banMember(serverId, userId, reason) {
        return this.post(`/servers/${serverId}/members/${userId}/ban`, { reason: reason || null });
    }

    /** Lift a ban. */
    unbanMember(serverId, userId) {
        return this.del(`/servers/${serverId}/members/${userId}/ban`);
    }

    /** Set or clear a member's server nickname. Pass null/empty to clear. */
    setNickname(serverId, userId, nickname) {
        return this.patch(`/servers/${serverId}/members/${userId}/nickname`, { nickname: nickname || null });
    }

    /** Assign a role to a member. Requires ManageRoles. */
    assignRole(serverId, userId, roleId) {
        return this.put(`/servers/${serverId}/members/${userId}/roles/${roleId}`);
    }

    /** Remove a role from a member. */
    removeRole(serverId, userId, roleId) {
        return this.del(`/servers/${serverId}/members/${userId}/roles/${roleId}`);
    }

    // ---- Forum threads ────────────────────────────────────────────────────

    /** List threads in a forum channel. */
    async listThreads(serverId, channelId) {
        const resp = await this.get(`/servers/${serverId}/channels/${channelId}/threads`);
        return _unwrapList(resp);
    }

    /** Fetch a thread + its posts. */
    getThread(serverId, channelId, threadId) {
        return this.get(`/servers/${serverId}/channels/${channelId}/threads/${threadId}`);
    }

    /** Start a new thread in a forum channel. */
    createThread(serverId, channelId, title, content, tags) {
        return this.post(`/servers/${serverId}/channels/${channelId}/threads`,
            { title, content, tags: tags ? Array.from(tags) : null });
    }

    /** Reply in a thread. */
    replyToThread(serverId, channelId, threadId, content, replyToPostId) {
        return this.post(`/servers/${serverId}/channels/${channelId}/threads/${threadId}/posts`,
            { content, replyToPostId: replyToPostId || null });
    }

    // ---- Streams (read) ───────────────────────────────────────────────────

    /** The bot's own channel status: live, viewers, title, game, HLS URL. */
    myStream() { return this.get('/streams/me'); }

    /** The current live directory (public). */
    async liveStreams() { return _unwrapList(await this.get('/streams/live')); }

    /** Public live status for any account. */
    streamFor(accountId) { return this.get(`/streams/${accountId}`); }

    /** The bot's stream chat moderators. Owner-only. */
    async streamModerators() { return _unwrapList(await this.get('/streams/me/moderators')); }

    /** The bot's stream chat bans + timeouts. Owner-only. */
    async streamBans() { return _unwrapList(await this.get('/streams/me/bans')); }

    // ---- Stream moderation ────────────────────────────────────────────────

    /** Promote a chat moderator on the bot's channel. */
    addStreamModerator(accountId) {
        return this.post('/streams/me/moderators', { accountId });
    }

    /** Demote a chat moderator. */
    removeStreamModerator(accountId) {
        return this.del(`/streams/me/moderators/${accountId}`);
    }

    /** Permanently ban a chatter. */
    banStreamChatter(accountId, reason) {
        return this.post('/streams/me/bans', { accountId, reason: reason || null });
    }

    /** Temporarily ban a chatter for `durationSeconds`. */
    timeoutStreamChatter(accountId, durationSeconds, reason) {
        return this.post('/streams/me/timeouts', { accountId, durationSeconds, reason: reason || null });
    }

    /** Lift a ban or timeout. */
    liftStreamBan(accountId) {
        return this.del(`/streams/me/bans/${accountId}`);
    }

    /** Set chat mode (subscribers only, URL allow). */
    updateStreamChatSettings(allowUrls, subscribersOnly) {
        return this.patch('/streams/me/chat-settings', { allowUrls, subscribersOnly });
    }

    // ---- Webhooks (outgoing — event delivery) ─────────────────────────────

    /** List the bot's outgoing (event-delivery) webhooks. */
    async listOutgoingWebhooks() { return _unwrapList(await this.get('/webhooks/outgoing')); }

    /**
     * Register an outgoing webhook. Response includes a one-time `secret` — store it.
     * @param {string} targetType  "server" or "stream"
     * @param {number} targetId    server id, or bot's own account id for stream target
     * @param {string} url         public HTTPS endpoint receiving POSTs
     * @param {string[]} events    e.g. ["message.created"] or ["stream.online", "stream.offline"]
     */
    createOutgoingWebhook(targetType, targetId, url, events) {
        return this.post('/webhooks/outgoing', { targetType, targetId, url, events: Array.from(events) });
    }

    /** Delete an outgoing webhook. */
    deleteOutgoingWebhook(webhookId) {
        return this.del(`/webhooks/outgoing/${webhookId}`);
    }

    // ---- Webhooks (incoming — post-to-channel URL) ────────────────────────

    /** List the bot's incoming webhooks. */
    async listIncomingWebhooks() { return _unwrapList(await this.get('/webhooks/incoming')); }

    /** Create an incoming webhook bound to a channel. Response includes the one-time POST URL with its token. */
    createIncomingWebhook(serverId, channelId, name) {
        return this.post('/webhooks/incoming', { serverId, channelId, name: name || null });
    }

    /** Delete an incoming webhook. */
    deleteIncomingWebhook(webhookId) {
        return this.del(`/webhooks/incoming/${webhookId}`);
    }

    /**
     * Post to an incoming webhook URL — anonymous (no bot token; the URL token is auth).
     * Static so you can call it without instantiating a Rolespace client:
     *
     *   await Rolespace.postIncomingWebhook(url, { content: 'Deploy done!' });
     */
    static async postIncomingWebhook(webhookUrl, payload) {
        try {
            const r = await fetch(webhookUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload),
            });
            return r.ok;
        } catch { return false; }
    }

    /**
     * Send a direct message.
     *   sendDM(recipientId, "text")
     *   sendDM(recipientId, "text", embed[, embed...])
     *   sendDM(recipientId, embed)
     *   sendDM(recipientId, { content, embeds, ... })
     */
    async sendDM(recipientId, ...rest) {
        const payload = _buildMessagePayload(rest);
        payload.recipientId = recipientId;
        return new RolespaceMessage(await this.post('/dm', payload));
    }

    // ---- Interaction polling ─────────────────────────────────────────────
    /**
     * Async iterator over interactions. Yields RolespaceInteraction instances.
     *
     *   for await (const ix of rs.interactions()) {
     *       if (ix.customId === 'book') {
     *           await rs.respond(ix.id, { type: 'message', content: `Hi ${ix.user.displayName}!` });
     *       }
     *   }
     *
     * @param {object} [opts]
     * @param {number} [opts.idleDelayMs=1000]
     * @param {AbortSignal} [opts.signal]
     */
    async *interactions(opts) {
        opts = opts || {};
        const idle = opts.idleDelayMs || 1000;
        let after = 0;
        while (!(opts.signal && opts.signal.aborted)) {
            const page = await this.get(`/interactions?after=${after}`);
            const data = (page && page.data) || [];
            for (const ix of data) yield new RolespaceInteraction(ix);
            if (page && typeof page.lastId === 'number') after = page.lastId;
            if (data.length === 0) await new Promise(r => setTimeout(r, idle));
        }
    }

    /** Respond to an interaction. `reply` is { type: 'message'|'update'|'modal'|'ack', ... }. */
    respond(interactionId, reply) {
        return this.post(`/interactions/${interactionId}/callback`, reply);
    }

    // ---- Listening for new messages in a channel ───────────────────────────
    /**
     * Async iterator that yields new RolespaceMessage objects as they appear in a channel.
     * Wraps the polling loop, cursor bookkeeping, and graceful error backoff so you can write:
     *
     *   for await (const msg of rs.watchMessages(serverId, channelId, { ownAccountId: me.bot.id })) {
     *       if (msg.content.startsWith('!ping')) {
     *           await rs.sendMessage(serverId, channelId, 'pong');
     *       }
     *   }
     *
     * For high-volume / production bots, prefer outgoing webhooks (push) over polling.
     * Polling is fine for low-traffic channels, dev/testing, or environments where you
     * can't expose a public HTTP receiver.
     *
     * @param {number} serverId
     * @param {number} channelId
     * @param {object} [opts]
     * @param {number} [opts.idleDelayMs=2000]  Wait between polls.
     * @param {number} [opts.batchSize=50]      Max messages per poll.
     * @param {string|Date} [opts.since]        Don't yield messages older than this timestamp.
     *                                          Defaults to "right now" so existing history is skipped.
     * @param {boolean} [opts.includeOwn=false] Yield messages posted by THIS bot. Default skips them
     *                                          to avoid common feedback loops.
     * @param {number|null} [opts.ownAccountId] Required if includeOwn=false — pass `(await rs.me()).bot.id`.
     * @param {AbortSignal} [opts.signal]       Cancellation.
     * @param {function} [opts.onError]         Called with an Error on each polling failure.
     */
    async *watchMessages(serverId, channelId, opts) {
        opts = opts || {};
        const idle = opts.idleDelayMs || 2000;
        const batchSize = opts.batchSize || 50;
        let lastSeenTs;
        if (opts.since) {
            lastSeenTs = (opts.since instanceof Date) ? opts.since.toISOString() : String(opts.since);
        } else {
            // Bootstrap with the most-recent message timestamp (or "now") so we DON'T replay history.
            try {
                const seed = await this.get(`/servers/${serverId}/channels/${channelId}/messages?limit=1`);
                const seedData = (seed && seed.data) || [];
                lastSeenTs = seedData.length > 0
                    ? seedData[seedData.length - 1].timestamp
                    : new Date().toISOString();
            } catch (err) {
                if (opts.onError) opts.onError(err);
                lastSeenTs = new Date().toISOString();
            }
        }

        while (!(opts.signal && opts.signal.aborted)) {
            try {
                const page = await this.get(`/servers/${serverId}/channels/${channelId}/messages?limit=${batchSize}`);
                const msgs = (page && page.data) || [];
                // API returns oldest → newest within the page; iterate in order so we yield in order too.
                for (const m of msgs) {
                    if (!m || !m.timestamp || m.timestamp <= lastSeenTs) continue;
                    if (!opts.includeOwn && opts.ownAccountId
                        && m.author && Number(m.author.id) === Number(opts.ownAccountId)) {
                        lastSeenTs = m.timestamp;
                        continue;
                    }
                    yield new RolespaceMessage(m);
                    lastSeenTs = m.timestamp;
                }
            } catch (err) {
                if (opts.onError) opts.onError(err);
                // Back off harder on transient failures so we don't hammer a flaky API.
                await new Promise(r => setTimeout(r, Math.min(30000, idle * 4)));
                continue;
            }
            await new Promise(r => setTimeout(r, idle));
        }
    }

    // ---- Webhook signature verification ──────────────────────────────────
    static verifyWebhook(rawBody, signatureHeader, secret) {
        if (!rawBody || !signatureHeader || !secret) return false;
        const expected = 'sha256=' + crypto.createHmac('sha256', secret)
            .update(typeof rawBody === 'string' ? Buffer.from(rawBody) : rawBody)
            .digest('hex');
        const a = Buffer.from(expected);
        const b = Buffer.from(signatureHeader);
        return a.length === b.length && crypto.timingSafeEqual(a, b);
    }
}

// Don't leak the bot token through stringification.
Object.defineProperty(Rolespace.prototype, 'toJSON', {
    value() { return { baseUrl: this._baseUrl, token: '[redacted]' }; },
});

// ═════════════════════════ helpers ═════════════════════════════════════

/** Unwrap { data: [...] } responses; pass through bare arrays. */
function _unwrapList(resp) {
    if (Array.isArray(resp)) return resp;
    if (resp && Array.isArray(resp.data)) return resp.data;
    return [];
}

/**
 * Build a message-shaped payload from the variadic tail of sendMessage / sendDM.
 * Accepts: ["text"], ["text", embed, ...], [embed], [embed, embed], [{content, embeds, ...}].
 */
function _buildMessagePayload(rest) {
    if (rest.length === 0) return { content: '' };

    // Single object payload — anything not a string and not a RolespaceEmbed.
    if (rest.length === 1) {
        const arg = rest[0];
        if (typeof arg === 'string') return { content: arg };
        if (arg instanceof RolespaceEmbed) return { content: '', embeds: [arg.toJSON()] };
        if (arg && typeof arg === 'object') return arg; // raw payload object
    }

    // Mixed: string text + N embeds, or N embeds with no text (caller passed embeds directly).
    let content = '';
    const embeds = [];
    for (const arg of rest) {
        if (typeof arg === 'string') content = arg;
        else if (arg instanceof RolespaceEmbed) embeds.push(arg.toJSON());
        else if (arg && typeof arg === 'object') Object.assign({}, arg); // ignore stray objects to keep behavior predictable
    }
    return embeds.length > 0 ? { content, embeds } : { content };
}

module.exports = {
    Rolespace,
    RolespaceError,
    RolespaceEmbed,
    // Response wrappers exported so callers can `instanceof`-check or extend.
    RolespaceObject,
    RolespaceMe,
    RolespaceUser,
    RolespaceMember,
    RolespaceServer,
    RolespaceCategory,
    RolespaceChannel,
    RolespaceRole,
    RolespaceMessage,
    RolespaceInteraction,
};
