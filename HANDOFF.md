# SchoolBell — handoff (updated 2026-11-04)

For the next agent or engineer. No prior conversation is needed. Read this first, then
`PROTOCOL.md` (v1 plus the §13 additions). Governing spec: `schoolbell-application-plan.md` and
`schoolbell-overnight-agent-brief.md` (copies in `/home/nbull/Documents/Codex/2026-11-02/al/outputs/`).

**Status:** a working lab pilot, not a certified emergency system. Bells, ring-now, live push-to-talk,
recorded announcements, volume override/restore, the Windows installer and the Ubuntu installer all work
end to end on real Windows audio in a VM and in a real systemd environment. Gaps are listed honestly below.

## 1. Repository

- Path `/home/nbull/BellSystem`, branch `main`, remote `origin` = github.com/0xC0FFEEBEEF/BellSystem.
  `main` is pushed. Release **v0.9.4** is published (GPL-3.0-or-later); v0.9.3, built before the
  licence was added, was deleted. The repository stays private until the owner makes it public, which
  server self-update needs (the update check reads GitHub Releases without a token).
- History was rewritten once (2026-11-03) to remove a committed test kit and lab credentials; every
  token in it was revoked. Licence: GPL-3.0-or-later (`LICENSE`, `LICENSES.md` for third parties).
