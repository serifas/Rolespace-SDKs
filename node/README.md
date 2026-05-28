# Rolespace Node.js SDK

Minimal, dependency-free client for the Rolespace bot API. Requires Node 18+.

## Install

Drop `rolespace.js` into your project, or:

```bash
npm install rolespace
```

## Quick start

```js
const { Rolespace } = require('rolespace');

// Reads ROLESPACE_BOT_TOKEN from your environment.
const rs = Rolespace.fromEnv();

const me = await rs.me();
console.log(`Logged in as ${me.bot.username} (#${me.bot.id})`);

// Send a message:
await rs.sendMessage(serverId, channelId, 'Hello from my bot!');

// Listen for interactions (button clicks, modal submits, etc.):
for await (const ix of rs.interactions()) {
    if (ix.customId === 'book') {
        await rs.respond(ix.id, { type: 'message', content: 'Booked!', ephemeral: true });
    }
}
```

## Webhook signature verification

```js
const express = require('express');
const { Rolespace } = require('rolespace');

const app = express();
app.post('/webhook', express.raw({ type: '*/*' }), (req, res) => {
    const sig = req.headers['x-rolespace-signature'];
    if (!Rolespace.verifyWebhook(req.body, sig, process.env.WEBHOOK_SECRET)) {
        return res.status(401).end();
    }
    const event = JSON.parse(req.body.toString('utf8'));
    // ...handle event...
    res.status(204).end();
});
```

**Important:** `express.raw` is required — if Express parses the JSON first the
signature check will fail.

## What this SDK does for you

- Loads the token from `ROLESPACE_BOT_TOKEN` so you don't hardcode it
- Retries 429s with exponential backoff (honors `Retry-After`)
- Hides the bot token from `console.log(client)` / `JSON.stringify`
- Verifies webhook signatures with a constant-time compare
- Async iterator over `/interactions` — no manual polling loop or cursor bookkeeping

## API

| Method | What it does |
|---|---|
| `rs.me()` | The bot account + owner + scopes |
| `rs.servers()` | All servers the bot is in |
| `rs.server(id)` | One server with its channels |
| `rs.serverChannels(id)` / `serverMembers(id)` | Lists |
| `rs.sendMessage(serverId, channelId, "text" or {content, embeds, components})` | Post a message |
| `rs.sendDM(recipientId, "text" or {...})` | Send a DM |
| `for await (const ix of rs.interactions())` | Stream of interactions |
| `rs.respond(id, reply)` | Reply to an interaction |
| `rs.get/post/patch/put/del(path, body?)` | Raw HTTP for endpoints not covered above |
| `Rolespace.verifyWebhook(rawBody, sigHeader, secret)` | Static; constant-time check |
