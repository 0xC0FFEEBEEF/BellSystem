# SchoolBell architecture

Governing spec: `uploads/schoolbell-application-plan.md`. Protocol: `PROTOCOL.md` (v1, frozen).

## Shape

- **Server** (`src/Server`, ASP.NET Core, net8.0): listens on `127.0.0.1:5001` only. TLS/HTTPS is
  terminated by Caddy in front (private school CA or school certificate). SQLite (WAL) in
  `/var/lib/schoolbell`. No inbound connections to classroom machines — devices dial out (WSS).
- **Contracts** (`src/Contracts`, frozen): one shared definition of every control message, the
  signed schedule manifest, the SBMF binary media frame, the enrollment/auth crypto, and stable
  occurrence IDs. Server, clients, simulator, and tests all round-trip the same fixtures in
  `contracts/fixtures/`.
- **Client.Service** (net8.0-windows): owns identity, the control connection, the durable bell
  cache and claim ledger, receipt queue, keep-awake policy thread, and the local named pipe.
  Session 0 — survives sign-out and no-user boot (connection-side only).
- **Client.Desktop** (WinForms tray): owns audio (NAudio WASAPI): bells, recorded playback, live
  announcement rendering, master endpoint volume capture/override/restore, chime, history window.
  Exactly one renderer per machine (service arbitrates the pipe slot).
- **Client.Core** (net8.0): everything testable without Windows APIs — reconnect backoff (full
  jitter, 30 s cap), bell scheduler (monotonic due times, 2 s missed-bell grace, expired batch,
  live-speech suppression), claim-before-play ledger, receipt queue, manifest verification,
  atomic sound cache, jitter buffer, keep-awake policy store with absolute offline expiry.
- **Web UI** (`src/Server/wwwroot`): office pages — dashboard, endpoint directory, schedules and
  calendar, live push-to-talk + record-then-send, history, `/admin`. Cookie session + CSRF
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

## Deliberated deviations from the plan's recommendations

- **.NET 8 LTS** instead of the plan's recommended .NET 10: the available lab ships .NET 8.
- **Vanilla ES-module SPA** served from `wwwroot` instead of React+TS: fewer build moving parts
  on the target box; required behaviors are unchanged. See KNOWN-ISSUES.
- See KNOWN-ISSUES for the manifest HMAC signing model and the lab-blocked Windows tests.
