# Rolespace Python SDK

Minimal client for the Rolespace bot API. Requires Python 3.8+ and `requests`.

## Install

Drop `rolespace.py` into your project, or:

```bash
pip install rolespace
```

## Quick start

```python
from rolespace import Rolespace

# Reads ROLESPACE_BOT_TOKEN from your environment.
rs = Rolespace.from_env()

me = rs.me()
print(f"Logged in as {me['bot']['username']} (#{me['bot']['id']})")

# Send a message:
rs.send_message(server_id, channel_id, "Hello from my bot!")

# Listen for interactions (button clicks, modal submits, etc.):
for ix in rs.interactions():
    if ix["customId"] == "book":
        rs.respond(ix["id"], {"type": "message", "content": "Booked!", "ephemeral": True})
```

## Webhook signature verification

```python
from flask import Flask, request, abort
from rolespace import Rolespace
import os, json

app = Flask(__name__)
SECRET = os.environ["WEBHOOK_SECRET"]

@app.post("/webhook")
def webhook():
    raw = request.get_data()  # raw bytes — NOT request.json
    sig = request.headers.get("X-Rolespace-Signature", "")
    if not Rolespace.verify_webhook(raw, sig, SECRET):
        abort(401)
    event = json.loads(raw)
    # ...handle event...
    return ("", 204)
```

**Important:** use `request.get_data()` — `request.json` re-serializes the body
and the signature check will fail.

## What this SDK does for you

- Loads the token from `ROLESPACE_BOT_TOKEN` so you don't hardcode it
- Retries 429s with exponential backoff (honors `Retry-After`)
- Hides the bot token from `repr(client)`
- Verifies webhook signatures with `hmac.compare_digest` (constant-time)
- Generator over `/interactions` — no manual polling loop or cursor bookkeeping

## API

| Method | What it does |
|---|---|
| `rs.me()` | Bot account + owner + scopes |
| `rs.servers()` | All servers the bot is in |
| `rs.server(id)` | One server with its channels |
| `rs.server_channels(id)` / `server_members(id)` | Lists |
| `rs.send_message(server_id, channel_id, "text" or {...})` | Post a message |
| `rs.send_dm(recipient_id, "text" or {...})` | Send a DM |
| `for ix in rs.interactions():` | Stream of interactions |
| `rs.respond(id, reply)` | Reply to an interaction |
| `rs.get/post/patch/put/delete(path, json=None)` | Raw HTTP for endpoints not covered above |
| `Rolespace.verify_webhook(raw_body, sig_header, secret)` | Static; constant-time check |
