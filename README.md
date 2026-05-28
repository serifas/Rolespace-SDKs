# Rolespace SDKs

Official client libraries for the [Rolespace](https://rolespace.net) bot API, in
three flavours.

| Language | Folder | Package | Install |
|---|---|---|---|
| Node.js (18+) | [`node/`](./node) | [`rolespace`](https://www.npmjs.com/package/rolespace) on npm | `npm install rolespace` |
| Python (3.8+) | [`python/`](./python) | [`rolespace`](https://pypi.org/project/rolespace/) on PyPI | `pip install rolespace` |
| .NET (6+) | [`csharp/`](./csharp) | [`Rolespace.Sdk`](https://www.nuget.org/packages/Rolespace.Sdk) on NuGet | `dotnet add package Rolespace.Sdk` |

Each SDK is intentionally small (under 250 lines) and ships with the same
high-level shape:

- **`fromEnv()`** — loads `ROLESPACE_BOT_TOKEN` from your environment
- **HTTP helpers** — `get`/`post`/`patch`/`put`/`delete` plus typed convenience methods (`me`, `servers`, `sendMessage`, …)
- **429 retry** with exponential backoff that honours `Retry-After`
- **Interaction iterator** — `for await … of rs.interactions()` (or generator / `IAsyncEnumerable`) so you never write a polling loop
- **`verifyWebhook(rawBody, signatureHeader, secret)`** — constant-time HMAC-SHA256 verification
- **Token redaction** — `console.log`/`repr`/`ToString` never leaks the token

## Quick start

```js
// Node
const { Rolespace } = require('rolespace');
const rs = Rolespace.fromEnv();
const me = await rs.me();
```

```python
# Python
from rolespace import Rolespace
rs = Rolespace.from_env()
me = rs.me()
```

```csharp
// .NET
using Rolespace.Sdk;
using var rs = RolespaceClient.FromEnv();
var me = await rs.MeAsync();
```

## Why an SDK over raw HTTP?

The protocol is plain REST with bearer auth, so you can use any HTTP client. But
these are the things every from-scratch integration gets wrong at least once:

- Skipping constant-time compare on webhook signatures
- Naively retrying 429s and getting rate-banned
- Hardcoding the token in source
- Reinventing the polling loop for `/interactions`
- Accidentally logging the `Authorization` header

The SDKs bake all of that in.

## Documentation

Full API reference: [rolespace.net/Developer/Docs](https://rolespace.net/Developer/Docs).
Each language folder has its own README with installation, webhook-receiver examples, and the
per-method API table.

## Releasing

See [`PUBLISHING.md`](./PUBLISHING.md). TL;DR: bump the version, push a language-prefixed
tag, and GitHub Actions ships it to the registry.

## Contributing

Issues and PRs welcome. Keep the surface small; if a feature is too niche for
every-bot, it belongs in user code rather than the SDK.

## License

MIT — see [`LICENSE`](./LICENSE).
