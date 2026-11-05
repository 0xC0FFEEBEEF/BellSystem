# SchoolBell architecture

Protocol: `PROTOCOL.md` (v1, plus additive v1.1 messages). Status and evidence: `HANDOFF.md`.

## Shape

- **Server** (`src/Server`, ASP.NET Core, net10.0): listens on `127.0.0.1:5001` only. TLS/HTTPS is
  terminated by Caddy in front (private school CA or school certificate). SQLite (WAL) in
  `/var/lib/schoolbell`. No inbound connections to classroom machines — devices dial out (WSS).
- **Contracts** (`src/Contracts`, frozen): one shared definition of every control message, the
  signed schedule manifest, the SBMF binary media frame, the enrollment/auth crypto, and stable
  occurrence IDs. Server, clients, simulator, and tests all round-trip the same fixtures in
  `contracts/fixtures/`.
- **Client.Service** (net10.0-windows): owns identity, the control connection, the durable bell
  cache and claim ledger, receipt queue, keep-awake policy thread, the updater, and the local named
  pipe. Runs in session 0, so it survives sign-out; when no tray companion is connected it renders
  audio itself (silent before the first sign-in since boot, see KNOWN-ISSUES).
- **Client.Desktop** (WinForms tray): renders bells, recorded and live announcements for the
  signed-in console session, and shows status and the history window.
- **Client.Audio** (NAudio WASAPI): the playback engine both renderers use, with master endpoint
  and per-app session volume capture/override/restore and crash recovery. Exactly one renderer per
  machine: the console session's companion if present, otherwise the service.
- **Client.Core** (net10.0): everything testable without Windows APIs — reconnect backoff (full
  jitter, 30 s cap), bell scheduler (monotonic due times, 6 s missed-bell grace, expired batch,
  live-speech suppression), claim-before-play ledger, receipt queue, manifest verification,
  atomic sound cache, jitter buffer, keep-awake policy store with absolute offline expiry.
- **Web UI** (`web/`, React + TypeScript, built into `web/dist` and served by the server): Home
  (who hears it, hold to talk, ring now, next bells), Announce, Bells (schedules, calendar, sounds),
  Endpoints, History, `/admin`. Cookie session + CSRF
  double-submit; roles admin/operator/viewer enforced in the API, not just pages.

## Key invariants (from the plan, implemented at the protocol level)

- Schedules are compiled server-side into a **7-day signed manifest** with **stable occurrence
  IDs** (`sha256(template|period|target|date|kind)`): editing a time moves the bell, it does not
  duplicate it. DST: nonexistent local times are skipped, ambiguous run once (first mapping).
  Devices accept only newer revisions, ack, and keep executing their last accepted manifest
  until it expires — never guessing the calendar after expiry.
- **Bells are executed client-side** from the cached manifest: offline-capable, claim-before-play,
  at-most-once per occurrence, missed bells recorded `expired` (never replayed late), bells due
  during live speech recorded `suppressed`. The claim→speaker crash window is documented
  (`started`/`unknown`, never claimed `completed`): audible exactly-once is never claimed.
- **Live audio**: operator holds a talk lease (renewed, 300 s hard cap), browser captures
  16 kHz mono PCM in 20 ms SBMF frames over `/ws/mic`; server records the stream once and fans
  out through **bounded per-device queues** (1500 frames ≈ 30 s) — one slow device cannot stall
  others (it is dropped with `receive_queue_overflow`). Reconnect rejoins at the current frame
  only; expired live audio is never replayed.
- **Delivery honesty**: socket delivery ≠ audible playback. States per endpoint per occurrence:
  offered/ready/started/completed/failed/expired/suppressed/unknown — visible in history.
- **Enrollment**: short-lived single-use scoped token → unique per-device secret + per-school
  trust pin; no shared device password, no unauthenticated broadcast, no remote shell, no
  certificate bypass anywhere (`tools/EnrollTool` refuses plain http to non-loopback hosts).
- **Keep-awake**: dedicated service thread (`SetThreadExecutionState`), absolute expiring
  policies honored offline, display left on by default; cannot defeat manual sleep/lid/shutdown
  and never unlocks a screen (documented to operators).

## Updates

- **Server**: the app checks GitHub Releases, verifies the ECDSA P-256 signature of
  `release-manifest.json` against the pinned release key, and writes a request file. A root updater
  (systemd path unit) re-verifies everything, backs up, installs, health-checks and rolls back. The
  web app never runs as root.
- **Windows clients**: staged rollouts with per-device download links; each computer verifies the
  signature, waits for a quiet period, installs through an update runner, and reinstalls its previous
  version if the new one does not report healthy.
