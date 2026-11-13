# SchoolBell operations

## Install the server (Ubuntu 24.04 LTS, x86-64)

Download `schoolbell-server-linux-x64.tar.gz` from the latest GitHub release, check it against the
SHA-256 in the release notes, then:

```bash
tar -xzf schoolbell-server-linux-x64.tar.gz
cd schoolbell-<version>
sudo bash install.sh --hostname bells.school.example --timezone America/Chicago
```

| Option | Meaning |
| --- | --- |
| `--hostname NAME\|IP` | Address the office and classrooms use (a DNS name is best). Repeatable. |
| `--timezone IANA` | School timezone. Never guessed from the server's clock. |
| `--tls-cert F --tls-key F` | Use the school's certificate instead of Caddy's internal CA. |
| `--server-name NAME` | Name shown in the office UI. |
| `--no-firewall` | Leave ufw alone (by default, if ufw is active, only 443/tcp is allowed). |

The office UI is served on port 443, so the address is just `https://<hostname>/`.

Layout: releases in `/opt/schoolbell/releases/<version>` with `/opt/schoolbell/current` pointing at the
running one; configuration, TLS and the pinned release key in `/etc/schoolbell`; database, sounds,
recordings and backups in `/var/lib/schoolbell`; Caddy state in `/var/lib/schoolbell-proxy`. Logs go to
journald (`journalctl -u schoolbell -f`, `journalctl -u schoolbell-caddy`).

Re-running `install.sh` keeps every setting and all data. Uninstall with
`sudo bash /opt/schoolbell/current/uninstall.sh`; it keeps data unless you add `--purge-data`.

## First sign-in

1. The installer prints a one-time setup code (also in `journalctl -u schoolbell | grep 'SETUP CODE'`).
2. Open `https://<hostname>/`, choose **First-time setup**, enter the code, and choose the admin username
   and password. Any non-empty password is accepted; set `SchoolBell__MinPasswordLength=<n>` in
   `/etc/schoolbell/env` to require a minimum length.
3. In `/admin`, check the school timezone (publishing refuses until it is set) and add office accounts
   (admin, operator or viewer).

## Enroll classroom computers

Recommended: **Admin → Windows deployment**. Enter a label, how many computers it may enroll and how
many hours it is valid, then select **Download Windows Installer**. The EXE contains the enrollment
token and the school CA certificate; the server address stays a parameter. Deploy it with the PDQ
parameters shown on that page, or:

```
SchoolBellSetup.exe --silent --server bells.school.example
```

The generic installer from the GitHub release takes the files separately. Download
`schoolbell-enroll.json` from the same page (**Separate enrollment file**) and use the CA at
`/etc/schoolbell/school-ca.cer`:

```
SchoolBellSetup.exe --silent --server bells.school.example --enrollment-file C:\Deploy\schoolbell-enroll.json --ca-file C:\Deploy\school-ca.cer --log C:\Deploy\install.log
```

Exit codes: 0 ok, 2 bad or missing argument, 10 installed but enrollment failed (check the address and
firewall), 11 the service did not start. The first install can take a few minutes while Windows
Defender scans the new files.

The installer adds the school CA to the computer's trusted roots, enrolls the computer (one credential
per machine), installs the **SchoolBell** service and the tray companion logon task, and registers an
entry in **Settings → Apps**. Uninstalling there removes the service, task, files, credential, logs and
the CA it added. Each computer needs a unique name: re-enrolling the same name reuses its device record.
Revoking a computer in `/admin` blocks reconnection; re-enrolling it needs a new enrollment file.

TLS: clients validate the server against the school CA or the Windows certificate store. There is no
trust-on-first-use and no certificate bypass.

## Daily operation

- **Home**: choose who hears it, hold to talk, ring the bell now, and see the next bells.
- **Bells**: schedule templates (normal day, late start, early release, exams…) with periods and
  weekdays, date overrides (a blank override means no bells) and holidays. **Publish** compiles the next
  7 days and pushes a signed manifest; each computer's accepted revision is shown. The server renews
  that published schedule every school day and on startup after an outage. Renewal uses the published
  rules, calendar and sounds; saved edits still need **Publish**. Offline computers keep
  running their last accepted manifest until it expires, then report `stale_cache`. **Bell sounds**
  uploads or replaces sounds (`std-bell` is the school bell); republish so cached bells pick up a
  replaced sound.
- **Announce**: live push-to-talk takes the schoolwide talk lease (one talker at a time, hard cap
  300 s; closing the tab ends it within a second). Recorded announcements accept a WAV up to 25 MB and
  5 minutes, with preview before sending. Targets are everyone, groups or selected computers, and
  operator scope is enforced by the server.
- **Endpoints**: per computer state: connected, ready to play, playing, muted, missing speaker, no
  renderer, stale, offline, playback failed. A green connection does not mean audible sound. Per-computer
  settings choose the speaker, announcement volume and cap, room and groups.
- **History**: delivery results per announcement and bell, with honest final states and locally
  generated transcripts. During a live schoolwide announcement, classroom notifications can open a
  live transcript; the same window changes to the final transcript after the announcement. Targeted
  transcript text stays office-only. Administrators and in-scope operators can choose **Redo with
  Whisper** when the stored recording still exists; the current text remains visible until the new
  transcription succeeds.
