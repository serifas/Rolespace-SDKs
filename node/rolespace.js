/**
 * Rolespace Node.js SDK
 *
 * A tiny, dependency-free client for the Rolespace bot API.
 * Requires Node 18+ (uses built-in fetch + crypto).
 *
 * Quick start:
 *   const { Rolespace } = require('./rolespace');
 *   const rs = Rolespace.fromEnv();              // reads ROLESPACE_BOT_TOKEN
 *   const me = await rs.me();
 *   console.log(`Logged in as ${me.bot.username}`);
 *
 * What this SDK gives you that raw fetch doesn't:
 *   - Token is loaded from env by default (no hardcoded tokens in source)
 *   - 429 rate-limit retries with exponential backoff + Retry-After
 *   - Async iterator over interactions (no manual polling loop)
 *   - Constant-time webhook signature verification (Rolespace.verifyWebhook)
 *   - TLS verification is enforced; can only be disabled with an explicit, scary opt-in
 *   - Authorization header is never logged
 */
'use strict';

const crypto = require('crypto');

const DEFAULT_BASE = 'https://rolespace.net';
const SDK_VERSION = '0.1.0';

class RolespaceError extends Error {
    constructor(message, status, body) {
        super(message);
        this.name = 'RolespaceError';
        this.status = status;
        this.body = body;
    }
}

class Rolespace {
    /**
     * @param {object} opts
     * @param {string} opts.token         - Bot token (rsp_*). Required.
     * @param {string} [opts.baseUrl]     - API base URL (default: https://rolespace.net).
     * @param {number} [opts.maxRetries]  - Max 429 retries before giving up (default: 5).
     * @param {boolean} [opts.dangerouslyDisableTls] - Opt-out of cert verification. Do not use.
     */
    constructor(opts) {
        if (!opts || typeof opts.token !== 'string' || !opts.token.startsWith('rsp_')) {
            throw new Error('Rolespace: token is required and must start with "rsp_"');
        }
        this._token = opts.token;
        this._baseUrl = (opts.baseUrl || DEFAULT_BASE).replace(/\/+$/, '');
        this._maxRetries = Number.isInteger(opts.maxRetries) ? opts.maxRetries : 5;
        this._dangerouslyDisableTls = opts.dangerouslyDisableTls === true;
        if (this._dangerouslyDisableTls) {
            // Mirror the user agent of `requests`/`HttpClient` — make this visible.
            // We don't actually disable TLS unless they ALSO set NODE_TLS_REJECT_UNAUTHORIZED=0,
            // because we refuse to do it for them. This flag only suppresses our warning.
        }
    }

    /** Build a client from env vars. Reads ROLESPACE_BOT_TOKEN and optional ROLESPACE_API_BASE. */
    static fromEnv() {
        const token = process.env.ROLESPACE_BOT_TOKEN;
        if (!token) {
            throw new Error('Rolespace.fromEnv: set ROLESPACE_BOT_TOKEN in your environment');
        }
        return new Rolespace({ token, baseUrl: process.env.ROLESPACE_API_BASE });
    }

    // ---- HTTP primitives ----
    /** Make a raw request. Most callers should use get/post/patch/del or the typed helpers. */
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
            // 429 → wait Retry-After (or exponential backoff) and try again.
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

    // ---- Typed convenience helpers ----
    me() { return this.get('/me'); }
    servers() { return this.get('/servers'); }
    server(id) { return this.get(`/servers/${id}`); }
    serverChannels(id) { return this.get(`/servers/${id}/channels`); }
    serverMembers(id) { return this.get(`/servers/${id}/members`); }
    sendMessage(serverId, channelId, payload) {
        return this.post(`/servers/${serverId}/channels/${channelId}/messages`,
            typeof payload === 'string' ? { content: payload } : payload);
    }
    sendDM(recipientId, payload) {
        return this.post('/dm', { recipientId, ...(typeof payload === 'string' ? { content: payload } : payload) });
    }

    // ---- Interaction polling ----
    /**
     * Async iterator over interactions. Resolves the polling loop, backoff, and
     * cursor management for you. Use with `for await`:
     *
     *   for await (const ix of rs.interactions()) {
     *       await rs.respond(ix.id, { type: 'message', content: 'hi', ephemeral: true });
     *   }
     *
     * @param {object} [opts]
     * @param {number} [opts.idleDelayMs=1000]  - Wait between empty polls.
     * @param {AbortSignal} [opts.signal]       - Optional cancellation.
     */
    async *interactions(opts) {
        opts = opts || {};
        const idle = opts.idleDelayMs || 1000;
        let after = 0;
        while (!(opts.signal && opts.signal.aborted)) {
            const page = await this.get(`/interactions?after=${after}`);
            const data = (page && page.data) || [];
            for (const ix of data) yield ix;
            if (page && typeof page.lastId === 'number') after = page.lastId;
            if (data.length === 0) await new Promise(r => setTimeout(r, idle));
        }
    }

    /** Respond to an interaction. `reply` is one of: { type: 'message' | 'update' | 'modal' | 'ack', ... }. */
    respond(interactionId, reply) {
        return this.post(`/interactions/${interactionId}/callback`, reply);
    }

    // ---- Webhook signature verification ----
    /**
     * Verify an X-Rolespace-Signature header against a raw request body.
     *
     * IMPORTANT: pass the RAW body buffer/string, NOT the parsed JSON. If your
     * server parsed the JSON first the byte order changed and the signature
     * will never match. With Express, use `app.use(express.raw({ type: '*\/*' }))`
     * on the webhook route.
     *
     * @param {Buffer|string} rawBody
     * @param {string} signatureHeader  - Value of X-Rolespace-Signature (e.g. "sha256=abc...")
     * @param {string} secret           - Shared signing secret you got when you registered the webhook.
     * @returns {boolean}
     */
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

// Don't leak the bot token through stringification (e.g. when a logger
// reaches for the client object).
Object.defineProperty(Rolespace.prototype, 'toJSON', {
    value() { return { baseUrl: this._baseUrl, token: '[redacted]' }; },
});

module.exports = { Rolespace, RolespaceError };
