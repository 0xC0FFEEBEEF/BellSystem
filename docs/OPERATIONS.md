# SchoolBell operations

## Install (Ubuntu 24.04 LTS x64)

```bash
bash build/publish.sh                                # on a build box with .NET SDK
# copy repo+dist to the server, then:
sudo bash install/server/install.sh --public-host bells.school.lan [--no-caddy] [--server-name "RHS Bells"]
```

Layout: binaries `/opt/schoolbell`, config `/etc/schoolbell` (env file, Caddyfile), state
`/var/lib/schoolbell` (SQLite db, sounds, keys, recordings), journald logs. Repeat installs and
upgrades replace binaries and **preserve** `/etc/schoolbell` and `/var/lib/schoolbell`.
`sudo bash install/server/uninstall.sh` removes service+binaries, keeps data (`--purge-data` to erase).

## First-run bootstrap

1. `journalctl -u schoolbell | grep 'SETUP CODE'` — a one-time code printed once per empty install.
2. Open `https://<host>/`, complete setup (username + ≥12-char password → admin), **set the school
   timezone** (publish refuses until set), then create operators/viewers in `/admin`.

## Enrolling classroom machines

1. `/admin → Enrollment tokens`: create a token (default single use, 24 h expiry, optional group scope).
2. On the classroom PC: `SchoolBellSetup.exe /silent /SERVER=https://<host> /ENROLLMENTFILE=token.txt /CAFILE=<CA root>`
   (or run without `/silent` to be prompted). The installer installs the school CA, enrolls the device
   (one-time, per machine), installs the **SchoolBellService** and a per-logon **SchoolBellCompanion** task.
3. Re-enrolling after uninstall requires a fresh token. Repair/upgrade reuses the machine's existing
   credentials. Revoking a device in `/admin` blocks reconnection and re-enrollment.

TLS: clients validate against the school CA (installed by the installer) or the system store; there
is no TOFU and no bypass. Caddy's built-in CA root: `/var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt`.

## Daily operation

- **Schedules**: templates (normal day, late start, early release, exam…) with periods and weekday
  assignments; calendar overrides per date (blank override = explicit no-bells) and holidays.
  **Publish** compiles 7 days and pushes signed manifests; the schedule page shows each device's
  accepted revision (lagging/offline devices visible; offline devices run their last accepted
  manifest until it expires, then report `stale_cache`).
- **Announcements**: "Live" takes the schoolwide talk lease (one at a time; renew every ≤60 s; hard
  cap 300 s; closing the tab ends it within a second), target = all/groups/selected devices
  (operator scope enforced server-side). "Recorded" uploads a WAV (≤25 MB, ≤5 min).
- **Endpoints page**: state per machine — connected, ready_to_play, playing, muted, missing_speaker,
  no_renderer, stale, offline, playback_failed. A green connection never means audible sound.
- **History**: per announcement/occurrence delivery results with honest final states; transcripts
  may be corrected with an audit trail. Targeted transcripts stay office-only.
- **Keep-awake** (/admin): scoped policies with absolute expiry; desired/acknowledged/failed shown
  separately; honored offline; does not unlock screens or defeat manual sleep (by design).

## Backup / restore

Stop `schoolbell`, tar `/etc/schoolbell` + `/var/lib/schoolbell`, restart. Restore = reverse on an
installed host. Client machines are stateless except their credential + cache — re-enroll to replace.

## Load and capacity

No product device cap. Bounded queues, visible configurable settings (outbox depth, per-device
media queue, max_connections) and live depths at `GET /api/diagnostics`. Simulators at 100 and 500
devices report measured connections/CPU/RAM/egress/queue lag; actual capacity depends on the server
and network (measured numbers live in TEST-RESULTS.md).

### Test/container environments (no systemd)

`install.sh` supports a documented sandbox mode used in this repo's own verification:
`SB_PREFIX=... SB_ETC=... SB_VAR=... SB_LOG=... SB_URLS=http://127.0.0.1:5011 SB_SETUP_CODE=... SB_SKIP_SYSTEMD=1 bash install/server/install.sh`
starts the binary directly with a pidfile instead of systemd (no caddy, no system user). The
production path above is unchanged; the systemd units themselves are exercised only on a real
systemd host (see KNOWN-ISSUES #8).
