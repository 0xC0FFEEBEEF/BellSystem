"""SchoolBell server: bell-schedule admin API + web UI + client sync."""
from __future__ import annotations

import sqlite3
from datetime import date, datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

from fastapi import Depends, FastAPI, HTTPException, Query
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

import db
import schedule as sched

BASE_DIR = Path(__file__).resolve().parent
VALID_SOUNDS = {"chime.wav", "bell.wav", "buzzer.wav"}

app = FastAPI(title="SchoolBell")


def now_tz(conn) -> datetime:
    return datetime.now(ZoneInfo(sched.get_effective_timezone(conn)))


def get_db():
    conn = db.connect()
    try:
        yield conn
    finally:
        conn.close()


# ---------- request models ----------

class ScheduleIn(BaseModel):
    name: str
    note: str = ""

class PeriodIn(BaseModel):
    name: str
    start_hm: str
    end_hm: str
    warn_min: int = 0

class PeriodsIn(BaseModel):
    periods: list[PeriodIn]

class CalendarIn(BaseModel):
    weekdays: list[int | None]  # 7 entries, Mon..Sun

class HolidayIn(BaseModel):
    date: str
    name: str

class OverrideIn(BaseModel):
    date: str
    schedule_id: int | None

class SettingsIn(BaseModel):
    school_name: str | None = None
    timezone: str | None = None
    sound_warn: str | None = None
    sound_start: str | None = None
    sound_end: str | None = None

class LogIn(BaseModel):
    client: str
    date: str
    time_hm: str
    bell_type: str
    period_num: int | None = None
    period_name: str | None = None


# ---------- validation helpers ----------

def parse_date(s: str) -> date:
    try:
        d = date.fromisoformat(s)
    except ValueError:
        raise HTTPException(400, f"invalid date {s!r}, expected YYYY-MM-DD")
    if d.isoformat() != s:
        raise HTTPException(400, f"invalid date {s!r}, expected YYYY-MM-DD")
    return d

def require_schedule(conn, sid: int):
    row = conn.execute("SELECT id FROM schedules WHERE id = ?", (sid,)).fetchone()
    if not row:
        raise HTTPException(404, f"schedule {sid} not found")

def validate_periods(periods: list[PeriodIn]):
    if not periods:
        raise HTTPException(400, "a schedule needs at least one period")
    for i, p in enumerate(periods, 1):
        try:
            start = sched.hm_to_min(p.start_hm)
            end = sched.hm_to_min(p.end_hm)
        except ValueError as e:
            raise HTTPException(400, f"period {i}: {e}")
        if not p.name.strip():
            raise HTTPException(400, f"period {i}: name required")
        if end <= start:
            raise HTTPException(400, f"period {p.name!r}: end time must be after start time")
        if not (0 <= p.warn_min <= 240):
            raise HTTPException(400, f"period {p.name!r}: warn_min must be 0-240")


# ---------- full state for the UI ----------

@app.get("/api/state")
def api_state(conn=Depends(get_db)):
    schedules = []
    for s in conn.execute("SELECT * FROM schedules ORDER BY name").fetchall():
        periods = conn.execute(
            "SELECT id, name, start_hm, end_hm, warn_min FROM periods "
            "WHERE schedule_id = ? ORDER BY start_hm, id", (s["id"],),
        ).fetchall()
        schedules.append({**dict(s), "periods": [dict(p) for p in periods]})
    wd = [None] * 7
    for r in conn.execute("SELECT day, schedule_id FROM weekday_defaults"):
        wd[r["day"]] = r["schedule_id"]
    overrides = [dict(r) for r in conn.execute(
        "SELECT o.id, o.date, o.schedule_id, s.name AS schedule_name FROM overrides o "
        "LEFT JOIN schedules s ON s.id = o.schedule_id ORDER BY o.date")]
    holidays = [dict(r) for r in conn.execute("SELECT * FROM holidays ORDER BY date")]
    clients = client_rows(conn)
    log = [dict(r) for r in conn.execute(
        "SELECT * FROM bell_log ORDER BY id DESC LIMIT 50")]
    settings = db.get_settings(conn)
    tzname = sched.get_effective_timezone(conn)
    today = now_tz(conn)
    return {
        "settings": settings,
        "timezone": tzname,
        "server_time": today.isoformat(),
        "schedules": schedules,
        "weekday_defaults": wd,
        "overrides": overrides,
        "holidays": holidays,
        "clients": clients,
        "log": log,
        "valid_sounds": sorted(VALID_SOUNDS),
    }


