#!/usr/bin/env python3
"""SchoolBell classroom client.

Runs on a classroom PC (Windows, macOS or Linux), syncs the school's bell
schedule from the SchoolBell server, keeps a clock offset to the server,
plays the configured sound at each bell time, and reports each fired bell.

Usage:
    python schoolbell_client.py --server http://SERVER:8011 --name "Room 104"
    python schoolbell_client.py --server ... --name ... --once    (print today's bells and exit)

Built with the stdlib only (see requirements.txt for packaging extras).
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
import urllib.parse
import urllib.request
from datetime import datetime
from pathlib import Path

CACHE_DIR = Path(os.environ.get("SCHOOLBELL_CACHE", str(Path.home() / ".schoolbell")))
USER_AGENT = "SchoolBellClient/1.0"


def log(msg: str):
    stamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    print(f"[{stamp}] {msg}", flush=True)


# ---------- pure logic (unit-tested) ----------

def compute_delta(server_iso: str, local_epoch: float) -> float:
    """server_time - local_time, in seconds. Positive = server clock ahead."""
    return datetime.fromisoformat(server_iso).timestamp() - local_epoch


def local_fire_epoch(bell_dt_iso: str, delta: float) -> float:
    """Local epoch at which this server-scheduled bell should fire."""
    return datetime.fromisoformat(bell_dt_iso).timestamp() - delta


def bell_key(bell: dict) -> str:
    return f'{bell["date"] if "date" in bell else bell["dt"][:10]}|{bell["dt"]}|{bell["bell_type"]}|{bell.get("period_num")}'


def pending_bells(sync: dict, fired: set, now_epoch: float) -> list[tuple[float, dict]]:
    """(local_fire_epoch, bell) for all not-yet-fired bells in the sync payload.
    `now_epoch` must be the local clock reading taken when the sync arrived:
    it is used both for the server-offset delta and for the past/future filter."""
    delta = compute_delta(sync["server_time"], now_epoch)
    out = []
    for day in sync.get("days", []):
        for b in day.get("bells", []):
            b = dict(b)
            b.setdefault("date", day["date"])
            if bell_key(b) in fired:
                continue
            fire = local_fire_epoch(b["dt"], delta)
            if fire > now_epoch:
                out.append((fire, b))
    out.sort(key=lambda x: x[0])
    return out


# ---------- server access ----------

def http_json(url: str, payload: dict | None = None) -> dict:
    data = None
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=data, method="POST" if payload is not None else "GET",
                                 headers={"User-Agent": USER_AGENT, "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.loads(r.read().decode("utf-8"))


def fetch_sync(server: str, name: str) -> dict:
    url = f"{server.rstrip('/')}/api/client/sync?name={urllib.parse.quote(name)}"
    return http_json(url)


def report_fire(server: str, name: str, day_date: str, bell: dict):
    http_json(f"{server.rstrip('/')}/api/client/log", {
        "client": name,
        "date": day_date,
        "time_hm": bell["time_hm"],
        "bell_type": bell["bell_type"],
        "period_num": bell.get("period_num"),
        "period_name": bell.get("period_name"),
    })


# ---------- sound ----------

def download_sounds(server: str, sounds: dict):
    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    for kind, fname in sounds.items():
        path = CACHE_DIR / fname
        if not path.exists():
            url = f"{server.rstrip('/')}/sounds/{urllib.parse.quote(fname)}"
            req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(req, timeout=15) as r:
                path.write_bytes(r.read())
            log(f"downloaded sound {fname}")


def play_sound(fname: str):
    path = str(CACHE_DIR / fname)
    if not os.path.exists(path):
        log(f"WARN: sound file {fname} not cached")
        return
    try:
        if sys.platform == "win32":
            import winsound
            winsound.PlaySound(path, winsound.SND_FILENAME | winsound.SND_ASYNC)
        elif sys.platform == "darwin":
            subprocess.Popen(["afplay", path])
        else:
            for player in (["aplay", "-q"], ["paplay"]):
                try:
                    subprocess.Popen([player[0], path] if player[0] != "aplay" else [player[0], "-q", path])
                    return
                except FileNotFoundError:
                    continue
            log("no Linux audio player found — bell logged silently")
    except Exception as e:  # keep ringing schedule alive even if audio fails
        log(f"WARN: playback failed: {e}")


# ---------- main loop ----------

def run_once(server: str, name: str) -> int:
    sync = fetch_sync(server, name)
    tz = sync.get("timezone", "")
    log(f"connected to {server} ({sync.get('school_name') or 'school'}) — timezone {tz}")
    for day in sync["days"]:
        label = day["schedule_name"] or {"holiday": "holiday", "override": "override (no bells)"}.get(day["reason"], "no bells")
        print(f"{day['date']}  {label}")
        for b in day["bells"]:
            print(f"   {b['time_hm']}  {b['bell_type']:<5}  period {b['period_num']}: {b['period_name']}")
    return 0


def run_forever(server: str, name: str, interval: int) -> int:
    fired: set = set()
    sync = None
    backoff = interval
    log(f"starting; syncing with {server} as {name!r}")
    while True:
        try:
            sync = fetch_sync(server, name)
            backoff = interval
            download_sounds(server, sync.get("sounds", {}))
            sounds = sync.get("sounds", {})
            local_at_sync = time.time()
            bells = pending_bells(sync, fired, local_at_sync)
            log(f"synced; {len(bells)} bells armed")
            # ring everything due before the next planned re-sync
            deadline = time.time() + interval
            for fire_at, bell in bells:
                if fire_at > deadline:
                    break
                wait = fire_at - time.time()
                if wait > 0:
                    time.sleep(wait)
                play_sound(sounds.get(bell["bell_type"], "bell.wav"))
                fired.add(bell_key(bell))
                label = f"{bell['bell_type']} bell, period {bell['period_num']}: {bell['period_name']} @ {bell['time_hm']}"
                log(f"RANG {label}")
                try:
                    day_date = bell.get("date", bell["dt"][:10])
                    report_fire(server, name, day_date, bell)
                except Exception as e:
                    log(f"WARN: could not report bell to server: {e}")
            time.sleep(max(0.5, deadline - time.time()))
        except KeyboardInterrupt:
            log("stopped")
            return 0
        except Exception as e:
            log(f"sync failed: {e} (retry in {backoff}s)")
            time.sleep(backoff)
            backoff = min(backoff * 2, 120)


def main() -> int:
    ap = argparse.ArgumentParser(description="SchoolBell classroom bell client")
    ap.add_argument("--server", required=True, help="SchoolBell server URL, e.g. http://192.168.1.20:8011")
    ap.add_argument("--name", required=True, help="This room/device name, e.g. 'Room 104'")
    ap.add_argument("--interval", type=int, default=30, help="sync interval seconds (default 30)")
    ap.add_argument("--once", action="store_true", help="print today's and tomorrow's bells, then exit")
    args = ap.parse_args()
    try:
        if args.once:
            return run_once(args.server, args.name)
        return run_forever(args.server, args.name, max(5, args.interval))
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    sys.exit(main())
