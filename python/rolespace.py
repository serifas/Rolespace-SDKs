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
from datetime import datetime, timezone
from typing import Any, Iterator, List, Optional, Union

import requests


def _utcnow_iso() -> str:
    """ISO-8601 UTC timestamp used as the initial cursor for watch_messages()."""
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


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


# ═════════════ Interactive components (panels of buttons + selects) ════════
#
# A bot attaches a Components panel to a message to render clickable buttons
# and select menus. When a user interacts, the bot picks the interaction up
# off ``rs.interactions()`` and replies with one of the ``rs.respond_*`` helpers.
#
# Usage::
#
#     panel = (Components()
#              .row(Button("yes", "Yes").success(),
#                   Button("no",  "No").secondary())
#              .row(Select("topic", "Pick a topic…")
#                       .option("Bugs",  "bugs")
#                       .option("Ideas", "ideas")))
#     rs.send_message(server_id, channel_id, "Pick one:", panel)
#
# Server-side limits: max 5 rows, max 5 buttons per row, max 1 select per row,
# max 25 options per select.


class Button:
    """A clickable button. Default style is ``secondary`` — chain ``.primary()`` /
    ``.success()`` / ``.danger()`` / ``.secondary()`` to change it, or use
    ``Button.link(url, label)`` for a static link button."""

    def __init__(self, custom_id: str, label: str):
        self.custom_id = custom_id
        self.label = label
        self.style = "secondary"
        self.url: Optional[str] = None

    @classmethod
    def link(cls, url: str, label: str = "Open") -> "Button":
        b = cls("", label)
        b.style = "link"
        b.url = url
        return b

    def primary(self) -> "Button":   self.style = "primary";   return self
    def secondary(self) -> "Button": self.style = "secondary"; return self
    def success(self) -> "Button":   self.style = "success";   return self
    def danger(self) -> "Button":    self.style = "danger";    return self

    def to_dict(self) -> dict:
        if self.style == "link":
            return {"type": "button", "style": "link", "label": self.label, "url": self.url}
        return {"type": "button", "style": self.style, "label": self.label, "customId": self.custom_id}


class Select:
    """A drop-down select menu. Add options with ``.option(label, value, description=None)``.
    Default is single-select; use ``.range(min, max)`` to allow multiple selections."""

    def __init__(self, custom_id: str, placeholder: Optional[str] = None):
        self.custom_id = custom_id
        self.placeholder = placeholder
        self.min_values = 1
        self.max_values = 1
        self.options: List[dict] = []

    def option(self, label: str, value: str, description: Optional[str] = None) -> "Select":
        opt: dict = {"label": label, "value": value}
        if description is not None:
            opt["description"] = description
        self.options.append(opt)
        return self

    def range(self, min_values: int, max_values: int) -> "Select":
        self.min_values = min_values
        self.max_values = max_values
        return self

    def to_dict(self) -> dict:
        return {
            "type": "select",
            "customId": self.custom_id,
            "placeholder": self.placeholder,
            "minValues": self.min_values,
            "maxValues": self.max_values,
            "options": list(self.options),
        }


class Components:
    """The root component panel: an ordered list of rows. Each ``.row(*components)``
    appends one row holding the given buttons/selects."""

    def __init__(self):
        self._rows: List[List[Any]] = []

    def row(self, *components) -> "Components":
        self._rows.append(list(components))
        return self

    def to_list(self) -> list:
        return [
            {"type": "row", "components": [c.to_dict() if hasattr(c, "to_dict") else c for c in row]}
            for row in self._rows
        ]


# ═════════════════════════════════ Modals ═══════════════════════════════════
#
# A small form a bot opens in response to an interaction. The user fills it
# and submits, which arrives as a ``modal_submit`` interaction with the values
# in ``ix.data["fields"]``.
#
# Usage::
#
#     modal = (Modal("bug-form", "Report a bug")
#              .short("summary", "Summary")
#              .paragraph("details", "What happened?"))
#     rs.respond_modal(ix.id, modal)