def client_rows(conn) -> list[dict]:
    now = now_tz(conn)
    out = []
    for r in conn.execute("SELECT * FROM clients ORDER BY name").fetchall():
        last = datetime.fromisoformat(r["last_seen"])
        out.append({**dict(r), "online": (now - last).total_seconds() <= 90})
    return out


# ---------- schedules / periods CRUD ----------

@app.post("/api/schedules")
def create_schedule(body: ScheduleIn, conn=Depends(get_db)):
    if not body.name.strip():
        raise HTTPException(400, "schedule name required")
    try:
        cur = conn.execute("INSERT INTO schedules (name, note) VALUES (?, ?)",
                           (body.name.strip(), body.note.strip()))
    except sqlite3.IntegrityError:
        raise HTTPException(409, f"schedule {body.name!r} already exists")
    conn.commit()
    return {"id": cur.lastrowid}

@app.put("/api/schedules/{sid}")
def update_schedule(sid: int, body: ScheduleIn, conn=Depends(get_db)):
    require_schedule(conn, sid)
    try:
        conn.execute("UPDATE schedules SET name = ?, note = ? WHERE id = ?",
                     (body.name.strip(), body.note.strip(), sid))
    except sqlite3.IntegrityError:
        raise HTTPException(409, f"schedule {body.name!r} already exists")
    conn.commit()
    return {"ok": True}

@app.delete("/api/schedules/{sid}")
def delete_schedule(sid: int, conn=Depends(get_db)):
    require_schedule(conn, sid)
    conn.execute("DELETE FROM schedules WHERE id = ?", (sid,))
    conn.commit()
    return {"ok": True}

@app.put("/api/schedules/{sid}/periods")
def replace_periods(sid: int, body: PeriodsIn, conn=Depends(get_db)):
    require_schedule(conn, sid)
    validate_periods(body.periods)
    conn.execute("DELETE FROM periods WHERE schedule_id = ?", (sid,))
    for i, p in enumerate(body.periods):
        conn.execute(
            "INSERT INTO periods (schedule_id, pos, name, start_hm, end_hm, warn_min) "
            "VALUES (?, ?, ?, ?, ?, ?)",
            (sid, i, p.name.strip(),
             f"{sched.parse_hm(p.start_hm)[0]:02d}:{sched.parse_hm(p.start_hm)[1]:02d}",
             f"{sched.parse_hm(p.end_hm)[0]:02d}:{sched.parse_hm(p.end_hm)[1]:02d}",
             p.warn_min),
        )
    conn.commit()
    return {"ok": True}


# ---------- calendar ----------

@app.put("/api/calendar")
def set_calendar(body: CalendarIn, conn=Depends(get_db)):
    if len(body.weekdays) != 7:
        raise HTTPException(400, "weekdays must have 7 entries (Mon..Sun)")
    for sid in body.weekdays:
        if sid is not None:
            require_schedule(conn, sid)
    conn.execute("DELETE FROM weekday_defaults")
    for day, sid in enumerate(body.weekdays):
        if sid is not None:
            conn.execute("INSERT INTO weekday_defaults (day, schedule_id) VALUES (?, ?)", (day, sid))
    conn.commit()
    return {"ok": True}

@app.post("/api/holidays")
def add_holiday(body: HolidayIn, conn=Depends(get_db)):
    parse_date(body.date)
    if not body.name.strip():
        raise HTTPException(400, "holiday name required")
    try:
        cur = conn.execute("INSERT INTO holidays (date, name) VALUES (?, ?)",
                           (body.date, body.name.strip()))
    except sqlite3.IntegrityError:
        raise HTTPException(409, f"a holiday already exists on {body.date}")
    conn.commit()
    return {"id": cur.lastrowid}

@app.delete("/api/holidays/{hid}")
def delete_holiday(hid: int, conn=Depends(get_db)):
    conn.execute("DELETE FROM holidays WHERE id = ?", (hid,))
    conn.commit()
    return {"ok": True}

@app.post("/api/overrides")
def add_override(body: OverrideIn, conn=Depends(get_db)):
    parse_date(body.date)
    if body.schedule_id is not None:
        require_schedule(conn, body.schedule_id)
    conn.execute(
        "INSERT INTO overrides (date, schedule_id) VALUES (?, ?) "
        "ON CONFLICT(date) DO UPDATE SET schedule_id = excluded.schedule_id",
        (body.date, body.schedule_id),
    )
    conn.commit()
    return {"ok": True}

