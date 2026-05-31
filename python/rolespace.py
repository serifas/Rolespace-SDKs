"""
Rolespace Python SDK
====================

A tiny client for the Rolespace bot API. Only dependency: ``requests``.

Quick start::

    from rolespace import Rolespace, RolespaceEmbed
    rs = Rolespace.from_env()          # reads ROLESPACE_BOT_TOKEN
    me = rs.me()
    print(f"Logged in as {me.bot.username}")

    msg = rs.send_message(server_id, channel_id, "Hello!")
    print(msg.id, msg.content)

    card = (RolespaceEmbed()
            .with_title("Patch 2.4")
            .with_color("#5f85f7")
            .add_field("Author", "@lynn", inline=True))
    rs.send_message(server_id, channel_id, "Heads up:", card)

What this SDK gives you that raw ``requests`` doesn't:

* Strongly-typed wrapper classes — ``msg.content`` instead of ``msg["content"]``,
  with safe ``None``/default returns for missing fields
* ``RolespaceEmbed`` builder so you don't hand-shape JSON for rich cards
* Token is loaded from env by default (no hardcoded tokens in source)
* 429 rate-limit retries with exponential backoff + ``Retry-After``
* Generator over interactions (no manual polling loop)
* Constant-time webhook signature verification (``Rolespace.verify_webhook``)
* TLS verification is enforced; can only be disabled with an explicit, scary opt-in
* Authorization header is never logged (``repr`` redacts the token)

Escape hatch: every typed wrapper exposes ``.raw`` — the underlying parsed dict —
so any field the server adds before this SDK is updated still works::

    new_field = msg.raw["brandNewField"]
"""
from __future__ import annotations

import hashlib
import hmac
import os
import time
from typing import Any, Iterator, List, Optional, Union

import requests


DEFAULT_BASE = "https://rolespace.net"
SDK_VERSION = "0.2.0"


class RolespaceError(Exception):
    """Raised on any non-2xx response from the API."""

    def __init__(self, message: str, status: int, body: str) -> None:
        super().__init__(message)
        self.status = status
        self.body = body


# ═══════════════════ Response wrapper base class ═══════════════════════
# All typed response wrappers extend this. Subclasses just declare @property
# accessors over self._raw. Missing fields return sensible defaults from the
# helpers below rather than raising KeyError.


class RolespaceObject:
    """Base class for typed wrappers around API responses."""

    __slots__ = ("_raw",)

    def __init__(self, raw: Optional[dict]) -> None:
        # Store the underlying parsed JSON so callers can always reach unmodelled fields.
        object.__setattr__(self, "_raw", raw or {})

    @property
    def raw(self) -> dict:
        """The underlying parsed JSON dict. Use for fields not yet modelled."""
        return self._raw

    # — typed accessors used by subclasses —

    def _str(self, name: str, default: str = "") -> str:
        v = self._raw.get(name)
        return v if isinstance(v, str) else default

    def _str_or_none(self, name: str) -> Optional[str]:
        v = self._raw.get(name)
        return v if isinstance(v, str) else None

    def _int(self, name: str, default: int = 0) -> int:
        v = self._raw.get(name)
        return v if isinstance(v, int) and not isinstance(v, bool) else default

    def _bool(self, name: str) -> bool:
        return bool(self._raw.get(name))

    def __repr__(self) -> str:
        return f"{type(self).__name__}({self._raw!r})"


# ═══════════════════════════ /me ═══════════════════════════════════════


class RolespaceMe(RolespaceObject):
    """The authenticated application: bot identity + owner + scopes."""

    @property
    def bot(self) -> "RolespaceUser":
        """The bot account — author of anything the bot does."""
        return RolespaceUser(self._raw.get("bot") or {})

    @property
    def owner(self) -> Optional["RolespaceUser"]:
        """The human owner of the application, if visible."""
        o = self._raw.get("owner")
        return RolespaceUser(o) if o else None

    @property
    def scopes(self) -> List[str]:
        """Granted OAuth-style scopes (e.g. messages.write)."""
        app = self._raw.get("application") or {}
        return list(app.get("scopes") or [])

    @property
    def application_id(self) -> int:
        """Numeric id of the application registration."""
        app = self._raw.get("application") or {}
        return app.get("id") or 0


# ═══════════════════════ Users / members ═══════════════════════════════


