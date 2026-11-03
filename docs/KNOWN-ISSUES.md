# SchoolBell known issues and honest limits

1. **Manifest signing is HMAC with a school-wide key.** `src/Server/PublishService.cs` signs the
   schedule manifest with `DeriveDeviceVerifyKey(trust_pin)` — the same key every device verifies
   with, because the trust pin is per-school. An attacker who obtains one enrolled machine's
   config (pin + secret) could forge a schedule manifest that every device in the school accepts.
   Fix candidates (post-pilot, protocol revision — coordinate before changing): per-device manifest
   signatures (outbox events are already per-device) or Ed25519 (server private key, public key in
   the enrollment file). Flagged honestly by the implementing agent; not silently changed.
2. **Windows audio path unproven in this environment.** The Windows lab was down for the whole
   build session (`win-a is not built yet`; bug report `20261103-0938-3e3e` closed
   `could_not_fix`). Gate A (real WASAPI playback/volume restore/locked-session/no-user-boot),
   Gate B (browser mic → server → Windows renderer), and SchoolBellSetup.exe install/repair/
   uninstall tests are **not executed**. Audio, service, and installer code compile (net8.0-windows
   via EnableWindowsTargeting) and client logic is tested on Linux, but audible output on real
   Windows is unverified.
3. **Crash window for bells.** A claim can exist without audible playback (crash between claim and
   speaker). Such occurrences surface as `started`/`unknown`, never `completed`. SchoolBell is
   at-most-once per occurrence; exactly-once audible delivery is never claimed.
4. **Offline expired bells are never replayed** (by design — a machine back from a week of downtime
   must not blast a day of late bells), but that means missed bells are silent: the office sees
   `expired` receipts, not sound.
5. **Targeted transcripts** are office-only (no verified-identity integration yet); local targeted
   history is cleared on identity change, but a classroom speaker is not a private medium.
6. **Deviation: .NET 8 instead of the recommended .NET 10 LTS** (lab availability). Pinned net8.0.
7. **Deviation: vanilla ES-module SPA instead of React+TS.** Same required behaviors; fewer build
   steps on the target box.
8. **systemd install path untested in a real systemd host** in this environment (no root on this VM;
   cluster nodes have no systemd). install.sh was written for and against Ubuntu 24.04 conventions
   and its binary-swap/state-preservation logic was exercised in sandbox mode; see TEST-RESULTS.
9. **Inno installer script uncompiled** until a Windows build host (the lab) is available — no
   SchoolBellSetup.exe artifact exists yet; build command is documented in OPERATIONS.
10. **Transcription pipeline has no engine wired by default**: whisper.cpp (`whisper.cpp` server,
    small.en) is built on the cluster and the server-side job queue is implemented and config-gated
    (`transcription_enabled`), but the loopback worker's end-to-end run status is recorded in
    TEST-RESULTS; transcripts are `pending` until the worker consumes them.
11. **DST policy**: nonexistent local bell times are skipped (logged in the publish report);
    ambiguous times fire once at the first mapping. A school that wants the second mapping must
    encode it as a date override.
12. **No device-count limit exists**; resource pressure is bounded only by visible configured
    queues — a school should watch `/api/diagnostics` (queue depths, dropped-frame counters) as
    enrollment grows toward the tested simulator ceiling.

## Update 2026-11-03 (second session) — see HANDOFF.md §8 for the current list

- Items 2, 6, 7, 9 and 10 above are superseded: Windows audio, the installer and the browser path were tested
  on a Windows 11 VM; the solution now targets .NET 10; the UI is React + TypeScript; SchoolBellSetup.exe is
  built with Inno Setup 6.7.3; Whisper is intentionally off for this pilot.
- New: pre-login (no-user-since-boot) audio is silent on Windows 11 25H2 — reported as no_renderer/unknown.
