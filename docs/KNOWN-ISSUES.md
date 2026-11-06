# SchoolBell known issues and limits

Current as of release 0.9.9. `HANDOFF.md` §5 lists what was verified and how.

1. **Silent before the first sign-in since boot.** Windows 11 (25H2, tested in a VM) plays session-0
   audio as digital silence until someone has signed in once since boot. A locked or signed-out
   computer plays normally after that. The client reports `no_renderer (no_user_since_boot)` and its
   receipts are `unknown`, never `completed`. Keep classroom computers signed in (locked is fine), or
   test the school's own hardware.
2. **Schedule manifests are signed with a school-wide HMAC key** derived from the per-school trust pin.
   Someone who copies one enrolled computer's configuration could forge a schedule that every computer in
   the school accepts. The fix is asymmetric signatures (the ECDSA release-key code already exists); it
   needs a protocol revision.
3. **The installer is not Authenticode-signed** (no code-signing certificate), so SmartScreen may warn.
   Release integrity comes from `SHA256SUMS` and the signed `release-manifest.json`.
4. **The first service start after an install or update is slow** (97 s on a 3 GB VM while Defender
   scans about 120 MB). The installer and update runner pre-load the files and retry the start.
5. **Crash window for bells.** A bell can be claimed and then not heard if the computer crashes between
   the claim and the speaker. Those show as `started` or `unknown`, never `completed`. Delivery is
   at most once per bell.
6. **Missed bells are never replayed.** A computer that was off, asleep or offline when a bell was due
   records it as `expired` rather than ringing late; the grace is 6 seconds (2 before 0.9.8). Admin → Log names each
   computer and bell that expired. (Up to 0.9.6 a client bug also expired every bell that came a few
   minutes after one that played; fixed in the 0.9.7 client.)
7. **Slow networks.** A computer that cannot keep up with live audio builds a backlog in TCP and proxy
   buffers the server cannot see. Renderers discard audio older than 2 seconds, so nothing plays late,
   but the bandwidth is wasted. Real flow control needs device acknowledgements.
8. **Computer names must be unique.** Re-enrolling by the same name reuses that device record
   (settings and groups kept), so two machines with one name fight over one identity.
9. **Windows client update rollback is untested.** A successful update (0.9.1 → 0.9.2) was pushed and
   installed on the lab VM. Reinstalling the previous version after a broken update relies on
   `C:\ProgramData\SchoolBell\updates\installed-setup.exe`, which the installer saves, but a broken
   client build has not been rolled out to test it.
10. **Server self-update needs a public repository.** The update check reads GitHub Releases without a
    token.
11. **Not yet tested on Windows:** keep-awake, a scheduled bell firing on a real classroom PC (ring-now
    uses the same playback path), and output changes on real hardware (headphones, HDMI, RDP, fast user
    switching).
12. **Transcripts are machine generated.** The bundled local Whisper `small.en` model is English-only
    and can mishear names, room numbers and speech over noisy microphones. The office can correct the
    final transcript; the original is retained. Live partials may lag on slower server CPUs.
13. **Daylight saving.** Bell times that do not exist on the change day are skipped (logged in the
    publish report); ambiguous times ring once, at the first occurrence. Encode anything else as a date
    override.
14. **No device-count limit.** Resource use is bounded only by the configured queues. Watch
    `/api/diagnostics` (queue depths, dropped frames) as enrollment grows past the 500 simulated devices
    that were tested.
15. **Targeted announcement transcripts stay office-only** (there is no verified-identity integration),
    and a classroom speaker is not a private medium.
