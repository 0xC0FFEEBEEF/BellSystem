# SchoolBell — handoff (2026-11-09)

For the next agent or engineer taking over SchoolBell. No prior conversation is needed. Read this
whole file first, then `PROTOCOL.md`, `docs/OPERATIONS.md` and `docs/KNOWN-ISSUES.md`.

SchoolBell is a locally hosted school bell and announcement system. An Ubuntu server hosts the
office web UI and `/admin`. Windows classroom computers run a service and a tray app, which play
scheduled bells, "ring now" bells, live push-to-talk and recorded announcements, and report what
they actually played. It is **in production at one school** (≈38 computers). It is a pilot, not a
certified emergency notification system.

## 0. Rules for whoever picks this up

1. **Never publish a release, push to a server or roll out to computers without the owner's
   explicit go-ahead.** The owner has said "don't publish yet" before; ask when unsure.
2. **Never print, copy or commit secrets.** That covers the release signing key
   (`~/.config/schoolbell-release/release-signing.key.pem`), enrollment files and tokens, device
   secrets and passwords. `.gitignore` blocks `*enroll*.json`, `*.key*`, `*.pfx` and `*.exe`.
3. **Version numbers only go up.** A computer or server never treats a rebuild of the same version as
   an update. Every fix that ships gets a new number.
4. **Fix bugs with a test that fails on the old code first.** Every field bug below has one.
5. Owner preferences: short, plain explanations; bells must ring once per moment; missed-bell
   grace **6 s**; bell retry window **4 s**. The owner said not to touch volume or unmute behaviour
   (an earlier "muted computers don't unmute" report was their own mistake).

## 1. Where things stand

| Item | State |
|---|---|
| GitHub | `0xC0FFEEBEEF/BellSystem`, **public**, branch `main`. Licence GPL-3.0-or-later. |
| Published releases | v0.9.4 … v0.9.9, v0.9.12–**v0.9.15** (latest, published 2026-11-09; v0.9.10–0.9.11 were superseded and removed). |
| **v0.9.15** | Published from commit `250e1a9`; server bundle sha256 `eeaba33c…75fd`, installer `d7bb06ad…8a24f`. |
| School server | `rhsvoice.SCSD.local` (10.8.0.46), Ubuntu 24.04, school timezone America/Chicago. Reported **0.9.12** and transcribing queued announcements on 2026-11-06; 0.9.15 is published but not yet confirmed deployed. Admin account `scsd`. |
| School computers | Nearly all on client **0.9.8**. RJH-LAB on 0.9.7. **RHS-17141 (jennifer.massey) is broken**: its 0.9.7 update aborted and left the service stopped (repair in §5.4). |
| Tests | 147 server + 73 client, all passing. The old fake-server cursor assertion race is fixed. |

**What v0.9.9 adds over v0.9.8** (all committed and pushed):

- **Reconnect loops fixed (server).** The server closed the connection on `manifest.rejected`, an
  "unknown" message. The settings message moved each computer's replay cursor, so every reconnect
  replayed the whole outbox. Replays now send only the newest schedule.
- **Updates never leave a computer dead (installer and updater).** Setup and the update runner now
  wait for the service and tray app to exit, retry an aborted setup (exit 5), read exit codes
  reliably, and always restart the SchoolBell service.
- **Enrollment override.** Admin → Enrollment tokens has **+5 computers** and **+24 h** for an
  enrollment that ran out or expired. Refused enrollments are logged with the reason, and
  `enroll.log` on each computer says why enrollment failed.
- **PDQ parameters use the server's certified name** (an IP fails TLS: setup exit code 10).
- Missed-bell reports for bells before a computer was enrolled are dropped.
- Abrupt disconnects are logged as plain "disconnected" instead of an ERROR. Log times are in
  school time.

