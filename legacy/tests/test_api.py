"""API tests for app.py using FastAPI TestClient with an isolated temp DB."""
import pytest
from fastapi.testclient import TestClient

import db


@pytest.fixture()
def client(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "test.db")
    import app
    with TestClient(app.app) as c:
        yield c


def create_schedule(c, name, periods):
    r = c.post("/api/schedules", json={"name": name})
    assert r.status_code == 200, r.text
    sid = r.json()["id"]
    r = c.put(f"/api/schedules/{sid}/periods", json={"periods": periods})
    assert r.status_code == 200, r.text
    return sid


REGULAR = [{"name": "Period 1", "start_hm": "08:00", "end_hm": "08:50", "warn_min": 5},
           {"name": "Period 2", "start_hm": "09:00", "end_hm": "09:50", "warn_min": 0}]


def test_state_starts_empty(client):
    s = client.get("/api/state").json()
    assert s["schedules"] == [] and s["holidays"] == [] and s["overrides"] == []
    assert s["weekday_defaults"] == [None] * 7
    assert s["settings"]["school_name"] == ""
    assert s["timezone"]  # effective tz always present


def test_schedule_crud_and_validation(client):
    sid = create_schedule(client, "Regular Day", REGULAR)
    r = client.post("/api/schedules", json={"name": "Regular Day"})
    assert r.status_code == 409
    r = client.put(f"/api/schedules/{sid}/periods",
                   json={"periods": [{"name": "X", "start_hm": "09:00", "end_hm": "08:00", "warn_min": 0}]})
    assert r.status_code == 400 and "after start" in r.json()["detail"]
    r = client.put(f"/api/schedules/{sid}/periods",
                   json={"periods": [{"name": "X", "start_hm": "9:00", "end_hm": "10:00", "warn_min": 300}]})
    assert r.status_code == 400
    r = client.put(f"/api/schedules/{sid}", json={"name": "Regular Day A"})
    assert r.status_code == 200
    state = client.get("/api/state").json()
    names = [s["name"] for s in state["schedules"]]
    assert "Regular Day A" in names and "Regular Day" not in names
    # rejected PUTs above must not have destroyed the original periods
    periods = state["schedules"][0]["periods"]
    assert len(periods) == 2 and periods[0]["start_hm"] == "08:00"
    assert client.delete(f"/api/schedules/{sid}").status_code == 200
    assert client.get("/api/state").json()["schedules"] == []


def test_calendar_holidays_overrides_and_day(client):
    sid = create_schedule(client, "Regular", REGULAR)
    early = create_schedule(client, "Early Release",
                            [{"name": "AM", "start_hm": "08:00", "end_hm": "11:00", "warn_min": 0}])
    r = client.put("/api/calendar", json={"weekdays": [sid, sid, None, None, None, None, None]})
    assert r.status_code == 200
    day = client.get("/api/day", params={"date": "2026-11-02"}).json()  # Monday
    assert day["schedule_id"] == sid
    assert [(b["bell_type"], b["time_hm"]) for b in day["bells"]] == [
        ("warn", "07:55"), ("start", "08:00"), ("end", "08:50"),
        ("start", "09:00"), ("end", "09:50")]
    assert client.get("/api/day", params={"date": "2026-11-01"}).json()["bells"] == []  # Sunday

    client.post("/api/overrides", json={"date": "2026-11-02", "schedule_id": early})
    day = client.get("/api/day", params={"date": "2026-11-02"}).json()
    assert day["schedule_id"] == early and day["reason"] == "override"

    client.post("/api/holidays", json={"date": "2026-11-02", "name": "Test Day"})
    day = client.get("/api/day", params={"date": "2026-11-02"}).json()
    assert day["reason"] == "holiday" and day["bells"] == []

    r = client.post("/api/holidays", json={"date": "2026-11-02", "name": "Again"})
    assert r.status_code == 409
    assert client.get("/api/day", params={"date": "notadate"}).status_code == 400


def test_settings_validation(client):
    r = client.put("/api/settings", json={"school_name": "Elm Street School", "timezone": "Nowhere/Place"})
    assert r.status_code == 400
    r = client.put("/api/settings", json={"timezone": "Nowhere/Place"})
    assert r.status_code == 400
    r = client.put("/api/settings", json={"school_name": "Elm Street School", "timezone": "America/Denver"})
    assert r.status_code == 200 and r.json()["timezone"] == "America/Denver"
    assert client.get("/api/state").json()["timezone"] == "America/Denver"
    r = client.put("/api/settings", json={"sound_start": "nonexistent.wav"})
    assert r.status_code == 400


def test_client_sync_log_and_clients(client):
    sid = create_schedule(client, "Regular", REGULAR)
    client.put("/api/calendar", json={"weekdays": [sid] * 5 + [None, None]})
    r = client.get("/api/client/sync", params={"name": "Room 104"})
    assert r.status_code == 200
    sync = r.json()
    assert sync["server_time"] and sync["timezone"]
    assert "days" in sync and "sounds" in sync
    # the next bell from sync days, if school is today, must be upcoming
    today = sync["days"][0]
    stime = sync["server_time"]
    upcoming = [b for b in today["bells"] if b["dt"] >= stime]
    if upcoming:
        assert upcoming[0]["bell_type"] in ("warn", "start", "end")

    r = client.get("/api/clients").json()
    assert any(c["name"] == "Room 104" and c["online"] for c in r["clients"])

    b = client.post("/api/client/log", json={"client": "Room 104", "date": "2026-11-02",
                                             "time_hm": "08:00", "bell_type": "start",
                                             "period_num": 1, "period_name": "Period 1"})
    assert b.status_code == 200
    bad = client.post("/api/client/log", json={"client": "Room 104", "date": "2026-11-02",
                                               "time_hm": "08:00", "bell_type": "nope"})
    assert bad.status_code == 400
    log = client.get("/api/log").json()["log"]
    assert log[0]["bell_type"] == "start" and log[0]["client"] == "Room 104"

    # a fired bell recorded in /api/next countdown sanity
    nxt = client.get("/api/next").json()
    assert nxt["server_time"] and isinstance(nxt["bells"], list)


def test_next_counts_only_future_bells(client):
    sid = create_schedule(client, "Regular", REGULAR)
    client.put("/api/calendar", json={"weekdays": [sid] * 7})
    nxt = client.get("/api/next").json()
    stime = nxt["server_time"]
    assert all(b["dt"] >= stime for b in nxt["bells"])