class Modal:
    """A modal form — title, customId, and up to 5 inputs. Add inputs with
    ``.short(...)`` / ``.paragraph(...)`` / ``.image(...)``. Chain ``.optional()``
    after an input to make it not required."""

    def __init__(self, custom_id: str, title: str):
        self.custom_id = custom_id
        self.title = title
        self._inputs: List[dict] = []

    def short(self, custom_id: str, label: str, placeholder: Optional[str] = None,
              max_length: int = 1000, value: Optional[str] = None) -> "Modal":
        self._inputs.append({
            "customId": custom_id, "label": label, "style": "short",
            "placeholder": placeholder, "required": True,
            "maxLength": max_length, "value": value,
        })
        return self

    def paragraph(self, custom_id: str, label: str, placeholder: Optional[str] = None,
                  max_length: int = 1000, value: Optional[str] = None) -> "Modal":
        self._inputs.append({
            "customId": custom_id, "label": label, "style": "paragraph",
            "placeholder": placeholder, "required": True,
            "maxLength": max_length, "value": value,
        })
        return self

    def image(self, custom_id: str, label: str, current_url: Optional[str] = None,
              multiple: bool = False) -> "Modal":
        self._inputs.append({
            "customId": custom_id, "label": label, "style": "image",
            "required": True, "multiple": multiple, "currentUrl": current_url,
        })
        return self

    def optional(self) -> "Modal":
        """Mark the LAST added input as optional."""
        if self._inputs:
            self._inputs[-1]["required"] = False
        return self

    def required(self) -> "Modal":
        """Mark the last added input as required (already the default)."""
        if self._inputs:
            self._inputs[-1]["required"] = True
        return self

    def to_dict(self) -> dict:
        return {
            "title": self.title,
            "customId": self.custom_id,
            "inputs": [dict(i) for i in self._inputs],
        }


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

    # ---- Role / permission convenience helpers ──────────────────────────

    def member_roles(self, server_id: int, user_id: int) -> List[RolespaceRole]:
        """Resolve a member's role ids into the full RolespaceRole objects
        (name, color, permissions)."""
        member = self.server_member(server_id, user_id)
        roles = self.server_roles(server_id)
        ids = set(member.role_ids)
        return [r for r in roles if r.id in ids]

    def has_role(self, server_id: int, user_id: int, role: Union[int, str]) -> bool:
        """True if the member has the given role. Pass an int id, or a name string
        (case-insensitive). Returns False if no role with that name exists.

        Example::

            if rs.has_role(server_id, msg.author.id, "Moderator"):
                rs.send_message(server_id, channel_id, "yes, boss")
        """
        member = self.server_member(server_id, user_id)
        if isinstance(role, int):
            return role in member.role_ids
        if not role or not isinstance(role, str):
            return False
        wanted = role.strip().lower()
        roles = self.server_roles(server_id)
        match = next((r for r in roles if r.name.lower() == wanted), None)
        return match is not None and match.id in member.role_ids

    def has_permission(self, server_id: int, user_id: int, permission: str) -> bool:
        """True if the member is allowed to perform the named action. The server owner
        and anyone with the ``administrator`` flag always pass.

        Accepted names (case-insensitive): ``administrator``, ``manageServer``,
        ``manageRoles``, ``manageChannels``, ``manageMessages``, ``kickMembers``,
        ``banMembers``, ``sendMessages``, ``viewChannels``, ``addReactions``.

        Example::

            if rs.has_permission(server_id, msg.author.id, "banMembers"):
                rs.ban_member(server_id, target_id, "spam")
        """
        member = self.server_member(server_id, user_id)
        if member.is_owner:
            return True
        roles = self.server_roles(server_id)
        ids = set(member.role_ids)
        for r in (r for r in roles if r.id in ids):
            perms = r.permissions or {}
            if perms.get("administrator"):
                return True
            if _permission_flag(perms, permission):
                return True
        return False

    def send_message(
        self,
        server_id: int,
        channel_id: int,
        content: Union[str, RolespaceEmbed, "Components", dict, None] = None,
        *extras,
    ) -> RolespaceMessage:
        """Send a message.

        Extras can be ``RolespaceEmbed`` instances (up to 10) and/or a single
        ``Components`` panel — pass them in any order alongside the text.

        Examples::

            rs.send_message(server_id, channel_id, "Hello!")
            rs.send_message(server_id, channel_id, "Heads up:", embed)
            rs.send_message(server_id, channel_id, "Two updates:", e1, e2)
            rs.send_message(server_id, channel_id, "Pick one:", panel)        # Components
            rs.send_message(server_id, channel_id, "Heads up:", embed, panel) # both
            rs.send_message(server_id, channel_id, embed)                     # embed only
            rs.send_message(server_id, channel_id, panel)                     # panel only
            rs.send_message(server_id, channel_id, {"content": "raw payload"})
        """
        payload = _build_message_payload(content, extras)
        return RolespaceMessage(
            self.post(f"/servers/{server_id}/channels/{channel_id}/messages", payload)
        )

    def list_messages(self, server_id: int, channel_id: int, limit: int = 50,
                      before: Optional[str] = None) -> List[RolespaceMessage]:
        """Recent messages, oldest → newest. Pass ``before`` (a message id) to page backwards."""
        url = f"/servers/{server_id}/channels/{channel_id}/messages?limit={limit}"
        if before:
            from urllib.parse import quote
            url += f"&before={quote(before)}"
        return [RolespaceMessage(m) for m in _unwrap_list(self.get(url))]

    def get_message(self, server_id: int, channel_id: int, message_id: str) -> RolespaceMessage:
        """Fetch a single message by id."""
        return RolespaceMessage(self.get(f"/servers/{server_id}/channels/{channel_id}/messages/{message_id}"))

    def edit_message(self, server_id: int, channel_id: int, message_id: str, new_content: str) -> RolespaceMessage:
        """Edit the bot's own message. Returns the updated message."""
        return RolespaceMessage(self.patch(
            f"/servers/{server_id}/channels/{channel_id}/messages/{message_id}",
            json={"content": new_content}))

    def delete_message(self, server_id: int, channel_id: int, message_id: str) -> Any:
        """Delete a message (own message OR any with ManageMessages)."""
        return self.delete(f"/servers/{server_id}/channels/{channel_id}/messages/{message_id}")

    def pin_message(self, server_id: int, channel_id: int, message_id: str) -> Any:
        """Pin a message. Requires ManageMessages."""
        return self.put(f"/servers/{server_id}/channels/{channel_id}/messages/{message_id}/pin")

    def unpin_message(self, server_id: int, channel_id: int, message_id: str) -> Any:
        """Unpin a message. Requires ManageMessages."""
        return self.delete(f"/servers/{server_id}/channels/{channel_id}/messages/{message_id}/pin")

    def add_reaction(self, server_id: int, channel_id: int, message_id: str, emoji: str) -> Any:
        """React to a message. Emoji is URL-encoded automatically."""
        from urllib.parse import quote
        return self.put(f"/servers/{server_id}/channels/{channel_id}/messages/{message_id}/reactions/{quote(emoji)}")

    def remove_reaction(self, server_id: int, channel_id: int, message_id: str, emoji: str) -> Any:
        """Remove the bot's own reaction."""
        from urllib.parse import quote
        return self.delete(f"/servers/{server_id}/channels/{channel_id}/messages/{message_id}/reactions/{quote(emoji)}")

    # ---- Channel + category management ─────────────────────────────────────

    def create_channel(self, server_id: int, name: str, type: str = "text",
                       category_id: Optional[int] = None, topic: Optional[str] = None,
                       is_private: bool = False) -> RolespaceChannel:
        """Create a channel. ``type`` is one of: text, voice, announcement, forum, rules."""
        return RolespaceChannel(self.post(f"/servers/{server_id}/channels", json={
            "name": name, "type": type, "categoryId": category_id,
            "topic": topic, "isPrivate": is_private,
        }))

    def update_channel(self, server_id: int, channel_id: int,
                       name: Optional[str] = None, topic: Optional[str] = None) -> Any:
        """Rename and/or change a channel's topic. Pass ``None`` for fields to leave alone."""
        return self.patch(f"/servers/{server_id}/channels/{channel_id}",
                          json={"name": name, "topic": topic})

    def delete_channel(self, server_id: int, channel_id: int) -> Any:
        """Delete a channel and its contents. Requires ManageChannels."""
        return self.delete(f"/servers/{server_id}/channels/{channel_id}")

    def create_category(self, server_id: int, name: str) -> Any:
        """Create a category. Requires ManageChannels."""
        return self.post(f"/servers/{server_id}/categories", json={"name": name})

    def update_category(self, server_id: int, category_id: int, name: str) -> Any:
        """Rename a category."""
        return self.patch(f"/servers/{server_id}/categories/{category_id}", json={"name": name})

    def delete_category(self, server_id: int, category_id: int, delete_channels: bool = False) -> Any:
        """Delete a category. With ``delete_channels=True``, also deletes every channel inside."""
        suffix = "true" if delete_channels else "false"
        return self.delete(f"/servers/{server_id}/categories/{category_id}?deleteChannels={suffix}")

    # ---- Member moderation ────────────────────────────────────────────────

    def kick_member(self, server_id: int, user_id: int, reason: Optional[str] = None) -> Any:
        """Kick a member. Requires KickMembers."""
        url = f"/servers/{server_id}/members/{user_id}"
        if reason:
            from urllib.parse import quote
            url += f"?reason={quote(reason)}"
        return self.delete(url)

    def ban_member(self, server_id: int, user_id: int, reason: Optional[str] = None) -> Any:
        """Ban a member. Requires BanMembers."""
        return self.post(f"/servers/{server_id}/members/{user_id}/ban", json={"reason": reason})

    def unban_member(self, server_id: int, user_id: int) -> Any:
        """Lift a ban."""
        return self.delete(f"/servers/{server_id}/members/{user_id}/ban")

    def set_nickname(self, server_id: int, user_id: int, nickname: Optional[str]) -> Any:
        """Set or clear a member's server nickname. ``None``/empty clears."""
        return self.patch(f"/servers/{server_id}/members/{user_id}/nickname",
                          json={"nickname": nickname})

    def assign_role(self, server_id: int, user_id: int, role_id: int) -> Any:
        """Assign a role to a member. Requires ManageRoles."""
        return self.put(f"/servers/{server_id}/members/{user_id}/roles/{role_id}")

    def remove_role(self, server_id: int, user_id: int, role_id: int) -> Any:
        """Remove a role from a member."""
        return self.delete(f"/servers/{server_id}/members/{user_id}/roles/{role_id}")

    # ---- Forum threads ────────────────────────────────────────────────────

    def list_threads(self, server_id: int, channel_id: int) -> list:
        """List threads in a forum channel."""
        return _unwrap_list(self.get(f"/servers/{server_id}/channels/{channel_id}/threads"))

    def get_thread(self, server_id: int, channel_id: int, thread_id: int) -> dict:
        """Fetch a thread + its posts."""
        return self.get(f"/servers/{server_id}/channels/{channel_id}/threads/{thread_id}")

    def create_thread(self, server_id: int, channel_id: int, title: str, content: str,
                      tags: Optional[List[str]] = None) -> dict:
        """Start a new thread in a forum channel."""
        return self.post(f"/servers/{server_id}/channels/{channel_id}/threads",
                         json={"title": title, "content": content, "tags": list(tags) if tags else None})

    def reply_to_thread(self, server_id: int, channel_id: int, thread_id: int, content: str,
                        reply_to_post_id: Optional[int] = None) -> dict:
        """Reply in a thread."""
        return self.post(
            f"/servers/{server_id}/channels/{channel_id}/threads/{thread_id}/posts",
            json={"content": content, "replyToPostId": reply_to_post_id})

    # ---- Streams (read) ───────────────────────────────────────────────────

    def my_stream(self) -> dict:
        """The bot's own channel status: live, viewers, title, game, HLS URL."""
        return self.get("/streams/me")

    def live_streams(self) -> list:
        """The current live directory (public)."""
        return _unwrap_list(self.get("/streams/live"))

    def stream_for(self, account_id: int) -> dict:
        """Public live status for any account."""
        return self.get(f"/streams/{account_id}")

    def stream_moderators(self) -> list:
        """The bot's stream chat moderators. Owner-only."""
        return _unwrap_list(self.get("/streams/me/moderators"))

    def stream_bans(self) -> list:
        """The bot's stream chat bans + timeouts. Owner-only."""
        return _unwrap_list(self.get("/streams/me/bans"))

    # ---- Stream moderation ────────────────────────────────────────────────

    def add_stream_moderator(self, account_id: int) -> Any:
        """Promote a chat moderator on the bot's channel."""
        return self.post("/streams/me/moderators", json={"accountId": account_id})

    def remove_stream_moderator(self, account_id: int) -> Any:
        """Demote a chat moderator."""
        return self.delete(f"/streams/me/moderators/{account_id}")

    def ban_stream_chatter(self, account_id: int, reason: Optional[str] = None) -> Any:
        """Permanently ban a chatter."""
        return self.post("/streams/me/bans", json={"accountId": account_id, "reason": reason})

    def timeout_stream_chatter(self, account_id: int, duration_seconds: int,
                               reason: Optional[str] = None) -> Any:
        """Temporarily ban a chatter for ``duration_seconds``."""
        return self.post("/streams/me/timeouts",
                         json={"accountId": account_id, "durationSeconds": duration_seconds, "reason": reason})

    def lift_stream_ban(self, account_id: int) -> Any:
        """Lift a ban or timeout."""
        return self.delete(f"/streams/me/bans/{account_id}")

    def update_stream_chat_settings(self, allow_urls: bool, subscribers_only: bool) -> Any:
        """Set chat mode (subscribers only, URL allow)."""
        return self.patch("/streams/me/chat-settings",
                          json={"allowUrls": allow_urls, "subscribersOnly": subscribers_only})

    # ---- Webhooks (outgoing — event delivery) ─────────────────────────────

    def list_outgoing_webhooks(self) -> list:
        """List the bot's outgoing (event-delivery) webhooks."""
        return _unwrap_list(self.get("/webhooks/outgoing"))

    def create_outgoing_webhook(self, target_type: str, target_id: int, url: str,
                                events: List[str]) -> dict:
        """Register an outgoing webhook. The response includes a one-time ``secret`` — store it.

        ``target_type`` is ``"server"`` or ``"stream"``. ``target_id`` is the server id (for
        "server") or the bot's own account id (for "stream"). ``events`` is a list of names
        like ``["message.created"]`` or ``["stream.online", "stream.offline"]``.
        """
        return self.post("/webhooks/outgoing", json={
            "targetType": target_type, "targetId": target_id, "url": url, "events": list(events),
        })

    def delete_outgoing_webhook(self, webhook_id: int) -> Any:
        """Delete an outgoing webhook."""
        return self.delete(f"/webhooks/outgoing/{webhook_id}")

    # ---- Webhooks (incoming — post-to-channel URL) ────────────────────────

    def list_incoming_webhooks(self) -> list:
        """List the bot's incoming webhooks."""
        return _unwrap_list(self.get("/webhooks/incoming"))

    def create_incoming_webhook(self, server_id: int, channel_id: int,
                                name: Optional[str] = None) -> dict:
        """Create an incoming webhook bound to a channel. Response includes the one-time POST URL with its token."""
        return self.post("/webhooks/incoming", json={
            "serverId": server_id, "channelId": channel_id, "name": name,
        })

    def delete_incoming_webhook(self, webhook_id: int) -> Any:
        """Delete an incoming webhook."""
        return self.delete(f"/webhooks/incoming/{webhook_id}")

    @staticmethod
    def post_incoming_webhook(webhook_url: str, payload: dict) -> bool:
        """Post to an incoming webhook URL — anonymous (no bot token; the URL token is auth).

        Static so you can call it without instantiating a client::

            Rolespace.post_incoming_webhook(url, {"content": "Deploy done!"})
        """
        try:
            r = requests.post(webhook_url, json=payload, timeout=15)
            return r.ok
        except requests.RequestException:
            return False

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
        """Respond to an interaction. ``reply`` is ``{"type": "message"|"update"|"modal"|"ack", ...}``.
        Most callers want one of the typed ``respond_*`` helpers below instead."""
        return self.post(f"/interactions/{interaction_id}/callback", reply)

    # ---- Typed interaction response helpers ─────────────────────────────────

    def respond_ack(self, interaction_id: int) -> Any:
        """Acknowledge an interaction with no visible response."""
        return self.post(f"/interactions/{interaction_id}/callback", {"type": "ack"})

    def respond_message(self, interaction_id: int, content: Union[str, RolespaceEmbed, "Components", None] = "",
                        *extras, ephemeral: bool = False) -> Any:
        """Post a new message in response to an interaction. Pass extras (embeds and/or one
        Components panel) the same way as ``send_message``. ``ephemeral=True`` makes the reply
        visible only to the user who interacted."""
        payload = _build_message_payload(content, extras)
        payload["type"] = "message"
        payload["ephemeral"] = ephemeral
        return self.post(f"/interactions/{interaction_id}/callback", payload)

    def respond_update(self, interaction_id: int, content: Optional[str] = None,
                       components: Optional["Components"] = None) -> Any:
        """Edit the panel that was clicked. Pass ``None`` for either to leave it
        unchanged; pass an empty ``Components()`` to remove the panel entirely."""
        payload: dict = {"type": "update"}
        if content is not None:
            payload["content"] = content
        if components is not None:
            payload["components"] = components.to_list()
        return self.post(f"/interactions/{interaction_id}/callback", payload)

    def respond_modal(self, interaction_id: int, modal: "Modal") -> Any:
        """Open a modal form in response to the interaction. Submission arrives as a
        ``modal_submit`` interaction with the values in ``ix.data["fields"]``."""
        return self.post(f"/interactions/{interaction_id}/callback",
                         {"type": "modal", "modal": modal.to_dict()})

    # ---- Listening for new messages in a channel ─────────────────────────
    def watch_messages(
        self,
        server_id: int,
        channel_id: int,
        idle_delay: float = 2.0,
        batch_size: int = 50,
        since: Optional[str] = None,
        include_own: bool = False,
        own_account_id: Optional[int] = None,
        on_error: Optional[Any] = None,
    ) -> Iterator[RolespaceMessage]:
        """Yield new ``RolespaceMessage`` objects from a channel as they appear.

        Wraps the polling loop, cursor bookkeeping, and graceful error backoff::

            me = rs.me()
            for msg in rs.watch_messages(server_id, channel_id, own_account_id=me.bot.id):
                if msg.content.startswith("!ping"):
                    rs.send_message(server_id, channel_id, "pong")

        For high-volume / production bots, prefer outgoing webhooks (push) over polling.
        Polling is fine for low-traffic channels, dev/testing, or environments where you
        can't expose a public HTTP receiver.

        Parameters
        ----------
        server_id, channel_id : int
        idle_delay : float
            Seconds between polls. Defaults to 2.
        batch_size : int
            Max messages per poll. Defaults to 50.
        since : str
            ISO-8601 timestamp; only yield messages newer than this. Defaults to "now"
            so existing channel history is skipped.
        include_own : bool
            When False (default), messages posted by THIS bot are filtered out. Avoids
            common reply-loop bugs.
        own_account_id : int
            Required when include_own is False. Usually ``rs.me().bot.id``.
        on_error : callable(exc)
            Called on each polling failure. Defaults to swallowing silently.
        """
        # Bootstrap the cursor: skip everything that's already in the channel.
        if since is not None:
            last_seen_ts = since
        else:
            try:
                seed = self.get(f"/servers/{server_id}/channels/{channel_id}/messages?limit=1")
                seed_data = (seed or {}).get("data") or []
                last_seen_ts = seed_data[-1]["timestamp"] if seed_data else _utcnow_iso()
            except Exception as ex:
                if on_error:
                    on_error(ex)
                last_seen_ts = _utcnow_iso()

        while True:
            try:
                page = self.get(f"/servers/{server_id}/channels/{channel_id}/messages?limit={batch_size}")
                msgs = (page or {}).get("data") or []
                # API returns oldest → newest; iterate in order.
                for m in msgs:
                    ts = m.get("timestamp")
                    if not ts or ts <= last_seen_ts:
                        continue
                    if (not include_own
                            and own_account_id is not None
                            and (m.get("author") or {}).get("id") == own_account_id):
                        last_seen_ts = ts
                        continue
                    yield RolespaceMessage(m)
                    last_seen_ts = ts
            except Exception as ex:
                if on_error:
                    on_error(ex)
                # Back off harder on transient failures so we don't hammer a flaky API.
                time.sleep(min(30.0, idle_delay * 4))
                continue
            time.sleep(idle_delay)

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


