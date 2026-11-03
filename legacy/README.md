# SchoolBell

A school bell-scheduling system: a web console for the front office, and a bell
client that runs on classroom PCs and rings each bell on time.

> Built from the uploaded brief and application plan filenames — the plan files
> themselves never arrived on this machine, so the feature set below is the
> reconstructed spec. See `RECONSTRUCTED-SPEC.md` for what was assumed.

## What it does

- **Schedules** — each schedule (Regular Day, Early Release…) is a list of
  periods with start/end times and an optional pre-bell (a warning chime N
  minutes before the period starts).
- **Calendar** — each weekday gets a default schedule; specific dates can be
  overridden (a different schedule, or no bells); holidays silence a day and
  beat overrides.
- **Today view** — today's bell timeline and a live countdown to the next bell.
- **Bell clients** — the client app checks in with the server, downloads its
  sounds once, keeps a clock offset to the server, plays the configured sound
  at every bell time, and reports each fired bell to the Activity log.
- **Settings** — school name, timezone (IANA name, e.g. `America/New_York`),
  and which sound plays for pre-bell / start / end.

## Running the server

```bash
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
.venv/bin/python gen_sounds.py           # creates sounds/*.wav (already included)
.venv/bin/uvicorn app:app --host 0.0.0.0 --port 8011
```

Open `http://localhost:8011/`. The app starts empty: create a schedule, give it
periods, assign it to weekdays in Calendar, then add holidays/overrides.
Data lives in `schoolbell.db` (SQLite, created on first run).

## Running a classroom client

```bash
python client/schoolbell_client.py --server http://SERVER_IP:8011 --name "Room 104"
python client/schoolbell_client.py --server http://SERVER_IP:8011 --name "Room 104" --once   # print today's bells, no ringing
```

### Building the Windows .exe (on a Windows machine)

```bat
cd client
build_exe.bat
```

Produces `dist\SchoolBellClient.exe`; start it per classroom PC with
`SchoolBellClient.exe --server http://SERVER_IP:8011 --name "Room 104"`
(Task Scheduler or the Startup folder). NOTE: the .exe build has not been
exercised on a real Windows machine from this environment.

## Tests

```bash
.venv/bin/pip install -r requirements-dev.txt   # includes pytest + httpx
.venv/bin/python -m pytest tests/
```

Covers schedule resolution (holiday > override > weekday), bell generation and
ordering, clock-offset math and pending-bell selection in the client, and API
validation via FastAPI TestClient.

## Layout

- `app.py` — FastAPI server: admin API, client sync/log endpoints, static UI
- `db.py`, `schedule.py` — SQLite schema; schedule resolution + bell math
- `index.html`, `static/` — the console UI
- `client/schoolbell_client.py` — classroom bell client (stdlib only)
- `client/build_exe.bat`, `client/requirements.txt` — PyInstaller packaging for Windows
- `gen_sounds.py`, `sounds/` — generated bell/chime/buzzer WAV assets
- `tests/` — pytest suites