class RolespaceUser(RolespaceObject):
    """A user-shaped object — bot/owner in /me, author in messages."""

    @property
    def id(self) -> int:
        return self._int("id") or self._int("userId")

    @property
    def username(self) -> str: return self._str("username")
    @property
    def display_name(self) -> str: return self._str("displayName")
    @property
    def nickname(self) -> Optional[str]: return self._str_or_none("nickname")
    @property
    def avatar_url(self) -> Optional[str]:
        return self._str_or_none("avatar") or self._str_or_none("avatarUrl")


class RolespaceMember(RolespaceUser):
    """A member of a server: user fields plus server-scoped role assignments."""

    @property
    def is_owner(self) -> bool: return self._bool("isOwner")

    @property
    def role_ids(self) -> List[int]:
        return list(self._raw.get("roleIds") or [])


# ═══════════════════════════ Servers ═══════════════════════════════════


class RolespaceServer(RolespaceObject):
    """A server (a.k.a. guild). Categories+channels are populated for /servers/{id};
    list responses give summary objects with empty Categories."""

    @property
    def id(self) -> int: return self._int("id")
    @property
    def name(self) -> str: return self._str("name")
    @property
    def description(self) -> Optional[str]: return self._str_or_none("description")
    @property
    def icon_url(self) -> Optional[str]: return self._str_or_none("iconUrl")
    @property
    def banner_url(self) -> Optional[str]: return self._str_or_none("bannerUrl")
    @property
    def is_public(self) -> bool: return self._bool("isPublic")
    @property
    def owner_id(self) -> int: return self._int("ownerId")
    @property
    def member_count(self) -> int: return self._int("memberCount")
    @property
    def created_at(self) -> Optional[str]: return self._str_or_none("createdAt")

    @property
    def categories(self) -> List["RolespaceCategory"]:
        return [RolespaceCategory(c) for c in (self._raw.get("categories") or [])]

    def all_channels(self) -> List["RolespaceChannel"]:
        """Flatten the category tree into a single channel list."""
        out: List["RolespaceChannel"] = []
        for cat in self.categories:
            out.extend(cat.channels)
        return out


class RolespaceCategory(RolespaceObject):
    @property
    def id(self) -> int: return self._int("id")
    @property
    def name(self) -> str: return self._str("name")
    @property
    def position(self) -> int: return self._int("position")
    @property
    def channels(self) -> List["RolespaceChannel"]:
        return [RolespaceChannel(c) for c in (self._raw.get("channels") or [])]


class RolespaceChannel(RolespaceObject):
    @property
    def id(self) -> int: return self._int("id")
    @property
    def server_id(self) -> int: return self._int("serverId")
    @property
    def category_id(self) -> Optional[int]:
        v = self._raw.get("categoryId")
        return v if isinstance(v, int) and not isinstance(v, bool) else None
    @property
    def category_name(self) -> Optional[str]: return self._str_or_none("categoryName")
    @property
    def name(self) -> str: return self._str("name")
    @property
    def topic(self) -> Optional[str]: return self._str_or_none("topic")
    @property
    def type(self) -> str:
        """Lowercase string: 'text', 'voice', 'announcement', 'forum', 'rules'."""
        return self._str("type")
    @property
    def position(self) -> int: return self._int("position")
    @property
    def is_nsfw(self) -> bool: return self._bool("isNsfw")
    @property
    def is_private(self) -> bool: return self._bool("isPrivate")


class RolespaceRole(RolespaceObject):
    @property
    def id(self) -> int: return self._int("id")
    @property
    def name(self) -> str: return self._str("name")
    @property
    def color(self) -> Optional[str]: return self._str_or_none("color")
    @property
    def position(self) -> int: return self._int("position")
    @property
    def is_everyone(self) -> bool: return self._bool("isEveryone")
    @property
    def is_default(self) -> bool: return self._bool("isDefault")
    @property
    def permissions(self) -> dict: return self._raw.get("permissions") or {}


# ═══════════════════════════ Messages ══════════════════════════════════


class RolespaceMessage(RolespaceObject):
    """A chat message. The ``id`` is a string (GUID), not numeric."""

    @property
    def id(self) -> str: return self._str("id")
    @property
    def channel_id(self) -> int: return self._int("channelId")
    @property
    def server_id(self) -> int: return self._int("serverId")
    @property
    def content(self) -> str: return self._str("content")
    @property
    def timestamp(self) -> Optional[str]: return self._str_or_none("timestamp")
    @property
    def edited_at(self) -> Optional[str]: return self._str_or_none("editedAt")
    @property
    def is_pinned(self) -> bool: return self._bool("isPinned")
    @property
    def reply_to_message_id(self) -> Optional[str]: return self._str_or_none("replyToMessageId")
    @property
    def author(self) -> RolespaceUser:
        return RolespaceUser(self._raw.get("author") or {})
    @property
    def reactions(self) -> list: return list(self._raw.get("reactions") or [])
    @property
    def attachments(self) -> list: return list(self._raw.get("attachments") or [])