- Layout: `src/Contracts` (shared protocol), `src/Server` (ASP.NET Core), `src/Client.Core`
  (platform-neutral client logic), `src/Client.Audio` (NAudio WASAPI engine), `src/Client.Service`
  (Windows service), `src/Client.Desktop` (tray companion), `web/` (React + TypeScript office UI),
  `tools/EnrollTool`, `tools/Simulator`, `install/server` (install.sh, updater, units, Caddy template),
  `install/windows` (Inno script, update runner, task XML, icon), `build/release.sh`, `scripts/lab/`
  (all lab and test tooling), `tests/` (184 automated tests), `docs/evidence/` (raw test evidence),
  `assets/sounds` (built-in sounds; `bell.wav` is the school's bell tone from `~/Music/belltone.wav`).

## 2. Versions and toolchain

- .NET **10.0.113** SDK (pinned in `global.json`; runtime 10.0.13). The official Microsoft SDK is installed in
  `~/.dotnet` because Arch's source-built SDK lacks the ASP.NET packs: `export PATH=$HOME/.dotnet:$PATH`.
- NuGet lock files are committed (fixed RIDs `linux-x64;win-x64`, Windows-only projects `win-x64`).
- NAudio **2.2.1**. NAudio 3.x is a fresh major rewrite that was still getting WasapiOut fixes in Oct 2026.
- Web: React 19.2.9, Vite 8.3.4, TypeScript 6.0.3, @vitejs/plugin-react 6.1.2 (`web/package-lock.json`).
- Caddy **2.11.7** (bundled into the server release, sha512-verified). Inno Setup **6.7.3** (installed
  in the lab VM, sha256-verified).
- Protocol `v: 1` with additive v1.1 messages (bell.ring, device.settings, history.item, update.offer,
  update.status). Server and client 0.9.0 are compatible.

## 3. Build, run, test

```bash
export PATH=$HOME/.dotnet:$PATH
dotnet build SchoolBell.sln
dotnet test tests/Server.Tests          # 121 tests
dotnet test tests/Client.Tests          # 63 tests
(cd web && npm ci && npm run build)     # office UI -> web/dist (the server serves it)
dotnet run --project src/Server         # dev: http://127.0.0.1:5001, prints SETUP CODE once
bash build/release.sh 0.9.5 --iscc-vm schoolbell-lab    # full signed release into dist/ (VM must be running)
RELEASE_OUT=.build/x bash build/release.sh 0.9.5 --skip-installer   # server-only bundle elsewhere
```
Release signing key: `~/.config/schoolbell-release/release-signing.key.pem` (ECDSA P-256, 0600,
**never commit**). Public half: `build/release-signing.pub.pem`, which is pinned by servers
(`/etc/schoolbell/release-signing.pub.pem`) and clients (`{app}\release-signing.pub.pem`).

Install interfaces (both tested):
```bash
sudo bash install.sh --hostname bells.school.example --timezone America/Chicago   # from the extracted bundle
```
```powershell
SchoolBellSetup.exe --silent --server 10.0.2.50 --enrollment-file C:\Deploy\schoolbell-enroll.json --ca-file C:\Deploy\school-ca.cer
```
Exit codes: 0 ok · 2 bad or missing argument · 10 installed but enrollment failed · 11 service did not start.

## 4. Releases

Latest: **v0.9.7** (commit `7ec4a66`, 2026-11-05): client bell loop no longer wakes late after a bell plays (every
other bell expired in the field), pre-publish bells not reported missed, hold to talk starts once every connected
computer is ready, Admin → Log with .txt download. Server bundle sha256 `95a1d81b…0907a`, installer `17d95eb7…eb45`.
Earlier: **v0.9.6** (`f74d477`, one bell per moment, scheduled bells in History), **v0.9.5** (`53f3c6a`, schedules over
32 KB reach clients), **v0.9.4** (`c60cf0e`). The repository is public since 2026-11-04. Assets:
`schoolbell-server-linux-x64.tar.gz` (sha256 `fdae1554…bfdd9`), `install.sh`, `SchoolBellSetup.exe`
(sha256 `1e880f9d…c151`, unsigned), `release-manifest.json` + `.sig`, `SHA256SUMS`, `LICENSE`,
`LICENSES.md`. Version numbers only go up: a server already on a version never sees a rebuild of the
same version as an update, so a fixed release always gets a new number.

To publish the next one: `bash build/release.sh 0.9.5 --iscc-vm schoolbell-lab`, check
`openssl dgst -sha256 -verify build/release-signing.pub.pem` against the manifest, then
`gh release create v0.9.5 --target <commit> …` with the same asset list (the server's updater looks for
`release-manifest.json`, `release-manifest.json.sig` and `schoolbell-server-linux-x64.tar.gz` by name).

## 5. Feature status

Legend: **WIN** verified on real Windows audio (lab VM, output captured to WAV and analyzed) · **UBU**
verified on Ubuntu 24.04.5 with systemd (privileged rootless container) · **SIM** simulator only ·
**UNIT** automated tests only · **NO** not done.

| Requirement | Status | Evidence / notes |
|---|---|---|
| Scheduled bells from the 7-day signed cache; stable IDs, DST, overrides, missed-bell grace, dedupe | UNIT (+ server core from the earlier session) | `PublishTests`, `BellSchedulerTests`, `LargeManifestTests`. **Up to 0.9.4 no real schedule was ever accepted**: a school week is well over 32 KB and clients dropped larger messages (found at the first school, 25 computers at revision 0; fixed in 0.9.5, see PROTOCOL.md `max_message_bytes`). A scheduled bell firing on Windows was not watched live. |
| Ring now (UI button, sound choice, everyone/group/selected) | WIN, UNIT | audible 2.92 s, 768 ms after the press; dedupe, expiry and hash tests |
| Customizable bell sounds (upload, replace std-bell, delete) | UNIT, UI | `ExtensionTests`. Replacing a sound needs a schedule re-publish for cached bells. |
| Volume override: master and app session, restore after success / crash | WIN | gate-a-matrix.txt: 30%+muted → 80% → 30%+muted; app-session mute captured and restored |
| Locked session plays | WIN | |
| Tray app killed mid-announcement → service continues and restores | WIN | |
| Signed out (after a sign-in) → service's session-0 renderer plays | WIN | |
| Service killed mid-announcement → SCM restart restores volume | WIN | |
| No-user boot (nobody signed in since boot) | **WIN: silent** | Windows renders session-0 audio as digital silence before the first sign-in, including Windows' own SoundPlayer. The client now reports `no_renderer (no_user_since_boot)` and receipts are `unknown`, never `completed`. Needs testing on real hardware. |
| Missing configured speaker → honest failure | WIN | `failed: output_missing`, state `missing_speaker` |
| Recorded announcement (record/upload, preview, send, chime) | WIN | waveform match 0.98 vs source |
| Live push-to-talk from the browser (Gate B) | WIN | Chrome with a fake mic file → server → Windows output; ~333 ms capture→speaker; ready 5.6 s after press |
| Talk lease, single talker, lease expiry, stall stop | UNIT (+WIN stall path) | |
| Reconnect with backoff, heartbeats, keep-alive timeouts | SIM, UNIT | 500 devices back in 1.5 s |
| Keep-awake API + service thread | UNIT | **not exercised on Windows** |
| Office UI: simple Home (who, hold to talk, ring, next bells), Announce, Bells (schedules, calendar, publish, sounds), Endpoints, History, /admin | UI built and screenshot-checked | `docs/evidence/ui/*.png` |
| Endpoint label user-first `User (WIN11-VM)`; device ID ≠ display name | WIN | |
| Per-device settings (speaker chooser, volume, cap, room, groups) | UNIT | the speaker list comes from the device |
| Tray history (schoolwide text only; targeted office-only) | UNIT | the window was not visually checked on Windows |
| Windows installer: silent args, Apps & Features entry, ACLs, service recovery, logon task, repair, clean uninstall | WIN | `docs/evidence/windows-installer.txt` |
| Client updates pushed by the server (signed, staged, rollback runner) | WIN, UNIT | 0.9.1 → 0.9.2 rolled out from /admin to the VM and reached `installed`. **Rollback of a broken client build was not run.** |
| Server install.sh on Ubuntu (fresh, repeat, HTTPS, units, permissions) | UBU | `docs/evidence/ubuntu-install.txt` |
| Server self-update from GitHub (check, apply, root updater, signature, backup, health, rollback) | UBU (GitHub API mocked) | `docs/evidence/ubuntu-lifecycle.txt`: 0.9.0→0.9.1 done, tampered refused, broken 0.9.2 rolled back with DB restore; the "Regular Day" schedule survives repeat install, update, rollback, uninstall and reinstall. |
| Backup / restore staging | UNIT | restore applies at the next start |
| Whisper transcription | **NO (omitted at the owner's request)** | Adapter and queue exist and are off (`transcription_enabled=false`). Building whisper.cpp was blocked by the session's safety policy. |
| Load: 100 and 500 simulated endpoints | SIM | §6 |
| Authenticode signing | NO | no certificate |

## 6. Measured results (single run each, lab hardware: 6-core host also running the 3 GB Windows VM)

| | 100 | 500 |
|---|---|---|
| all connected | 0.53 s | 1.27 s |
| ring delivery p95 | 42 ms | 157 ms |
| live egress (PCM16 16 kHz) | 25.7 Mbps | 126.9 Mbps |
| live transport latency p95/p99 | 4/6 ms | 31/194 ms (91/469 ms in a rerun under contention) |
| server CPU during live, avg/max (% of 1 core) | 8/21 | 27–33/54–108 |
| server RSS max | 309 MB | 348 MB |
| mass reconnect | 0.67 s | 1.5–2.0 s |

Transport only: simulated devices don't render audio. The single real endpoint measured about 333 ms
browser→speaker. Raw JSON: `docs/evidence/load/`.

## 7. Lab environment (on this machine)

- **Windows VM** `schoolbell-lab` (libvirt `qemu:///system`): a copy-on-write **overlay of
  `~/VMs/win11/win11.qcow2`**. Do not boot the original `win11` while this overlay is in use, or the
  overlay becomes invalid. Delete `~/VMs/schoolbell-lab` and `virsh undefine schoolbell-lab --nvram` when finished.
  Account `User`, blank password, auto sign-in. Audio output is recorded to `~/VMs/schoolbell-lab/audio/out.wav`.
  Shared folder: `~/VMs/schoolbell-lab/share` = `Z:`. Run commands with `scripts/lab/vmexec.py -c '<PowerShell>'`.
  A SchoolBell client is installed (last updated to 0.9.2 by the client-update test).
- **Lab server** (the owner is testing against it): **ephemeral data under `/tmp/claude-1000/.../scratchpad/lab`**.
  Start/stop with `scripts/lab/lan-up.sh` / `lab-down.sh`. Office URL https://10.1.0.191:8443 (also 10.1.16.63,
  192.168.122.1), CA `.../lab/lab-ca.crt`. Accounts: a lab-only office account (min length lowered
  via `SchoolBell__MinPasswordLength=6`) and `labadmin` (password file in the lab dir). The host's `ufw`
  must allow 8443/tcp. The binary is `dist/publish/server-lan` (built before the sound-packaging fix; this lab
  DB already has its sounds).
- **Load-test server**: `.../scratchpad/loadlab` on 127.0.0.1:8444, with 500 simulated device records.
- **Ubuntu container** `sb-ubuntu` (podman, image `localhost/schoolbell-ubuntu2404-systemd`, 443→127.0.0.1:9443).
- Owner's test kit: `/home/nbull/SchoolBell-test/` (installer, CA, README.txt). Its enrollment token was revoked
  before publishing; download a fresh enrollment file from Admin to reuse it.

## 8. Known issues and limits (also see KNOWN-ISSUES.md)

1. **Pre-login audio**: silent until someone has signed in once since boot (Windows 11 25H2 VM). Reported
   honestly. Schools need classroom PCs signed in (locked is fine) or must verify their hardware.
2. **First start after install/update is slow** (97 s on the 3 GB VM while Defender scans about 120 MB). The
   installer and update runner pre-warm, then start the service with retries.
3. **Manifest signing is HMAC** with a key derived from the per-school trust pin (from the earlier session).
   A stolen device config could forge manifests. Move to Ed25519/ECDSA (the release-key code exists).
4. Slow consumers: the backlog builds up in TCP and proxy buffers the server can't see; renderers discard audio
   older than 2 s, so nothing plays late, but bandwidth is wasted. Real flow control needs device acks.
5. Re-enrolling by the **same computer name** reuses the device record (settings and groups kept, outbox
   cleared). Two machines with the same name will fight over one identity; names must be unique.
6. Client updates: a successful update is tested on Windows; **rollback of a broken client build is not**. It
   depends on `ProgramData\SchoolBell\updates\installed-setup.exe` (saved by the installer, confirmed present).
7. Unsigned installer (SmartScreen warning). Whisper is off. Keep-awake not tested on Windows.

## 9. Next tasks, in order of impact

1. **Make the repository public** (owner), then on the school server confirm **Admin → Server software →
   Check GitHub now** finds v0.9.4 (or the next release) as a verified release.
2. **Real hardware**: classroom PC + speakers. No-user boot, headphones plugged/unplugged, HDMI output
   changes, locked/RDP/fast-user-switch, keep-awake (powercfg /requests), scheduled bell timing spread on two
   or more PCs.
3. **Client update rollback**: roll out a deliberately broken signed client build to the VM and confirm
   `rolled_back` with the previous version running again.
4. Replace HMAC manifest signing with asymmetric signatures; then re-enable Whisper (build pinned
   whisper.cpp v1.9.5 with `GGML_NATIVE=OFF GGML_CPU_ALL_VARIANTS=ON`; model `ggml-small.en.bin`
   sha256 `c6138d6d…1e5d`, already downloaded to `.build/whisper/models/`), plus a `schoolbell-whisper.service`.
5. Code-sign `SchoolBellSetup.exe` once the school has a certificate.
