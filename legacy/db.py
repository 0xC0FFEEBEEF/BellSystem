"""SchoolBell storage: SQLite schema, connection helper, settings access."""
import sqlite3
from pathlib import Path

DB_PATH = Path(__file__).resolve().parent / "schoolbell.db"

SCHEMA = """
CREATE TABLE IF NOT EXISTS schedules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    note TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS periods (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    schedule_id INTEGER NOT NULL REFERENCES schedules(id) ON DELETE CASCADE,
    pos INTEGER NOT NULL,
    name TEXT NOT NULL,
    start_hm TEXT NOT NULL,
    end_hm TEXT NOT NULL,
    warn_min INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS weekday_defaults (
    day INTEGER PRIMARY KEY CHECK (day BETWEEN 0 AND 6),
    schedule_id INTEGER REFERENCES schedules(id) ON DELETE SET NULL
);
CREATE TABLE IF NOT EXISTS overrides (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    date TEXT NOT NULL UNIQUE,
    schedule_id INTEGER REFERENCES schedules(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS holidays (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    date TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS clients (
    name TEXT PRIMARY KEY,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS bell_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts TEXT NOT NULL,
    client TEXT NOT NULL,
    date TEXT NOT NULL,
    time_hm TEXT NOT NULL,
    bell_type TEXT NOT NULL,
    period_num INTEGER,
    period_name TEXT
);
"""

DEFAULT_SETTINGS = {
    "school_name": "",
    "timezone": "",          # empty = use the host timezone
    "sound_warn": "chime.wav",
    "sound_start": "bell.wav",
    "sound_end": "chime.wav",
}


def connect() -> sqlite3.Connection:
    # check_same_thread=False: FastAPI runs sync endpoints (and the dependency
    # teardown) in a threadpool; each request gets its own connection anyway.
    conn = sqlite3.connect(DB_PATH, check_same_thread=False)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    conn.executescript(SCHEMA)
    return conn


def get_settings(conn) -> dict:
    rows = {r["key"]: r["value"] for r in conn.execute("SELECT key, value FROM settings")}
    out = dict(DEFAULT_SETTINGS)
    out.update(rows)
    return out


def set_setting(conn, key: str, value: str):
    conn.execute(
        "INSERT INTO settings (key, value) VALUES (?, ?) "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
        (key, value),
    )