# ═══════════════════════ Interactions ══════════════════════════════════


class RolespaceInteraction(RolespaceObject):
    """A user interaction with one of your bot's components."""

    @property
    def id(self) -> int: return self._int("id")
    @property
    def type(self) -> str:
        """One of 'button', 'select', 'modal_submit'."""
        return self._str("type")
    @property
    def custom_id(self) -> str:
        """The customId the bot set on the component. Use this to dispatch."""
        return self._str("customId")
    @property
    def server_id(self) -> int: return self._int("serverId")
    @property
    def channel_id(self) -> int: return self._int("channelId")
    @property
    def message_id(self) -> Optional[str]: return self._str_or_none("messageId")
    @property
    def user(self) -> RolespaceUser:
        return RolespaceUser(self._raw.get("user") or {})
    @property
    def data(self) -> Optional[dict]:
        """Extra payload — shape varies by type."""
        v = self._raw.get("data")
        return v if isinstance(v, dict) else None
    @property
    def created_at(self) -> Optional[str]: return self._str_or_none("createdAt")


# ════════════════════════ Embeds (rich cards) ══════════════════════════
#
# Outbound builder. Two styles work — pick whichever reads better:
#
#   # Plain-init style:
#   card = RolespaceEmbed(title="Hi", color="#5f85f7")
#   card.add_field("Author", "@lynn", inline=True)
#
#   # Fluent builder style:
#   card = (RolespaceEmbed()
#           .with_title("Hi")
#           .with_color("#5f85f7")
#           .add_field("Author", "@lynn", inline=True))
#
# Pass to send_message as the last (or only) positional argument.


class RolespaceEmbed:
    """A rich card you can attach to a message — title/description/fields/image/etc."""

    def __init__(
        self,
        title: Optional[str] = None,
        description: Optional[str] = None,
        color: Optional[str] = None,
        url: Optional[str] = None,
        image: Optional[str] = None,
        image_flag: Optional[str] = None,
        thumbnail: Optional[str] = None,
        author: Optional[dict] = None,
        footer: Optional[str] = None,
    ) -> None:
        self.title = title
        self.description = description
        self.color = color
        self.url = url
        self.image = image
        self.image_flag = image_flag
        self.thumbnail = thumbnail
        self.author = author
        self.footer = footer
        self.fields: List[dict] = []
        self.gallery: List[Any] = []

    # — fluent setters —

    def with_title(self, title: str) -> "RolespaceEmbed":
        self.title = title; return self
    def with_url(self, url: str) -> "RolespaceEmbed":
        self.url = url; return self
    def with_description(self, description: str) -> "RolespaceEmbed":
        self.description = description; return self
    def with_color(self, hex_color: str) -> "RolespaceEmbed":
        self.color = hex_color; return self
    def with_image(self, url: str, flag: Optional[str] = None) -> "RolespaceEmbed":
        """flag: optional 'nsfw' | 'triggering' | 'spoiler'."""
        self.image = url
        self.image_flag = flag
        return self
    def with_thumbnail(self, url: str) -> "RolespaceEmbed":
        self.thumbnail = url; return self
    def with_author(self, name: str, icon_url: Optional[str] = None) -> "RolespaceEmbed":
        self.author = {"name": name, "iconUrl": icon_url} if icon_url else {"name": name}
        return self
    def with_footer(self, footer: str) -> "RolespaceEmbed":
        self.footer = footer; return self

    def add_field(self, name: str, value: str, inline: bool = False) -> "RolespaceEmbed":
        """Append an inline name/value field. Up to 25 per embed."""
        self.fields.append({"name": name, "value": value, "inline": inline})
        return self

    def add_gallery_image(self, url: str, flag: Optional[str] = None) -> "RolespaceEmbed":
        """Append a gallery image. Up to 24 per embed."""
        self.gallery.append({"url": url, "flag": flag} if flag else url)
        return self

    def to_dict(self) -> dict:
        """Build the JSON-shaped dict the server expects. Omits empty/None fields."""
        out: dict = {}
        for k, v in [
            ("title", self.title),
            ("url", self.url),
            ("description", self.description),
            ("color", self.color),
            ("image", self.image),
            ("imageFlag", self.image_flag),
            ("thumbnail", self.thumbnail),
            ("author", self.author),
            ("footer", self.footer),
        ]:
            if v is not None:
                out[k] = v
        if self.fields:
            out["fields"] = self.fields
        if self.gallery:
            out["gallery"] = self.gallery
        return out