- **History → Transcript → Notify endpoints** sends the current completed schoolwide transcript
  to connected, updated computers in the original announcement audience. The persistent classroom
  card says **Transcript ready** and offers **View transcript** / **Ignore**. It sends no audio and
  does not rerun recognition. History reports the sent, offline and client-update counts. Reminders
  are immediate, not queued for offline computers; ordinary history synchronization never repeats
  the popup. Targeted transcripts retain their office-only restriction.
- **Keep computers awake** (`/admin`): scoped policies with an absolute expiry, honored offline. They
  do not unlock screens or override manual sleep.
- **Admin → Settings → Bell notifications**: enable/disable classroom bell cards and set their
  duration to 1–300 seconds (default 12). This applies to scheduled and manual bells, requires the
  updated Windows client, and persists across offline restarts. Clicking a card dismisses it early.
- **Admin → Client logs**: a separate verbose log viewer, with all endpoints or one endpoint,
  text search, older uploads, a separate-window link, and complete filtered text downloads. Updated
  clients send retained service logs (including exceptions), each profile's companion logs, enrollment
  and updater logs under ProgramData\SchoolBell, and SchoolBell installer logs in Windows\Temp.
  Existing local rotated logs are uploaded too; already-deleted local logs cannot be recovered.
  Uploads use the enrolled device credential, run separately from audio/control, and resume from
  acknowledged byte offsets after an outage. Server retention is 14 days from upload. Client logs
  are visible only to administrators and remain separate from the server log.

On upgrade from a release without published-rule snapshots, the renewal worker adopts the saved
rules only if they reproduce the last published schedule. If they differ, **Bells → Calendar & publish**
and the server log display the reason: explicitly publish the intended schedule once. The initial
schedule for a new installation always requires an explicit Publish.

The bell and announcement volume override raises the Windows master volume (and unmutes) for the
duration of the sound, then puts master and per-app volume back exactly as they were.

Transcription runs entirely on the SchoolBell server through `schoolbell-whisper.service` on loopback
port 8178. Check it with `systemctl status schoolbell-whisper` and
`journalctl -u schoolbell-whisper -f`. It uses the bundled English `small.en` model, defaults to two
CPU cores, and never blocks audio playback. **Admin → Settings → Transcription performance** can assign
1–16 cores (always leaving at least two detected host cores for SchoolBell and the OS). Applying a
change restarts only Whisper; an in-progress job returns to the durable retry queue. The unprivileged
web app writes a bounded request which `schoolbell-whisper-control.path` hands to a root-only validator.
Set `SchoolBell__TranscriptionEnabled=false` in
`/etc/schoolbell/env` and restart `schoolbell` to disable it; queued final transcripts resume when it
is re-enabled.

Recorded uploads are queued for transcription immediately, independently of their 24-hour delivery
window. Live recordings are queued after the WAV is finalized and closed. New work wakes the worker;
eligible jobs run back-to-back, with recent requests first. Server startup reclaims interrupted jobs
and repairs retained recordings missing transcription jobs, including recorded offers from older
releases that still show `transcript: none`. Existing corrected transcripts are preserved during redo.
Empty output and request timeouts are retried with backoff and shown as errors, never saved as a
successful empty transcript. Final jobs take priority over best-effort live previews.

**Admin → Settings → Transcription performance** probes Whisper's `/health` endpoint and shows
whether the model is ready, whether transcription is enabled, queued/running/failed jobs, the latest
job duration and error, and (when available) the actual process thread count and cgroup CPU allowance.
An old `applied` settings file alone does not establish that the engine is running. The core slider
sets an allowance; CPU use varies across recognition stages and more threads are not always faster.
History refreshes open transcript details every two seconds and distinguishes queued, running,
retrying and failed work. Classroom transcript windows retain completed text when audio ends.

Recognition uses an English `small.en` model. Only digital silence is skipped without inference;
quiet audio receives up to 32× gain in the inference copy. The original audio and playback volume
are unchanged. Local speech checks and limits are recorded in
[`evidence/transcription-2026-11-13.md`](evidence/transcription-2026-11-13.md).

## Updates

**Server:** **Admin → Server software → Check GitHub now** reads the GitHub Releases of the configured
repository (default `0xC0FFEEBEEF/BellSystem`). Only releases whose manifest is signed with the release
key pinned at `/etc/schoolbell/release-signing.pub.pem` are offered. Applying one hands the request to
a root updater (`schoolbell-update.path`), which re-checks the signature and SHA-256, backs up the
database, installs, health-checks, and rolls back automatically, restoring the database if the new
version fails. With **Automatic updates** on, the server checks every 6 hours and installs inside the
update window (default 02:00–04:00 school time) when nothing is playing. The check needs the repository
to be public; a private repository is not supported.

**Windows clients:** each server release contains the matching signed Windows client. Roll it out from
**Admin → Windows client updates** in batches. Each computer verifies the signature, waits until no
bell or announcement is due, installs, and reinstalls its previous version if the new one does not
reconnect. A computer counts as installed once it reconnects on the new version.

## Backup and restore

**Admin → Backups** creates a consistent backup (database, sounds and keys; optionally recordings) in
`/var/lib/schoolbell/backups`, which you can download. **Restore from a file** uploads a backup; the
server validates it, restarts, and applies it, keeping the previous database. The server updater also
takes a backup before every update. Classroom computers keep only their credential and bell cache;
replacing one means re-enrolling it.

## Load and capacity

There is no device limit. Queues are bounded and visible: outbox depth, per-device media queue and
connection counts are at `GET /api/diagnostics`. Simulated loads of 100 and 500 devices are measured in
`HANDOFF.md` §6 (raw data in `docs/evidence/load/`); real capacity depends on the server and network.