# Accepted permission names (case-insensitive). Mirrors the C# enum surface.
_PERMISSION_KEYS = {
    "administrator":   "administrator",
    "manageserver":    "manageServer",
    "manageroles":     "manageRoles",
    "managechannels":  "manageChannels",
    "managemessages":  "manageMessages",
    "kickmembers":     "kickMembers",
    "banmembers":      "banMembers",
    "sendmessages":    "sendMessages",
    "viewchannels":    "viewChannels",
    "addreactions":    "addReactions",
}


def _permission_flag(perms: dict, name: Optional[str]) -> bool:
    """True if `perms[name]` is truthy, matching keys case-insensitively.
    Returns False for unknown names."""
    if not name or not isinstance(name, str):
        return False
    key = _PERMISSION_KEYS.get(name.strip().lower())
    return bool(perms.get(key)) if key else False


def _build_message_payload(content: Any, extras: tuple) -> dict:
    """Normalize send_message/send_dm arguments into the server's expected payload shape.

    ``extras`` may contain any mix of ``RolespaceEmbed`` instances and one
    ``Components`` panel — the result will carry both as appropriate keys.
    """
    # Promote a leading Embed / Components to the extras list so the rest of the
    # logic stays uniform.
    if isinstance(content, RolespaceEmbed):
        extras = (content,) + tuple(extras)
        content = ""
    elif isinstance(content, Components):
        extras = (content,) + tuple(extras)
        content = ""
    elif isinstance(content, dict):
        # Raw payload — caller knows what they're doing. Don't merge extras into it.
        return dict(content)
    elif content is None:
        content = ""
    elif not isinstance(content, str):
        raise TypeError(f"send_message: unsupported content type {type(content).__name__}")

    embed_dicts: List[dict] = []
    components_payload = None
    for x in extras:
        if isinstance(x, RolespaceEmbed):
            embed_dicts.append(x.to_dict())
        elif isinstance(x, Components):
            if components_payload is not None:
                raise TypeError("send_message: only one Components panel is allowed per message.")
            components_payload = x.to_list()
        elif isinstance(x, dict):
            # Allow a dict in extras for raw {embed_dict} cases.
            embed_dicts.append(x)
        else:
            raise TypeError(f"send_message: unsupported extra type {type(x).__name__}")

    out: dict = {"content": content}
    if embed_dicts:
        out["embeds"] = embed_dicts
    if components_payload is not None:
        out["components"] = components_payload
    return out