@app.delete("/api/overrides/{oid}")
def delete_override(oid: int, conn=Depends(get_db)):
    conn.execute("DELETE FROM overrides WHERE id = ?", (oid,))
    conn.commit()
    return {"ok": True}


# ---------- settings ----------

@app.put("/api/settings")
def update_settings(body: SettingsIn, conn=Depends(get_db)):
    if body.timezone is not None:
        tz = body.timezone.strip()
        if tz:
            try:
                ZoneInfo(tz)
            except Exception:
                raise HTTPException(400, f"unknown timezone {tz!r}")
        db.set_setting(conn, "timezone", tz)
    for key in ("school_name", "sound_warn", "sound_start", "sound_end"):
        val = getattr(body, key)
        if val is not None:
            if key.startswith("sound_") and val not in VALID_SOUNDS:
                raise HTTPException(400, f"unknown sound {val!r}")
            db.set_setting(conn, key, val.strip())
    conn.commit()
    return db.get_settings(conn)


# ---------- day / next / sync ----------

@app.get("/api/day")
def api_day(date_str: str | None = Query(None, alias="date"), conn=Depends(get_db)):
    d = parse_date(date_str) if date_str else now_tz(conn).date()
    reason, sid = sched.resolve_schedule_id(conn, d)
    name, bells = sched.bells_for_datetime(conn, d)
    return {"date": d.isoformat(), "reason": reason,
            "schedule_id": sid, "schedule_name": name, "bells": bells}

@app.get("/api/next")
def api_next(limit: int = 12, conn=Depends(get_db)):
    now = now_tz(conn)
    return {
        "server_time": now.isoformat(),
        "timezone": sched.get_effective_timezone(conn),
        "bells": sched.upcoming_bells(conn, now, days=5, limit=limit),
    }


# ---------- client endpoints ----------

@app.get("/api/client/sync")
def client_sync(name: str = Query(min_length=1, max_length=80), conn=Depends(get_db)):
    now = now_tz(conn)
    iso = now.isoformat()
    conn.execute(
        "INSERT INTO clients (name, first_seen, last_seen) VALUES (?, ?, ?) "
        "ON CONFLICT(name) DO UPDATE SET last_seen = excluded.last_seen",
        (name.strip(), iso, iso),
    )
    conn.commit()
    settings = db.get_settings(conn)
    days = []
    for d in [now.date(), now.date() + timedelta(days=1)]:
        sname, bells = sched.bells_for_datetime(conn, d)
        reason, _ = sched.resolve_schedule_id(conn, d)
        days.append({"date": d.isoformat(), "reason": reason,
                     "schedule_name": sname, "bells": bells})
    return {
        "server_time": iso,
        "timezone": sched.get_effective_timezone(conn),
        "school_name": settings["school_name"],
        "sounds": {"warn": settings["sound_warn"],
                   "start": settings["sound_start"],
                   "end": settings["sound_end"]},
        "days": days,
    }

@app.post("/api/client/log")
def client_log(body: LogIn, conn=Depends(get_db)):
    parse_date(body.date)
    try:
        sched.parse_hm(body.time_hm)
    except ValueError as e:
        raise HTTPException(400, str(e))
    if body.bell_type not in sched.BELL_TYPES:
        raise HTTPException(400, f"bell_type must be one of {sched.BELL_TYPES}")
    conn.execute(
        "INSERT INTO bell_log (ts, client, date, time_hm, bell_type, period_num, period_name) "
        "VALUES (?, ?, ?, ?, ?, ?, ?)",
        (now_tz(conn).isoformat(), body.client.strip()[:80], body.date,
         body.time_hm, body.bell_type, body.period_num, body.period_name),
    )
    conn.commit()
    return {"ok": True}

@app.get("/api/log")
def get_log(limit: int = 50, conn=Depends(get_db)):
    limit = max(1, min(limit, 500))
    return {"log": [dict(r) for r in conn.execute(
        "SELECT * FROM bell_log ORDER BY id DESC LIMIT ?", (limit,))]}

@app.get("/api/clients")
def get_clients(conn=Depends(get_db)):
    return {"clients": client_rows(conn)}

@app.get("/api/health")
def health(conn=Depends(get_db)):
    return {"ok": True, "time": now_tz(conn).isoformat()}


# ---------- web UI (relative paths only, for the preview) ----------

@app.get("/")
def index():
    return FileResponse(BASE_DIR / "index.html")

app.mount("/static", StaticFiles(directory=BASE_DIR / "static"), name="static")
app.mount("/sounds", StaticFiles(directory=BASE_DIR / "sounds"), name="sounds")
