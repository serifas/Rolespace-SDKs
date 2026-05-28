# Rolespace .NET SDK

Minimal client for the Rolespace bot API. Targets .NET 6+.

## Install

```bash
dotnet add package Rolespace.Sdk
```

Or drop `Rolespace.cs` into your project.

## Quick start

```csharp
using Rolespace.Sdk;

// Reads ROLESPACE_BOT_TOKEN from your environment.
using var rs = RolespaceClient.FromEnv();

var me = await rs.MeAsync();
Console.WriteLine($"Logged in as {me.GetProperty("bot").GetProperty("username").GetString()}");

// Send a message:
await rs.SendMessageAsync(serverId, channelId, "Hello from my bot!");

// Listen for interactions:
await foreach (var ix in rs.InteractionsAsync())
{
    if (ix.GetProperty("customId").GetString() == "book")
    {
        var id = ix.GetProperty("id").GetInt64();
        await rs.RespondAsync(id, new { type = "message", content = "Booked!", ephemeral = true });
    }
}
```

## Webhook signature verification (ASP.NET Core)

```csharp
using Rolespace.Sdk;

app.MapPost("/webhook", async (HttpContext ctx) =>
{
    // Read the RAW body — NOT through a model binder.
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var raw = ms.ToArray();

    var sig = ctx.Request.Headers["X-Rolespace-Signature"].ToString();
    var secret = Environment.GetEnvironmentVariable("WEBHOOK_SECRET")!;
    if (!RolespaceClient.VerifyWebhook(raw, sig, secret))
        return Results.Unauthorized();

    using var doc = JsonDocument.Parse(raw);
    // ...handle doc.RootElement...
    return Results.NoContent();
});
```

**Important:** model binders eagerly parse JSON; reading from `ctx.Request.Body`
yourself is the only way to keep the original bytes for HMAC verification.

## What this SDK does for you

- Loads the token from `ROLESPACE_BOT_TOKEN` so you don't hardcode it
- Retries 429s with exponential backoff (honors `Retry-After`)
- Hides the bot token from `ToString()`
- Verifies webhook signatures with `CryptographicOperations.FixedTimeEquals` (constant-time)
- `IAsyncEnumerable<JsonElement>` over `/interactions` — no manual polling loop

## API

| Method | What it does |
|---|---|
| `rs.MeAsync()` | Bot account + owner + scopes |
| `rs.ServersAsync()` | All servers the bot is in |
| `rs.ServerAsync(id)` | One server with its channels |
| `rs.ServerChannelsAsync(id)` / `ServerMembersAsync(id)` | Lists |
| `rs.SendMessageAsync(serverId, channelId, "text" or anonymous payload)` | Post a message |
| `rs.SendDmAsync(recipientId, "text")` | Send a DM |
| `await foreach (var ix in rs.InteractionsAsync())` | Stream of interactions |
| `rs.RespondAsync(id, reply)` | Reply to an interaction |
| `rs.GetAsync/PostAsync/PatchAsync/PutAsync/DeleteAsync(path, body?)` | Raw HTTP for endpoints not covered above |
| `RolespaceClient.VerifyWebhook(rawBody, sigHeader, secret)` | Static; constant-time check |
