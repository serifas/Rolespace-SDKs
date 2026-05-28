"""
Rolespace Python SDK
====================

A tiny client for the Rolespace bot API. Only dependency: ``requests``.

Quick start::

    from rolespace import Rolespace
    rs = Rolespace.from_env()          # reads ROLESPACE_BOT_TOKEN
    me = rs.me()
    print(f"Logged in as {me['bot']['username']}")

What this SDK gives you that raw ``requests`` doesn't:

* Token is loaded from env by default (no hardcoded tokens in source)
* 429 rate-limit retries with exponential backoff + ``Retry-After``
* Generator over interactions (no manual polling loop)
* Constant-time webhook signature verification (``Rolespace.verify_webhook``)
* TLS verification is enforced; can only be disabled with an explicit, scary opt-in
* Authorization header is never logged (``repr`` redacts the token)
"""
from __future__ import annotations

import hashlib
import hmac
import os
import time
from typing import Any, Iterator, Optional, Union

import requests


DEFAULT_BASE = "https://rolespace.net"
SDK_VERSION = "0.1.0"


class RolespaceError(Exception):
    """Raised on any non-2xx response from the API."""

    def __init__(self, message: str, status: int, body: str) -> None:
        super().__init__(message)
        self.status = status
        self.body = body


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
        # One session per client so connections are pooled.
        self._sess = requests.Session()
        self._sess.headers.update({
            "Authorization": f"Bearer {token}",
            "Accept": "application/json",
            "User-Agent": f"rolespace-python/{SDK_VERSION}",
        })

    # Build a client from env vars.
    @classmethod
    def from_env(cls) -> "Rolespace":
        token = os.environ.get("ROLESPACE_BOT_TOKEN")
        if not token:
            raise RuntimeError(
                "Rolespace.from_env: set ROLESPACE_BOT_TOKEN in your environment"
            )
        return cls(token=token, base_url=os.environ.get("ROLESPACE_API_BASE", DEFAULT_BASE))

    # Hide the token from logs / debuggers / pickle.
    def __repr__(self) -> str:
        return f"Rolespace(base_url={self._base_url!r}, token='[redacted]')"

    # ---- HTTP primitives ----
    def request(self, method: str, path: str, json: Any = None) -> Any:
        """Make a raw request. Most callers should use get/post/patch/delete."""
        url = path if path.startswith("http") else (
            self._base_url + (path if path.startswith("/") else "/api/v1/" + path)
        )
        attempt = 0
        while True:
            res = self._sess.request(method, url, json=json, verify=self._verify_tls, timeout=30)
            # 429 → wait Retry-After (or exponential backoff) and try again.
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

    def get(self, path: str) -> Any:                 return self.request("GET",    path)
    def post(self, path: str, json: Any = None) -> Any:  return self.request("POST",   path, json if json is not None else {})
    def patch(self, path: str, json: Any = None) -> Any: return self.request("PATCH",  path, json if json is not None else {})
    def put(self, path: str, json: Any = None) -> Any:   return self.request("PUT",    path, json if json is not None else {})
    def delete(self, path: str) -> Any:              return self.request("DELETE", path)

    # ---- Typed convenience helpers ----
    def me(self) -> Any:                              return self.get("/me")
    def servers(self) -> Any:                         return self.get("/servers")
    def server(self, id: int) -> Any:                 return self.get(f"/servers/{id}")
    def server_channels(self, id: int) -> Any:        return self.get(f"/servers/{id}/channels")
    def server_members(self, id: int) -> Any:        return self.get(f"/servers/{id}/members")

    def send_message(self, server_id: int, channel_id: int, payload: Union[str, dict]) -> Any:
        body = {"content": payload} if isinstance(payload, str) else payload
        return self.post(f"/servers/{server_id}/channels/{channel_id}/messages", body)

    def send_dm(self, recipient_id: int, payload: Union[str, dict]) -> Any:
        body = {"content": payload} if isinstance(payload, str) else dict(payload)
        body["recipientId"] = recipient_id
        return self.post("/dm", body)

    # ---- Interaction polling ----
    def interactions(self, idle_delay: float = 1.0) -> Iterator[dict]:
        """
        Yield interactions forever. Resolves the polling loop, backoff, and
        cursor management for you::

            for ix in rs.interactions():
                rs.respond(ix["id"], {"type": "message", "content": "hi", "ephemeral": True})

        Break out of the loop normally to stop polling.
        """
        after = 0
        while True:
            page = self.get(f"/interactions?after={after}")
            data = (page or {}).get("data") or []
            for ix in data:
                yield ix
            last_id = (page or {}).get("lastId")
            if isinstance(last_id, int):
                after = last_id
            if not data:
                time.sleep(idle_delay)

    def respond(self, interaction_id: int, reply: dict) -> Any:
        """Respond to an interaction. ``reply`` is ``{"type": "message"|"update"|"modal"|"ack", ...}``."""
        return self.post(f"/interactions/{interaction_id}/callback", reply)

    # ---- Webhook signature verification ----
    @staticmethod
    def verify_webhook(raw_body: Union[bytes, str], signature_header: str, secret: str) -> bool:
        """
        Verify an ``X-Rolespace-Signature`` header against a raw request body.

        IMPORTANT: pass the RAW body bytes, NOT the parsed JSON. If your
        framework parsed the JSON first the byte order changed and the
        signature will never match. With Flask, use ``request.get_data()``
        (not ``request.json``).
        """
        if not raw_body or not signature_header or not secret:
            return False
        body = raw_body.encode() if isinstance(raw_body, str) else raw_body
        secret_bytes = secret.encode() if isinstance(secret, str) else secret
        expected = "sha256=" + hmac.new(secret_bytes, body, hashlib.sha256).hexdigest()
        return hmac.compare_digest(expected, signature_header)
