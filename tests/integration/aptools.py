"""Helpers to generate a multiworld, host it with the real Archipelago MultiServer and watch it."""
from __future__ import annotations

import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import uuid
import zipfile
from typing import Any, Dict, List, Optional

import yaml
from websockets.sync.client import connect as ws_connect

AP_DIR = os.environ.get("AP_DIR", "Archipelago")  # an Archipelago checkout with this world in worlds/ or custom_worlds/
PYTHON = os.environ.get("AP_PYTHON", sys.executable)


def generate(players: Dict[str, Dict[str, Any]], workdir: str, seed: Optional[int] = None) -> str:
    """Generate a multiworld; returns the path of the .archipelago file."""
    if os.path.exists(workdir):
        shutil.rmtree(workdir)
    pdir = os.path.join(workdir, "players")
    odir = os.path.join(workdir, "out")
    os.makedirs(pdir)
    for name, opts in players.items():
        game = opts.pop("game", "Escape from Tarkov")
        with open(os.path.join(pdir, f"{name}.yaml"), "w") as f:
            yaml.safe_dump({"name": name, "game": game, game: opts}, f)
    cmd = [PYTHON, "Generate.py", "--player_files_path", pdir, "--outputpath", odir]
    if seed is not None:
        cmd += ["--seed", str(seed)]
    env = dict(os.environ, SKIP_REQUIREMENTS_UPDATE="1")
    proc = subprocess.run(cmd, cwd=AP_DIR, env=env, capture_output=True, text=True, stdin=subprocess.DEVNULL, timeout=900)
    if proc.returncode != 0:
        raise RuntimeError(f"generation failed:\n{proc.stdout[-3000:]}\n{proc.stderr[-3000:]}")
    zips = glob.glob(os.path.join(odir, "*.zip"))
    if not zips:
        raise RuntimeError("no output zip")
    with zipfile.ZipFile(zips[0]) as z:
        z.extractall(odir)
    return glob.glob(os.path.join(odir, "*.archipelago"))[0]


def patch_multidata(src: str, dst: str, slot_data_changes: Dict[str, Any]) -> str:
    """Copy a .archipelago file, changing every Escape from Tarkov slot's slot_data (AP's own loader/writer)."""
    script = (
        "import sys, zlib, pickle, json\n"
        "sys.path.insert(0, '.')\n"
        "src, dst, changes = sys.argv[1], sys.argv[2], json.loads(sys.argv[3])\n"
        "raw = open(src, 'rb').read()\n"
        "data = pickle.loads(zlib.decompress(raw[1:]))\n"
        "for slot, info in data['slot_info'].items():\n"
        "    if info.game == 'Escape from Tarkov':\n"
        "        data['slot_data'][slot].update(changes)\n"
        "open(dst, 'wb').write(raw[:1] + zlib.compress(pickle.dumps(data), 9))\n"
    )
    env = dict(os.environ, SKIP_REQUIREMENTS_UPDATE="1")
    proc = subprocess.run([PYTHON, "-c", script, src, dst, json.dumps(slot_data_changes)], cwd=AP_DIR, env=env,
                          capture_output=True, text=True, timeout=300)
    if proc.returncode != 0:
        raise RuntimeError(f"patching multidata failed:\n{proc.stderr[-3000:]}")
    return dst