**What v0.9.12 adds over v0.9.9:** persistent announcement notifications with View/Ignore, live
schoolwide transcripts that become final in the same window, 12-second click-to-dismiss bell cards,
and bundled local whisper.cpp `small.en` transcription. The notification card and buttons have
rounded styling, and View transcript remains on one line. Past announcements do not create popups;
older queued recordings can complete into History only. Targeted transcript text remains office-only.
Whisper is built and runtime-tested against Ubuntu 24.04; the release gate rejects newer glibc
requirements. A Whisper startup failure now rolls back the whole upgrade and stops its restart loop.

**What v0.9.13 fixes:** Announcement History now reads the API's `transcript_current` and
`transcript_original` fields, so completed and corrected transcripts display instead of the false
"pending or unavailable" message. Existing stored transcripts require no migration or retranscription.

**What v0.9.14 adds:** Admin → Settings can set Whisper's CPU core allowance and reports its
applied state. Announcement History has **Redo with Whisper**, which preserves the current transcript
until the replacement succeeds. On clients, a transcript window opened during an announcement stays
open after the audio ends, says **Generating transcription…** while pending, and shows the completed
transcript in that same window when it arrives.

**What v0.9.15 fixes:** Announcement History now offers **Redo with Whisper** for older announcements
whose transcript status is `none`, provided their stored audio still exists. The action creates a new
pending transcript and queues the original recording for Whisper.

## 2. Repository and toolchain

- Path on the owner's machine: `/home/nbull/BellSystem`.
- Layout:
  - `src/Contracts`: shared protocol.
  - `src/Server`: ASP.NET Core, loopback :5001 behind bundled Caddy on :443.
  - `src/Client.Core`: platform-neutral client logic.
  - `src/Client.Audio`: NAudio WASAPI.
  - `src/Client.Service`: Windows service, `SchoolBellService.exe`, service name `SchoolBell`.
  - `src/Client.Desktop`: tray app, `SchoolBellCompanion.exe`.
  - `web/`: React + TypeScript office UI.
  - `tools/EnrollTool`, `tools/DeploymentBootstrap` (PDQ launcher), `tools/Simulator`.
  - `install/server`: `install.sh`, root updater, systemd units, Caddy template.
  - `install/windows`: Inno script, `update-runner.ps1`.
  - `build/release.sh`, `scripts/lab/`, `tests/`, `docs/`.