# ════════════════════════ Main client ══════════════════════════════════


class Rolespace:
    """Synchronous client for the Rolespace bot API."""

    def __init__(
        self,
        token: str,
        base_url: str = DEFAULT_BASE,
        max_retries: int = 5,
        dangerously_disable_tls: bool = False,
    ) -> None:
        if not token or not token.startswith("rsp_"):
            raise ValueError('Rolespace: token is required and must start with "rsp_"')
        self._token = token
        self._base_url = base_url.rstrip("/")
        self._max_retries = max_retries
        self._verify_tls = not dangerously_disable_tls
        self._sess = requests.Session()
        self._sess.headers.update({
            "Authorization": f"Bearer {token}",
            "Accept": "application/json",
            "User-Agent": f"rolespace-python/{SDK_VERSION}",
        })

    @classmethod
    def from_env(cls) -> "Rolespace":
        """Build a client from env vars (ROLESPACE_BOT_TOKEN required, ROLESPACE_API_BASE optional)."""
        token = os.environ.get("ROLESPACE_BOT_TOKEN")
        if not token:
            raise RuntimeError("Rolespace.from_env: set ROLESPACE_BOT_TOKEN in your environment")
        return cls(token=token, base_url=os.environ.get("ROLESPACE_API_BASE", DEFAULT_BASE))

    def __repr__(self) -> str:
        return f"Rolespace(base_url={self._base_url!r}, token='[redacted]')"

    # ---- HTTP primitives ─────────────────────────────────────────────────

    def request(self, method: str, path: str, json: Any = None) -> Any:
        """Make a raw request. Most callers should use get/post/patch/delete."""
        url = path if path.startswith("http") else (
            self._base_url + (path if path.startswith("/") else "/api/v1/" + path)
        )
        attempt = 0
        while True:
            res = self._sess.request(method, url, json=json, verify=self._verify_tls, timeout=30)
            if res.status_code == 429 and attempt < self._max_retries:
                try:
                    ra = float(res.headers.get("Retry-After", "0"))
                except ValueError:
                    ra = 0.0
                wait = ra if ra > 0 else min(30.0, 0.5 * (2 ** attempt))
                time.sleep(wait)
                attempt += 1
                continue
            if not res.ok:
                raise RolespaceError(
                    f"Rolespace API {res.status_code} on {method} {path}: {res.text[:300]}",
                    res.status_code, res.text,
                )
            if res.status_code == 204 or not res.content:
                return None
            ctype = res.headers.get("Content-Type", "")
            return res.json() if "application/json" in ctype else res.text

    def get(self, path: str) -> Any:                            return self.request("GET",    path)
    def post(self, path: str, json: Any = None) -> Any:         return self.request("POST",   path, json if json is not None else {})
    def patch(self, path: str, json: Any = None) -> Any:        return self.request("PATCH",  path, json if json is not None else {})
    def put(self, path: str, json: Any = None) -> Any:          return self.request("PUT",    path, json if json is not None else {})
    def delete(self, path: str) -> Any:                         return self.request("DELETE", path)

    # ---- Typed convenience helpers ───────────────────────────────────────

    def me(self) -> RolespaceMe:
        """The authenticated application + bot identity + owner + scopes."""
        return RolespaceMe(self.get("/me"))

    def servers(self) -> List[RolespaceServer]:
        """All servers the bot has been added to (summary objects)."""
        return [RolespaceServer(s) for s in _unwrap_list(self.get("/servers"))]

    def server(self, id: int) -> RolespaceServer:
        """One server with its categories and visible channels."""
        return RolespaceServer(self.get(f"/servers/{id}"))

    def server_channels(self, id: int) -> List[RolespaceChannel]:
        """Flat list of channels the bot can view in the server."""
        return [RolespaceChannel(c) for c in _unwrap_list(self.get(f"/servers/{id}/channels"))]

    def server_members(self, id: int) -> List[RolespaceMember]:
        """All members of the server."""
        return [RolespaceMember(m) for m in _unwrap_list(self.get(f"/servers/{id}/members"))]

    def server_member(self, server_id: int, user_id: int) -> RolespaceMember:
        """One member of the server."""
        return RolespaceMember(self.get(f"/servers/{server_id}/members/{user_id}"))

    def server_roles(self, id: int) -> List[RolespaceRole]:
        """All roles in the server, highest position first."""
        return [RolespaceRole(r) for r in _unwrap_list(self.get(f"/servers/{id}/roles"))]

    def send_message(
        self,
        server_id: int,
        channel_id: int,
        content: Union[str, RolespaceEmbed, dict, None] = None,
        *embeds: RolespaceEmbed,
    ) -> RolespaceMessage:
        """Send a message.

        Examples::

            rs.send_message(server_id, channel_id, "Hello!")
            rs.send_message(server_id, channel_id, "Heads up:", embed)
            rs.send_message(server_id, channel_id, "Two updates:", e1, e2)
            rs.send_message(server_id, channel_id, embed)          # embed only
            rs.send_message(server_id, channel_id, {"content": "raw payload"})
        """
        payload = _build_message_payload(content, embeds)
        return RolespaceMessage(
            self.post(f"/servers/{server_id}/channels/{channel_id}/messages", payload)
        )

    def get_message(self, server_id: int, channel_id: int, message_id: str) -> RolespaceMessage:
        """Fetch a single message by id."""
        return RolespaceMessage(self.get(f"/servers/{server_id}/channels/{channel_id}/messages/{message_id}"))

    def send_dm(
        self,
        recipient_id: int,
        content: Union[str, RolespaceEmbed, dict, None] = None,
        *embeds: RolespaceEmbed,
    ) -> RolespaceMessage:
        """Send a direct message. Same shape as send_message but without server/channel."""
        payload = _build_message_payload(content, embeds)
        payload["recipientId"] = recipient_id
        return RolespaceMessage(self.post("/dm", payload))

    # ---- Interaction polling ─────────────────────────────────────────────

    def interactions(self, idle_delay: float = 1.0) -> Iterator[RolespaceInteraction]:
        """Yield interactions forever (as RolespaceInteraction instances).

        Example::

            for ix in rs.interactions():
                if ix.custom_id == "book":
                    rs.respond(ix.id, {"type": "message", "content": f"Hi {ix.user.display_name}!"})
        """
        after = 0
        while True:
            page = self.get(f"/interactions?after={after}")
            data = (page or {}).get("data") or []
            for ix in data:
                yield RolespaceInteraction(ix)
            last_id = (page or {}).get("lastId")
            if isinstance(last_id, int):
                after = last_id
            if not data:
                time.sleep(idle_delay)

    def respond(self, interaction_id: int, reply: dict) -> Any:
        """Respond to an interaction. ``reply`` is ``{"type": "message"|"update"|"modal"|"ack", ...}``."""
        return self.post(f"/interactions/{interaction_id}/callback", reply)

    # ---- Webhook signature verification ──────────────────────────────────

    @staticmethod
    def verify_webhook(raw_body: Union[bytes, str], signature_header: str, secret: str) -> bool:
        """Verify an ``X-Rolespace-Signature`` header against a raw request body.

        IMPORTANT: pass the RAW body bytes, NOT the parsed JSON. With Flask use
        ``request.get_data()`` (NOT ``request.json``).
        """
        if not raw_body or not signature_header or not secret:
            return False
        body = raw_body.encode() if isinstance(raw_body, str) else raw_body
        secret_bytes = secret.encode() if isinstance(secret, str) else secret
        expected = "sha256=" + hmac.new(secret_bytes, body, hashlib.sha256).hexdigest()
        return hmac.compare_digest(expected, signature_header)


# ═════════════════════════ helpers ═════════════════════════════════════


def _unwrap_list(resp: Any) -> list:
    """Unwrap ``{"data": [...]}`` responses; pass through bare arrays."""
    if isinstance(resp, list):
        return resp
    if isinstance(resp, dict) and isinstance(resp.get("data"), list):
        return resp["data"]
    return []


def _build_message_payload(content: Any, embeds: tuple) -> dict:
    """Normalize send_message/send_dm arguments into the server's expected payload shape."""
    embed_dicts = [e.to_dict() if isinstance(e, RolespaceEmbed) else e for e in embeds]

    if content is None:
        return {"content": "", **({"embeds": embed_dicts} if embed_dicts else {})}
    if isinstance(content, str):
        return {"content": content, **({"embeds": embed_dicts} if embed_dicts else {})}
    if isinstance(content, RolespaceEmbed):
        # Treat the leading embed as just another embed; no text.
        return {"content": "", "embeds": [content.to_dict(), *embed_dicts]}
    if isinstance(content, dict):
        # Raw payload — caller knows what they're doing. Don't merge embeds into it.
        return dict(content)
    raise TypeError(f"send_message: unsupported content type {type(content).__name__}")
