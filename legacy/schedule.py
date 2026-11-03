"""SchoolBell schedule resolution and bell-time math.

Pure logic over a sqlite3 connection; no web-framework dependency so the
client and the tests can import it directly.

Day resolution priority: holiday > date override > weekday default.
An override whose schedule_id is NULL means "explicitly no bells today".
"""
from __future__ import annotations

import re
from dataclasses import dataclass, asdict
from datetime import date, datetime, timedelta
from zoneinfo import ZoneInfo

HM_RE = re.compile(r"^([01]?\d|2[0-3]):([0-5]\d)$")

BELL_TYPES = ("warn", "start", "end")


def parse_hm(hm: str) -> tuple[int, int]:
    m = HM_RE.match(hm.strip())
    if not m:
        raise ValueError(f"invalid time {hm!r}, expected HH:MM (24h)")
    return int(m.group(1)), int(m.group(2))


def hm_to_min(hm: str) -> int:
    h, mi = parse_hm(hm)
    return h * 60 + mi


def resolve_schedule_id(conn, d: date) -> tuple[str | None, int | None]:
    """Return (reason, schedule_id) for a calendar date. reason is
    'holiday', 'override', 'weekday' or 'none'; schedule_id None = no bells."""
    hol = conn.execute("SELECT name FROM holidays WHERE date = ?", (d.isoformat(),)).fetchone()
    if hol:
        return "holiday", None
    ovr = conn.execute(
        "SELECT schedule_id FROM overrides WHERE date = ?", (d.isoformat(),)
    ).fetchone()
    if ovr:
        return "override", ovr["schedule_id"]
    row = conn.execute(
        "SELECT schedule_id FROM weekday_defaults WHERE day = ?", (d.weekday(),)
    ).fetchone()
    if row and row["schedule_id"] is not None:
        return "weekday", row["schedule_id"]
    return "none", None


@dataclass
class Bell:
    bell_type: str          # warn | start | end
    time_hm: str            # HH:MM
    period_num: int
    period_name: str
    schedule_id: int

    def as_dict(self):
        return asdict(self)


def bells_for_schedule(conn, schedule_id: int) -> list[Bell]:
    rows = conn.execute(
        "SELECT row_number() OVER (ORDER BY start_hm, id) AS num, "
        "name, start_hm, end_hm, warn_min FROM periods "
        "WHERE schedule_id = ? ORDER BY start_hm, id",
        (schedule_id,),
    ).fetchall()
    bells: list[Bell] = []
    for r in rows:
        start_min = hm_to_min(r["start_hm"])
        if r["warn_min"] and start_min >= r["warn_min"]:
            w = start_min - r["warn_min"]
            bells.append(Bell("warn", f"{w // 60:02d}:{w % 60:02d}", r["num"], r["name"], schedule_id))
        bells.append(Bell("start", r["start_hm"], r["num"], r["name"], schedule_id))
        bells.append(Bell("end", r["end_hm"], r["num"], r["name"], schedule_id))
    bells.sort(key=lambda b: (hm_to_min(b.time_hm), BELL_TYPES.index(b.bell_type)))
    return bells


def bells_for_datetime(conn, d: date) -> tuple[str | None, list[dict]]:
    """Resolved bells for one date, each with an ISO datetime in tz."""
    reason, sid = resolve_schedule_id(conn, d)
    out: list[dict] = []
    schedule_name = None
    if sid is not None:
        srow = conn.execute("SELECT name FROM schedules WHERE id = ?", (sid,)).fetchone()
        schedule_name = srow["name"] if srow else None
        tzname = get_effective_timezone(conn)
        tz = ZoneInfo(tzname)
        for b in bells_for_schedule(conn, sid):
            h, mi = parse_hm(b.time_hm)
            dt = datetime(d.year, d.month, d.day, h, mi, tzinfo=tz)
            item = b.as_dict()
            item["reason"] = reason
            item["dt"] = dt.isoformat()
            out.append(item)
    return schedule_name, out


def get_effective_timezone(conn) -> str:
    from db import get_settings

    tzname = get_settings(conn)["timezone"].strip()
    if tzname:
        return tzname
    return host_timezone()


def host_timezone() -> str:
    import os

    try:
        link = os.path.realpath("/etc/localtime")
        marker = "zoneinfo/"
        i = link.find(marker)
        if i >= 0:
            return link[i + len(marker):]
    except OSError:
        pass
    return "UTC"


def upcoming_bells(conn, now: datetime, days: int = 3, limit: int = 12) -> list[dict]:
    """The next bells from `now` (tz-aware) looking ahead `days` calendar days."""
    out: list[dict] = []
    d = now.date()
    for _ in range(days):
        _, bells = bells_for_datetime(conn, d)
        for b in bells:
            dt = datetime.fromisoformat(b["dt"])
            if dt >= now:
                out.append(b)
        d = d + timedelta(days=1)
    out.sort(key=lambda b: b["dt"])
    return out[:limit]