class MultiServer:
    def __init__(self, multidata: str, port: int = 38281, logfile: str = os.path.join(tempfile.gettempdir(), "multiserver.log")) -> None:
        self.multidata = multidata
        self.port = port
        self.logfile = logfile
        self.proc: Optional[subprocess.Popen] = None

    @staticmethod
    def kill_stale(port: int) -> None:
        """Kill MultiServer processes left over from an earlier run (matched on argv, never on our own pid)."""
        me = os.getpid()
        for pid in os.listdir("/proc"):
            if not pid.isdigit() or int(pid) == me:
                continue
            try:
                argv = open(f"/proc/{pid}/cmdline", "rb").read().split(b"\0")
            except OSError:
                continue
            args = [a.decode(errors="replace") for a in argv]
            if len(args) > 3 and args[1].endswith("MultiServer.py") and "--port" in args and str(port) in args:
                try:
                    os.kill(int(pid), 15)
                except OSError:
                    pass

    def start(self) -> None:
        env = dict(os.environ, SKIP_REQUIREMENTS_UPDATE="1")
        self.kill_stale(self.port)
        for _ in range(20):
            if not self._port_open():
                break
            time.sleep(0.5)
        open(self.logfile, "w").close()
        self._log = open(self.logfile, "a")
        self.proc = subprocess.Popen(
            [PYTHON, "MultiServer.py", "--port", str(self.port), "--host", "127.0.0.1",
             "--disable_save", "--loglevel", "info", self.multidata],
            cwd=AP_DIR, env=env, stdin=subprocess.PIPE, stdout=self._log, stderr=subprocess.STDOUT, text=True)
        deadline = time.time() + 120
        while time.time() < deadline:
            time.sleep(1)
            if self.proc.poll() is not None:
                raise RuntimeError("MultiServer exited:\n" + open(self.logfile).read()[-3000:])
            if self._port_open():
                time.sleep(1)
                if self.proc.poll() is None:
                    return
            if self.proc.poll() is not None:
                raise RuntimeError("MultiServer exited:\n" + open(self.logfile).read()[-3000:])
        raise RuntimeError("MultiServer did not start")

    def _port_open(self) -> bool:
        import socket
        with socket.socket() as s:
            s.settimeout(0.5)
            return s.connect_ex(("127.0.0.1", self.port)) == 0

    def command(self, text: str) -> None:
        assert self.proc and self.proc.stdin
        self.proc.stdin.write(text + "\n")
        self.proc.stdin.flush()

    def stop(self) -> None:
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=15)
            except subprocess.TimeoutExpired:
                self.proc.kill()
        self.proc = None


class Watcher:
    """Connects to a slot like a tracker would and records everything the server sends."""

    def __init__(self, slot: str, port: int = 38281, tags: Optional[List[str]] = None) -> None:
        self.slot = slot
        self.port = port
        self.tags = tags or ["Tracker"]
        self.packets: List[Dict[str, Any]] = []
        self.checked: set = set()
        self.slot_id: Optional[int] = None
        self._ws = None
        self._thread: Optional[threading.Thread] = None
        self._stop = False
        self.lock = threading.Lock()

    def start(self) -> "Watcher":
        self._ws = ws_connect(f"ws://127.0.0.1:{self.port}", max_size=None, open_timeout=20)
        room = json.loads(self._ws.recv(timeout=20))
        self.packets.extend(room)
        self._ws.send(json.dumps([{
            "cmd": "Connect", "password": "", "game": "", "name": self.slot, "uuid": str(uuid.uuid4()),
            "version": {"major": 0, "minor": 6, "build": 4, "class": "Version"},
            "items_handling": 0, "tags": self.tags, "slot_data": False,
        }]))
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()
        deadline = time.time() + 20
        while self.slot_id is None and time.time() < deadline:
            time.sleep(0.2)
        if self.slot_id is None:
            raise RuntimeError(f"watcher could not connect: {self.packets[-3:]}")
        return self

    def _loop(self) -> None:
        while not self._stop:
            try:
                msg = self._ws.recv(timeout=1)
            except TimeoutError:
                continue
            except Exception:
                break
            for packet in json.loads(msg):
                with self.lock:
                    self.packets.append(packet)
                    if packet.get("cmd") == "Connected":
                        self.slot_id = packet["slot"]
                        self.checked.update(packet.get("checked_locations", []))
                    elif packet.get("cmd") == "RoomUpdate" and "checked_locations" in packet:
                        self.checked.update(packet["checked_locations"])

    def send(self, packets: List[Dict[str, Any]]) -> None:
        self._ws.send(json.dumps(packets))

    def wait_checked(self, location_id: int, timeout: float = 30) -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            with self.lock:
                if location_id in self.checked:
                    return
            time.sleep(0.5)
        raise AssertionError(f"location {location_id} was never checked")

    def find(self, predicate, since: int = 0) -> List[Dict[str, Any]]:
        with self.lock:
            return [p for p in self.packets[since:] if predicate(p)]

    def wait_for(self, predicate, timeout: float = 30, since: int = 0) -> Dict[str, Any]:
        deadline = time.time() + timeout
        while time.time() < deadline:
            hits = self.find(predicate, since)
            if hits:
                return hits[0]
            time.sleep(0.5)
        raise AssertionError("expected packet never arrived")

    def mark(self) -> int:
        with self.lock:
            return len(self.packets)

    def reconnect(self) -> "Watcher":
        """Reconnect after the server restarted (keeps already recorded packets)."""
        self.close()
        self._stop = False
        self.slot_id = None
        return self.start()

    def close(self) -> None:
        self._stop = True
        try:
            self._ws.close()
        except Exception:
            pass
