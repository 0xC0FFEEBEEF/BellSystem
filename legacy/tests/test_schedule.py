"""Tests for schedule resolution and bell-time math (schedule.py)."""
import sqlite3
from datetime import date, datetime

import pytest

import db
import schedule


def make_conn(tz="America/New_York"):
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    conn.executescript(db.SCHEMA)
    db.set_setting(conn, "timezone", tz)
    return conn


def add_schedule(conn, name, periods):
    cur = conn.execute("INSERT INTO schedules (name) VALUES (?)", (name,))
    sid = cur.lastrowid
    for i, (pname, start, end, warn) in enumerate(periods):
        conn.execute(
            "INSERT INTO periods (schedule_id, pos, name, start_hm, end_hm, warn_min) "
            "VALUES (?, ?, ?, ?, ?, ?)",
            (sid, i, pname, start, end, warn),
        )
    conn.commit()
    return sid


# ---- time parsing ----

def test_parse_hm_valid():
    assert schedule.parse_hm("8:05") == (8, 5)
    assert schedule.parse_hm("23:59") == (23, 59)

@pytest.mark.parametrize("bad", ["24:00", "12:60", "9:5", "", "abc", "0900"])
def test_parse_hm_invalid(bad):
    with pytest.raises(ValueError):
        schedule.parse_hm(bad)


# ---- resolution priority ----

def test_weekday_default_used():
    conn = make_conn()
    sid = add_schedule(conn, "Regular Day", [("P1", "08:00", "08:50", 0)])
    conn.execute("INSERT INTO weekday_defaults (day, schedule_id) VALUES (0, ?)", (sid,))
    reason, resolved = schedule.resolve_schedule_id(conn, date(2026, 11, 2))  # Monday
    assert (reason, resolved) == ("weekday", sid)

def test_holiday_beats_everything():
    conn = make_conn()
    sid = add_schedule(conn, "Regular Day", [("P1", "08:00", "08:50", 0)])
    conn.execute("INSERT INTO weekday_defaults (day, schedule_id) VALUES (4, ?)", (sid,))
    conn.execute("INSERT INTO overrides (date, schedule_id) VALUES ('2026-11-06', ?)", (sid,))
    conn.execute("INSERT INTO holidays (date, name) VALUES ('2026-11-06', 'Veterans Day')")
    reason, resolved = schedule.resolve_schedule_id(conn, date(2026, 11, 6))
    assert (reason, resolved) == ("holiday", None)

def test_override_beats_weekday_and_can_mean_no_bells():
    conn = make_conn()
    regular = add_schedule(conn, "Regular Day", [("P1", "08:00", "08:50", 0)])
    early = add_schedule(conn, "Early Release", [("P1", "08:00", "12:30", 0)])
    conn.execute("INSERT INTO weekday_defaults (day, schedule_id) VALUES (0, ?)", (regular,))
    conn.execute("INSERT INTO overrides (date, schedule_id) VALUES ('2026-11-02', ?)", (early,))
    assert schedule.resolve_schedule_id(conn, date(2026, 11, 2))[1] == early
    conn.execute("INSERT INTO overrides (date, schedule_id) VALUES ('2026-11-03', NULL)")
    reason, resolved = schedule.resolve_schedule_id(conn, date(2026, 11, 3))
    assert (reason, resolved) == ("override", None)

def test_day_with_no_assignment():
    conn = make_conn()
    reason, resolved = schedule.resolve_schedule_id(conn, date(2026, 11, 1))  # Sunday, unset
    assert (reason, resolved) == ("none", None)


# ---- bell generation ----

def test_bell_order_and_warn():
    conn = make_conn()
    sid = add_schedule(conn, "Regular", [("Period 1", "08:00", "08:50", 5),
                                         ("Period 2", "09:00", "09:50", 0)])
    bells = schedule.bells_for_schedule(conn, sid)
    seq = [(b.bell_type, b.time_hm, b.period_num) for b in bells]
    assert seq == [
        ("warn", "07:55", 1), ("start", "08:00", 1), ("end", "08:50", 1),
        ("start", "09:00", 2), ("end", "09:50", 2),
    ]

def test_warn_bell_not_before_midnight():
    conn = make_conn()
    sid = add_schedule(conn, "Odd", [("Very early", "00:05", "01:00", 10)])
    bells = schedule.bells_for_schedule(conn, sid)
    assert [(b.bell_type, b.time_hm) for b in bells] == [("start", "00:05"), ("end", "01:00")]

def test_same_time_start_before_end():
    conn = make_conn()
    sid = add_schedule(conn, "Back to back", [("A", "08:00", "09:00", 0), ("B", "09:00", "10:00", 0)])
    bells = schedule.bells_for_schedule(conn, sid)
    same_time = [b.bell_type for b in bells if b.time_hm == "09:00"]
    assert same_time == ["start", "end"]


# ---- upcoming window ----

def test_upcoming_skips_to_next_schoolday_across_weekend():
    conn = make_conn()
    sid = add_schedule(conn, "Regular", [("P1", "08:00", "08:50", 5)])
    for day in range(5):  # Mon-Fri
        conn.execute("INSERT INTO weekday_defaults (day, schedule_id) VALUES (?, ?)", (day, sid))
    conn.commit()
    now = datetime(2026, 10, 30, 15, 0, tzinfo=schedule.ZoneInfo("America/New_York"))  # Friday 15:00
    nxt = schedule.upcoming_bells(conn, now, days=5, limit=3)
    assert [b["dt"] for b in nxt] == [
        "2026-11-02T07:55:00-05:00",  # Monday warn
        "2026-11-02T08:00:00-05:00",
        "2026-11-02T08:50:00-05:00",
    ]

def test_upcoming_excludes_already_passed_today():
    conn = make_conn()
    sid = add_schedule(conn, "Regular", [("P1", "08:00", "08:50", 0), ("P2", "09:00", "09:50", 0)])
    conn.execute("INSERT INTO weekday_defaults (day, schedule_id) VALUES (0, ?)", (sid,))
    conn.commit()
    now = datetime(2026, 11, 2, 8, 30, tzinfo=schedule.ZoneInfo("America/New_York"))
    nxt = schedule.upcoming_bells(conn, now, days=2, limit=10)
    assert [(b["bell_type"], b["time_hm"]) for b in nxt] == [
        ("end", "08:50"), ("start", "09:00"), ("end", "09:50"),
    ]
