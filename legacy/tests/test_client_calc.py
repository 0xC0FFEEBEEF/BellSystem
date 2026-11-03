"""Unit tests for the bell client's pure math (offset + pending bell selection)."""
import importlib.util
import time
from datetime import datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

spec = importlib.util.spec_from_file_location(
    "schoolbell_client", Path(__file__).resolve().parents[1] / "client" / "schoolbell_client.py")
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)

TZ = ZoneInfo("America/New_York")


def sync_payload():
    now = datetime(2026, 11, 2, 8, 30, tzinfo=TZ)
    day = {"date": "2026-11-02", "schedule_name": "Regular", "bells": [
        {"bell_type": "end", "time_hm": "08:50", "period_num": 1,
         "period_name": "Period 1", "schedule_id": 1, "dt": "2026-11-02T08:50:00-05:00"},
        {"bell_type": "start", "time_hm": "09:00", "period_num": 2,
         "period_name": "Period 2", "schedule_id": 1, "dt": "2026-11-02T09:00:00-05:00"},
        {"bell_type": "end", "time_hm": "08:00", "period_num": 1,
         "period_name": "Period 1", "schedule_id": 1, "dt": "2026-11-02T08:00:00-05:00"},
    ]}
    return {"server_time": now.isoformat(), "timezone": "America/New_York",
            "days": [day], "sounds": {"start": "bell.wav", "end": "chime.wav", "warn": "chime.wav"}}


def test_compute_delta_positive_when_server_ahead():
    server_iso = "2026-11-02T08:30:00-05:00"
    local_epoch = datetime.fromisoformat(server_iso).timestamp() - 7  # local clock 7s behind
    assert client.compute_delta(server_iso, local_epoch) == 7.0


def test_local_fire_epoch_corrects_for_clock_skew():
    # bell at 09:00 server time; server clock is 7s ahead of local -> fire at local 08:59:53
    bell_dt = "2026-11-02T09:00:00-05:00"
    fire = client.local_fire_epoch(bell_dt, 7.0)
    assert fire == datetime.fromisoformat(bell_dt).timestamp() - 7


def test_pending_bells_sorted_future_only_excludes_fired():
    p = sync_payload()
    now_epoch = datetime.fromisoformat(p["server_time"]).timestamp()  # 08:30 local==server
    bells = client.pending_bells(p, set(), now_epoch)
    times = [b["time_hm"] for _, b in bells]
    assert times == ["08:50", "09:00"]  # 08:00 already passed
    # after marking the 08:50 end bell fired, only 09:00 remains
    fired = {client.bell_key(bells[0][1])}
    left = client.pending_bells(p, fired, now_epoch)
    assert [b["time_hm"] for _, b in left] == ["09:00"]


def test_pending_bells_empty_for_past_day():
    p = sync_payload()
    # same clock on both sides, but the day's bells are already over
    p["server_time"] = "2026-11-02T15:00:00-05:00"
    late = datetime.fromisoformat(p["server_time"]).timestamp()
    assert client.pending_bells(p, set(), late) == []
