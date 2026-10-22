"""A scripted stand-in for the Escape from Tarkov client, talking to a real SPT 4.1.6 server.

It speaks the same HTTP API the game uses (plain JSON with requestcompressed/responsecompressed = 0),
which is enough to create profiles, chat with the Archipelago contact, accept/complete quests and
start/end raids exactly like the game does.
"""
from __future__ import annotations

import json
import ssl
import time
import urllib.request
from typing import Any, Dict, List, Optional

BOT_ID = "6727ac0f4a7b9e3d2c1f0a01"
SYSTEM_DIALOG = "59e7125688a45068a6249071"
USEC_HEAD = "5fdb5950f5264a66150d1c6e"
USEC_VOICE = "5fc614f40b735e7b024c76e9"


class SptClient:
    def __init__(self, base: str = "https://127.0.0.1:6969") -> None:
        self.base = base
        self.session: Optional[str] = None
        self._ctx = ssl.create_default_context()
        self._ctx.check_hostname = False
        self._ctx.verify_mode = ssl.CERT_NONE

    # ------------------------------------------------------------------ transport
    def raw(self, url: str, body: Any = None, method: str = "POST") -> str:
        data = None if body is None else json.dumps(body).encode()
        if method == "POST" and data is None:
            data = b"{}"
        req = urllib.request.Request(self.base + url, data=data, method=method)
        req.add_header("Content-Type", "application/json")
        req.add_header("requestcompressed", "0")
        req.add_header("responsecompressed", "0")
        if self.session:
            req.add_header("Cookie", f"PHPSESSID={self.session}")
        with urllib.request.urlopen(req, context=self._ctx, timeout=120) as resp:
            return resp.read().decode("utf-8")

    def call(self, url: str, body: Any = None, method: str = "POST") -> Any:
        text = self.raw(url, body, method)
        try:
            parsed = json.loads(text)
        except json.JSONDecodeError:
            return text
        if isinstance(parsed, dict) and "err" in parsed and "data" in parsed:
            if parsed["err"] not in (0, None):
                raise RuntimeError(f"{url} -> err {parsed['err']}: {parsed.get('errmsg')}")
            return parsed["data"]
        return parsed

    # ------------------------------------------------------------------ account
    def register_and_login(self, username: str, password: str = "", edition: str = "Standard") -> str:
        try:
            # SPT 4.1+: the v2 launcher API; the session id is the profile id
            self.call("/launcher/v2/register", {"username": username, "password": password, "edition": edition})
            info = self.call("/launcher/v2/profile", {"username": username, "password": password})
            sid = (info.get("Response") or info.get("response") or {}).get("profileId") if isinstance(info, dict) else None
        except Exception:  # noqa: BLE001
            sid = None
        if not sid:
            # SPT 4.0
            self.raw("/launcher/profile/register", {"username": username, "password": password, "edition": edition})
            sid = self.raw("/launcher/profile/login", {"username": username, "password": password}).strip().strip('"')
        if not sid or sid.upper() == "FAILED":
            raise RuntimeError(f"login failed: {sid}")
        self.session = sid
        return sid

    def create_character(self, nickname: str, side: str = "Usec") -> None:
        self.call("/client/game/profile/create",
                  {"side": side, "nickname": nickname, "headId": USEC_HEAD, "voiceId": USEC_VOICE})

    def start_game(self) -> None:
        self.call("/client/game/start")
        self.call("/client/game/profile/select", {"uid": self.session})

    def profiles(self) -> List[Dict[str, Any]]:
        return self.call("/client/game/profile/list")

    def pmc(self) -> Dict[str, Any]:
        for p in self.profiles():
            if p.get("Info", {}).get("Side") in ("Usec", "Bear"):
                return p
        raise RuntimeError("no pmc profile")

    def scav(self) -> Dict[str, Any]:
        for p in self.profiles():
            if p.get("Info", {}).get("Side") == "Savage":
                return p
        raise RuntimeError("no scav profile")

    def regenerate_scav(self) -> Dict[str, Any]:
        return self.call("/client/game/profile/savage/regenerate")[0]

    # ------------------------------------------------------------------ chat
    def friends(self) -> Any:
        return self.call("/client/friend/list")

    def say(self, text: str) -> None:
        self.call("/client/mail/msg/send", {"dialogId": BOT_ID, "type": 1, "text": text, "replyTo": ""})

    def dialog(self, dialog_id: str = BOT_ID, kind: int = 1) -> List[Dict[str, Any]]:
        data = self.call("/client/mail/dialog/view", {"dialogId": dialog_id, "type": kind, "limit": 500, "time": 0})
        return data.get("messages", []) if isinstance(data, dict) else []

    def bot_messages(self) -> List[Dict[str, Any]]:
        msgs = [m for m in self.dialog() if m.get("uid") == BOT_ID]
        return sorted(msgs, key=lambda m: m.get("dt", 0))

    def bot_texts(self) -> List[str]:
        return [m.get("text", "") for m in self.bot_messages()]

    def bot_ids(self) -> set:
        return {m["_id"] for m in self.bot_messages()}

    def wait_for_bot(self, needle: str, timeout: float = 30.0, start_count: Any = None) -> str:
        """Wait for a bot message containing needle. start_count may be a set of message ids to ignore
        (preferred) or, for compatibility, a count taken from len(bot_texts())."""
        if isinstance(start_count, int):
            ignore = {m["_id"] for m in self.bot_messages()[:start_count]}
        else:
            ignore = set(start_count or ())
        deadline = time.time() + timeout
        while time.time() < deadline:
            for m in self.bot_messages():
                if m["_id"] not in ignore and needle.lower() in (m.get("text") or "").lower():
                    return m["text"]
            time.sleep(1)
        raise AssertionError(f"bot never said '{needle}'. Last messages: {self.bot_texts()[-5:]}")

    # ------------------------------------------------------------------ game actions
    def item_event(self, actions: List[Dict[str, Any]]) -> Any:
        return self.call("/client/game/profile/items/moving", {"data": actions, "tm": 0, "reload": 0})

    def accept_quest(self, qid: str) -> Any:
        return self.item_event([{"Action": "QuestAccept", "qid": qid, "type": "PmcQuest"}])

    def complete_quest(self, qid: str) -> Any:
        return self.item_event([{"Action": "QuestComplete", "qid": qid, "removeExcessItems": True}])

    def locations(self) -> Dict[str, Any]:
        return self.call("/client/locations")

    def raid_start(self, location: str, side: str = "Pmc") -> Dict[str, Any]:
        return self.call("/client/match/local/start", {
            "serverId": "", "location": location, "timeVariant": "CURR", "mode": "PVE_OFFLINE",
            "playerSide": side, "transitionType": "None", "shouldSkipLootGeneration": True,
        })

    def raid_end(self, server_id: str, result: str, exit_name: Optional[str] = None,
                 killer: Optional[str] = None, profile: Optional[Dict[str, Any]] = None) -> Any:
        """End a raid. profile is the client's copy of the PMC/Scav at raid end; by default a fresh one
        (as if the client had just reloaded it). Pass an older copy to act like a client that did not."""
        if profile is None:
            profile = self.pmc()
        stats = profile.setdefault("Stats", {}).setdefault("Eft", {})
        if killer:
            stats["Aggressor"] = {"Name": killer, "Side": "Savage", "Role": "assault"}
        return self.call("/client/match/local/end", {
            "serverId": server_id,
            "results": {
                "profile": profile,
                "result": result,
                "killerId": None,
                "killerAid": None,
                "exitName": exit_name,
                "inSession": True,
                "favorite": False,
                "playTime": 600,
            },
            "lostInsuredItems": [],
            "transferItems": {},
            "locationTransit": None,
        })

    def poll_plugin(self) -> Dict[str, Any]:
        return self.call("/archipelago/client/poll", method="GET")