- .NET **10.0.113** SDK in `~/.dotnet` (Arch's SDK lacks the ASP.NET packs). Run
  `export PATH=$HOME/.dotnet:$PATH` first.
- Node + npm for `web/`. NAudio 2.2.1, React 19, Vite 8, TypeScript 6. Caddy 2.11.7 is bundled.
  Inno Setup 6.7.3 runs inside the Windows VM.
- NuGet lock files are committed (fixed RIDs `linux-x64;win-x64`).

## 3. Build and test

```bash
cd /home/nbull/BellSystem && export PATH=$HOME/.dotnet:$PATH
dotnet build SchoolBell.sln
dotnet test tests/Server.Tests          # 142 tests
dotnet test tests/Client.Tests          # 73 tests
(cd web && npm ci && npm run build)     # office UI -> web/dist (served by the server)
```

## 4. Releasing (build, verify, publish)

**Build.** This needs the Windows lab VM, because the installer is compiled with Inno Setup inside it.
Start the VM, build, then shut it down (the owner prefers it off):

```bash
cd /home/nbull/BellSystem && export PATH=$HOME/.dotnet:$PATH
virsh -c qemu:///system start schoolbell-lab
# wait until the guest agent answers:
until virsh -c qemu:///system qemu-agent-command schoolbell-lab '{"execute":"guest-ping"}' >/dev/null 2>&1; do sleep 5; done; sleep 30
bash build/release.sh 0.9.16 --iscc-vm schoolbell-lab     # next unused version; wipes and refills dist/
virsh -c qemu:///system shutdown schoolbell-lab
```

`release.sh` does the rest:
- Builds the web UI, the server, the Windows client and the installer.
- Signs `release-manifest.json` with the release key. It refuses to run if the key is missing or
  doesn't match `build/release-signing.pub.pem`.
- Puts the matching signed Windows client inside the server bundle (`app/client-release/`).
- Writes `dist/SHA256SUMS`.

**Verify** before publishing:

```bash
openssl dgst -sha256 -verify build/release-signing.pub.pem -signature <(base64 -d dist/release-manifest.json.sig) dist/release-manifest.json
grep -E '"version"|"commit"' dist/release-manifest.json
tar -xzOf dist/server/schoolbell-server-linux-x64.tar.gz schoolbell-0.9.16/app/client-release/release-manifest.json | grep '"version"'
sha256sum dist/server/schoolbell-server-linux-x64.tar.gz dist/windows/SchoolBellSetup.exe
```

**Publish** (only with the owner's go-ahead). Write release notes to a file, then:

```bash
gh release create v0.9.16 -R 0xC0FFEEBEEF/BellSystem --target <commit from the manifest> \
  --title "SchoolBell 0.9.16" --notes-file <notes.md> --latest \
  dist/server/schoolbell-server-linux-x64.tar.gz dist/server/install.sh dist/windows/SchoolBellSetup.exe \
  dist/release-manifest.json dist/release-manifest.json.sig dist/SHA256SUMS dist/LICENSES.md dist/LICENSE
```

- **Asset names matter.** The server's updater looks for `release-manifest.json`,
  `release-manifest.json.sig` and `schoolbell-server-linux-x64.tar.gz` on the latest release.
- Afterwards, check that GitHub's digests match the local files:
  `gh api repos/0xC0FFEEBEEF/BellSystem/releases/latest --jq '.assets[] | "\(.name) \(.digest)"'`.
- Then update §1 of this file.

**v0.9.15 is published** at `https://github.com/0xC0FFEEBEEF/BellSystem/releases/tag/v0.9.15`.
The signed artifacts remain in `dist/`; that directory is gitignored and is wiped by the next
`release.sh` run.

## 5. Deploying at the school

### 5.1 Server
- **Admin → Updates → Server software → Check GitHub now → Install X.** The root updater does the rest:
  - re-verifies the signature and SHA-256
  - backs up the database
  - runs `install.sh --upgrade` and health-checks
  - rolls back on failure, restoring the database
- The server log shows "server update to X requested … the root updater takes over". Details are
  in `/var/lib/schoolbell/update/update.log` on the server.
- Manual fallback (for example if the school's web filter blocks github.com, as it once did) keeps
  all data:

  ```bash
  scp ~/BellSystem/dist/server/schoolbell-server-linux-x64.tar.gz scsd@rhsvoice:~/
  # on rhsvoice:
  tar -xzf schoolbell-server-linux-x64.tar.gz && cd schoolbell-<version> && sudo bash install.sh
  ```

### 5.2 Classroom computers
- **Admin → Updates → Windows client updates → version → Roll out.** Set the batch size and the
  minutes between batches.
- Each computer:
  - verifies the signature
  - waits for a quiet moment (no bell or announcement due)
  - runs `update-runner.ps1`: setup, then a health check, then reinstalling the previous version if
    the new one doesn't reconnect
- A computer counts as installed when it reconnects on the new version.
- The server version must be ≥ the client version.

### 5.3 New computers (PDQ)
- **Admin → Windows deployment → Download Windows Installer.** The EXE embeds an enrollment and the
  school CA.
- PDQ parameters:
  `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /SERVER=rhsvoice.SCSD.local /LOG="C:\Windows\Temp\SchoolBell-setup.log"`.
- **Always use the name, never the IP.** The certificate covers only the `install.sh --hostname`
  names.
- Setup exit codes:
  - **10**: installed, but enrollment failed. See `C:\ProgramData\SchoolBell\logs\enroll.log`.
  - **11**: the service did not start.
  - **5**: setup aborted (a file in use).
- A used-up or expired enrollment can be reopened with **+5 computers** / **+24 h** in Admin.

### 5.4 Diagnosing a computer from PDQ
Use PDQ's **PowerShell** mode. In Command mode, PowerShell has to be wrapped in
`powershell -NoProfile -Command "…"`. In PowerShell mode, don't wrap it: the outer shell expands
`$_` inside the quotes.

```powershell
Get-Service SchoolBell | Format-List Name,Status,StartType
$log = Get-ChildItem C:\ProgramData\SchoolBell\updates -Recurse -Include update-runner.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) { "== " + $log.FullName; Get-Content $log.FullName -Tail 20 }
"== service.log"; Get-Content C:\ProgramData\SchoolBell\logs\service.log -Tail 30 -ErrorAction SilentlyContinue
```

**Repair RHS-17141** (service stopped after an aborted update). This closes the tray app, re-runs
the installer already downloaded on that computer (a repair that keeps its enrollment), and starts
the service:

```powershell
Stop-Process -Name SchoolBellCompanion -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 5
$p = Start-Process 'C:\ProgramData\SchoolBell\updates\0.9.7\SchoolBellSetup.exe' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/LOG=C:\Windows\Temp\SchoolBell-repair.log' -Wait -PassThru
"repair exit code: " + $p.ExitCode; Get-Service SchoolBell | Format-List Name,Status
```

Other places to look:
- Computer logs: `C:\ProgramData\SchoolBell\logs\service.log`, `enroll.log`, and the tray app's
  `%LOCALAPPDATA%\SchoolBell\companion.log`.
- Server side: **Admin → Log** (Verbose adds every bell played; Download .txt), **History**
  (per-computer result of every bell), and **Endpoints** (per-computer state; flags a computer that
  went offline mid-update).

## 6. What the field taught us (2026-11-04/05)

Each of these reached production. Each now has a test that fails on the old code.

| Symptom at the school | Cause | Fixed in |
|---|---|---|
| No scheduled bell ever rang; 25 of 25 at "revision 0" | A real 7-day schedule is > 32 KB; clients dropped larger messages and the server's replay skipped it silently | 0.9.5 (clients take 1 MB and advertise it; server never sends a client more than it accepts) |
| Bells rang twice or 3–4 times in a row | Two schedules shared times; back-to-back periods ended and started on the same minute | 0.9.6 (one bell per moment per target; clients also ring once across targets) |
| Every other bell "Expired" on 36/38 computers | Bell loop timed its next wake from a clock read **before** the 3 s bell played, so it woke ~3 s late, past the 2 s grace | 0.9.7 (client) |
| Hold to talk waited ~5.6 s | Server always waited the full 5 s preparation window | 0.9.7 (starts when every connected computer is ready) |
| A computer reconnected every 2 s | One unhandled message ended the connection and was replayed on reconnect | 0.9.8 (client keeps the connection); 0.9.9 (server root cause below) |
| Computers dropped ~50 ms after connecting; "resync cursor not found" on every reconnect | Server had no handler for `manifest.rejected`; the live settings message moved the device cursor out of the outbox | 0.9.9 (server) |
| PDQ install exit code 10 | `/SERVER=10.8.0.46`; the certificate covers only the name | 0.9.9 (Admin builds the PDQ line from the certified name; enroll.log explains) |
| RHS-17141 silent for hours | Update and rollback setup both aborted (exit 5, file in use); runner left the service stopped | 0.9.9 (wait for processes, retry, always restart the service) |

Also added on request:
- **Scheduled bells in History**, with per-computer details and a "no report" list.
- **Admin → Log** with a Verbose switch and .txt download.
- **Bell retries:** after 1 s and 2.5 s, only within 4 s of the bell's time, and only for passing
  failures. Missing sounds are not retried.
- **6 s missed-bell grace.**
- **Enrollment override.**

## 7. Feature status

Legend: **WIN** verified on Windows audio in the lab VM (output captured and analyzed) · **UBU**
verified on Ubuntu 24.04 with systemd · **FIELD** working at the school · **UNIT** automated
tests only · **NO** not done.

| Requirement | Status | Notes |
|---|---|---|
| Scheduled bells from the 7-day signed cache | FIELD, UNIT | 0.9.8 at the school: whole periods "completed" on every connected computer |
| Ring now | FIELD, WIN | |
| Customizable bell sounds | UNIT, UI | republish after replacing a sound |
| Volume override (master + app session) with restore | WIN | do not change (owner) |
| Locked session plays / signed out plays (service renderer) | WIN | |
| No-user boot (nobody signed in since boot) | **WIN: silent** | Windows plays session-0 audio as silence before the first sign-in; reported honestly as `no_renderer` |
| Recorded announcements | WIN | |
| Live push-to-talk | FIELD, WIN | starts in ~1 s since 0.9.7 |
| Windows installer (silent, Apps entry, repair, uninstall) | FIELD, WIN | |
| Client updates (signed, staged, rollback) | FIELD | 0.9.5–0.9.8 rolled out at the school; one computer failed (fixed in 0.9.9) |
| Server install + self-update from GitHub | FIELD, UBU | 0.9.7 → 0.9.8 via Admin at the school |
| History (announcements, scheduled, manual, per-computer) | FIELD | |
| Admin → Log | FIELD | |
| Keep-awake | UNIT | not exercised on Windows |
| Transcription (Whisper) | RELEASE | v0.9.15: Ubuntu 24.04-compatible local whisper.cpp `small.en`, live partials, durable final transcripts, configurable CPU cores, redo (including transcript `none`), and persistent generating window |
| Authenticode signing | NO | no certificate; SmartScreen may warn |
| Load: 100 / 500 simulated endpoints | SIM | §9 |

## 8. Known issues and next tasks

Full list in `docs/KNOWN-ISSUES.md`. The ones that matter most:

1. Update the server to **0.9.15** from Admin. The transcript-`none` redo fix is server/web-only;
   existing 0.9.14 Windows clients remain compatible. Roll out 0.9.15 clients later if matching versions are desired.
   **Repair RHS-17141 first** (§5.4): the updater on it is the old one.
2. The update-runner fix only helps updates **from** 0.9.9 onwards, because each update is run by
   the runner already on the computer. Updating **to** 0.9.9 benefits from the installer's own new
   wait. Watch Endpoints for any computer flagged "went offline while updating".
3. Schedule manifests use a school-wide HMAC key. Moving to the existing ECDSA signing needs a
   protocol revision.
4. Silent before the first sign-in since boot (Windows behaviour). Keep classroom computers signed
   in (locked is fine).
5. The flaky client test (§1).
6. Possible improvement: buffer the first ~1 s of hold-to-talk audio so nothing spoken before
   "ready" is lost.

## 9. Measured results (lab, single runs)

| | 100 endpoints | 500 endpoints |
|---|---|---|
| all connected | 0.53 s | 1.27 s |
| ring delivery p95 | 42 ms | 157 ms |
| live egress (PCM16 16 kHz) | 25.7 Mbps | 126.9 Mbps |
| live transport latency p95/p99 | 4/6 ms | 31/194 ms |
| server RSS max | 309 MB | 348 MB |

Raw data: `docs/evidence/load/`.

## 10. Lab environment (owner's machine)

- **Windows VM `schoolbell-lab`** (libvirt `qemu:///system`):
  - Copy-on-write overlay of `~/VMs/win11/win11.qcow2`. Do not boot the original `win11` while it
    is in use.
  - Account `User`, auto sign-in. Shared folder `~/VMs/schoolbell-lab/share` = `Z:`.
  - Run commands with `scripts/lab/vmexec.py -c '<PowerShell>'`.
  - Used for compiling the installer. Keep it shut down when idle.
- **Ubuntu container `sb-ubuntu`** (podman, systemd): used by `scripts/lab/ubuntu-upgrade-test.sh`
  for install/update/rollback lifecycle tests.
- Lab servers (`scripts/lab/lan-up.sh`, `lab-up.sh`) keep their data in a session scratch
  directory; recreate them as needed.
- Spec documents: `schoolbell-application-plan.md` and `schoolbell-overnight-agent-brief.md` in
  `/home/nbull/Documents/Codex/2026-11-02/al/outputs/`.
