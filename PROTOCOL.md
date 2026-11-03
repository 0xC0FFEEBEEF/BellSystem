# SchoolBell Protocol v1 (frozen for pilot)

Version: `1`. All control messages are JSON envelopes over WSS; media is binary over a
separate WSS. Both connections are **outbound from the device** to the configured server
(HTTPS/WSS on 443 in production; lab: Caddy 443 → app on loopback 5001).
JSON numeric times are **Unix milliseconds UTC** unless a field name says `_hm`/local.

## 1. Identifiers and labels

- `device_id`: random UUID issued at enrollment, stable for the device lifetime.
- `device_secret`: 32-byte random per-device key issued at enrollment (hex). Never reused
  across devices, never logged, never a shared secret.
- `event_id`: UUID per control message (idempotency).
- `occurrence_id`: stable bell identity — see §6. Distinct from `cursor`.
- `announcement_id`: UUID per announcement (live or recorded).
- `revision`: monotonically increasing integer schedule revision.
- `cursor`: last control `event_id` the device durably processed (resync only, never a playback id).
- Display labels are never credentials: `Active user (computer)` e.g. `james.doe (rhs-9393)`;
  `No signed-in user (rhs-9393)`. Server-side identity/authorization never derives from them.

## 2. HTTP (pre-connection)

All office UI under cookie/session auth with roles `admin|operator|viewer` (§9).

### Enrollment — POST /api/enroll
Request: `{ "token": "<short-lived scoped enrollment token>", "device_name": "rhs-9393" }`
Response 201: `{ "device_id": uuid, "device_secret": hex, "server_trust_pin": hex }`
- Token: created in `/admin`, single-use or N-uses with absolute expiry (default 24h), scoped
  to a group (optional). Server stores only a hash of the token. Responses and token values
  are never logged. Re-enrollment of an unknown device with a valid token issues fresh
  credentials; a revoked device is rejected with `403 revoked`.
- `server_trust_pin`: random per-school value distributed only through the installer /
  enrollment file; the device presents it on connect and the server verifies it. It binds
  the device to *this* server instance (a stolen credential cannot be used against a
  different SchoolBell server). TLS trust itself comes from the CA, not from the pin.

### Trust
Server is reached at `https://<server>` configured **explicitly** at install time. TLS is
validated against the school CA (installed at setup via `--ca-file`) or a system-trusted
certificate. There is no TOFU and no bypass: a TLS failure is a hard error surfaced to
diagnostics.

## 3. Control connection — WSS /ws/control

Connect, then handshake within 10 s:

```json
{ "v":1, "type":"auth.challenge" }                       // server → device (payload: {nonce: hex16})
{ "v":1, "type":"auth.response", "payload": {
    "device_id": "...", "client_version": "...",
    "proof": hex(HMAC-SHA256(device_secret, nonce + device_id)) } }
{ "v":1, "type":"auth.ok", "payload": { "server_time": 1793700000000 } }
```

Then the device sends a resync header (durable, from local storage):

```json
{ "v":1, "type":"device.resync", "payload": {
  "cursor": "last-event-uuid", "cache_revision": 7,
  "receipts": [ /* queued receipts, §8, max 500 */ ] } }
```

- Heartbeat: ping/pong every 10 s; status is `stale` after 30 s without contact.
- Reconnect: exponential backoff **with full jitter**, initial immediate, cap 30 s
  (configurable), indefinitely; a stable connection (≥60 s) resets the delay. Network-change
  events trigger one jittered immediate retry. No parallel reconnect loops.
- Server pushes control messages with `event_id`; device durably records the highest
  processed `cursor` and applies each event at-most-once.

## 4. Device status reports — type `device.status` (device → server, on change + every 60 s)

```json
{ "v":1, "type":"device.status", "event_id":"...", "payload": {
  "computer_name":"rhs-9393", "active_user":"james.doe", "active_user_sid":"S-1-5-...",
  "renderer": "ready|no_renderer|error", "renderer_reason":"signed_out|missing_output|crash",
  "output_device": "Speakers (Realtek)", "output_present": true,
  "master_muted": false, "master_volume": 42, "session_muted": false, "session_volume": 55,
  "clock_offset_ms": 812, "cache_revision": 7, "cache_expires_at": 1794304800000,
  "keep_awake": {"desired": true, "applied": true, "expires_at": 1793710800000},
  "last_playback": {"occurrence_id":"...","state":"completed","at":1793690400123},
  "companion": "running|hidden|missing" } }
```

Server derives endpoint state for the dashboard: `connected, ready_to_play, playing, muted,
missing_speaker, no_renderer, stale, offline, playback_failed`. A green connection never
implies audible sound.

## 5. Schedule publication

Office edits templates/overrides/holidays (draft state, server-side only). **Publish**
compiles the next 7 days for the school timezone into a manifest:

```json
{ "v":1, "type":"schedule.manifest", "event_id":"...", "payload": {
  "revision": 8, "school_tz": "America/Chicago",
  "generated_at": 1793690000000,
  "horizon_start": 1793667600000, "horizon_end": 1794272400000,
  "expires_at": 1794272400000,
  "sounds": [ { "id": "std-bell", "sha256": "...", "url": "/api/sounds/std-bell" } ],
  "occurrences": [ {
      "occurrence_id": "sha256hex...", "starts_at": 1793693400000,
      "bell_name": "Period 1 start", "kind": "start|end|warn",
      "sound_id": "std-bell", "volume_policy": "announcement", "duration_s": 3,
      "target_scope": {"all": true} | {"groups":[3]} | {"devices":[...]},
      "schedule_template": "Regular Day", "local_date": "2026-11-04", "local_hm": "08:30",
      "expires_at": 1794272400000 } ],
  "signature": "base64 HMAC-SHA256(manifest_signing_key, canonical_json)" } }
```

- Canonical JSON: keys sorted, UTF-8, no insignificant whitespace (clients verify before use).
- Manifest signing key lives only on the server; verification secret for devices is derived
  from `server_trust_pin` (documented KDF in contracts) and distributed via the enrollment
  file only. A manifest whose signature fails is rejected and reported (`manifest_rejected`).
- **Stable occurrence IDs:** `occurrence_id = sha256(template_id + period_index + target_key +
  local_date)`. Editing a time, sound, or bell name keeps the same `occurrence_id` (the bell
  *moves*, it does not duplicate). Deleting a period/date drops that id. New date overrides
  create ids for the override's compiled periods.
- DST: occurrences compile from **local** rules to absolute UTC. A nonexistent local time
  (spring-forward) is **skipped** and logged in publish report; an ambiguous local time
  (fall-back) runs **once at the first interpretation** unless the schedule sets `second`.
- Devices accept only `revision > local_revision`; acceptance is atomic: download sounds →
  verify sha256 → write cache to temp dir → atomic rename → ack:

```json
{ "v":1, "type":"schedule.ack", "payload": {"revision": 8, "accepted_at": ..., "expires_at": ...} }
```

The office schedule page lists devices by **accepted revision** — an offline device keeps
executing its last accepted manifest until it expires; after expiry the device stops
scheduling and reports `stale_cache` (it never guesses the calendar).

## 6. Bell execution (client-side, offline-capable)

Device-local rules (implemented in `Client.Service`, mirrored in the simulator):
- Monotonic timer: compute due time once (absolute UTC vs server-offset-corrected monotonic
  clock), then wait; never re-resolve wall clocks mid-wait.
- **Claim before playback:** durably write `{occurrence_id, claimed_at}` before starting
  audio; duplicate triggers (reconnect storm, second session) find the claim and do nothing.
- **Missed-bell grace: 2 s default** (configurable 0–60 s): if due-time passed > grace,
  record receipt `missed` (state `expired`) and do **not** replay late — after a long sleep,
  reboot, or outage the whole past horizon is skipped with one `expired` batch log.
- **Live-speech priority:** while a live announcement is active for this device, a due bell
  is **suppressed** (receipt `suppressed`) and never rings afterward. Recorded announcements
  queue within their expiry window.
- Crash window: a claim can exist with no audible playback (crash in between). Status is
  `started`/`unknown`, never `completed`. The system is at-most-once attempt per occurrence;
  exactly-once audible delivery is never claimed.

## 7. Announcements — offer, media, end

Offer (server → device, control WS):

```json
{ "v":1, "type":"announcement.offer", "payload": {
  "announcement_id": "uuid", "kind": "live|recorded",
  "audience_snapshot_id": "uuid", "priority": "live|normal",
  "format": {"codec":"pcm_s16le","sample_rate":16000,"channels":1,"frame_ms":20},
  "offer_expires_at": ..., "max_duration_s": 300, "chime": true,
  "source_label": "Office — all endpoints" } }
```

Device prepares output + volume override, replies `announcement.ready` (receipt `ready`) or
`announcement.reject` (reason: `renderer|output|volume_locked`). After the bounded
preparation window (default 5 s) the server opens the media channel:

- Media WSS: `/ws/media?announcement_id=...&device_id=...&token=...` authenticated by a
  short-lived, single-use media token issued on the control channel bound to
  (device_id, announcement_id). The server redeems the token only for that exact pair.
- Binary frame (all little-endian):

```
magic  "SBMF" (4B) | version u8=1 | flags u8 | announcement_seq u32 |
capture_ts_ms u64  | payload_len u16 (≤640) | PCM s16le mono 16kHz (320 samples @20ms)
flags bit0 = last frame of announcement
```

- Server records the source stream once; per-device send queue is **bounded** (default
  1500 frames ≈ 30 s ≈ 7.7 MB); a slow device is dropped with `failed(receive_queue_overflow)`
  — it cannot stall others.
- Client jitter buffer starts 200–400 ms, bounded max 2 s; stale/gapped frames are dropped
  (`gap` logged); on end (`flags.last` or `announcement.end`) valid queued audio drains,
  volume restores, receipt `completed` (or `failed(reason)`).
- Reconnect during a live stream: rejoin **only at the current frame** if the offer's target
  binding is still valid; expired/live audio older than the rejoin point is discarded, never
  replayed. Recorded announcements re-offer from the server outbox within expiry.
- `announcement.end` control message: `{announcement_id, ended_by: operator|lease|timeout|
  server|error, at}`.

Talk lease (office browser): `POST /api/announcements/lease` → `{lease_id, expires_at,
renew: "every ≤60 s"}`; one active lease schoolwide (pilot); conflicts rejected `409`.
Lease expires on non-renewal, tab closure (server-side socket close), or max 300 s default
(configurable). Expired lease ⇒ `announcement.end(ended_by: lease)` ⇒ clients stop and
restore volume within seconds.

## 8. Receipts (device → server, durable queue ≤ 5000, batched)

`{ "v":1,"type":"receipt","payload": {"occurrence_id"|"announcement_id": "...",
"state":"offered|ready|started|completed|failed|expired|suppressed|unknown",
"at": ms, "error_code": "output_missing|renderer|queue_overflow|..." } }`
Server stores per-target results; UI distinguishes socket delivery from audible completion.

## 9. Keep-awake

Server policy objects (`/admin`): `{policy_id, scope: device|group|all, mode: until|school_hours,
expires_at (absolute), display_off: false}`. Pushed via control:

```json
{ "v":1,"type":"keepawake.set","payload":{"policy_id":"...","expires_at":1793710800000} }
{ "v":1,"type":"keepawake.clear","payload":{"policy_id":"..."} }
```

Device applies on a **dedicated service thread**: `SetThreadExecutionState(ES_CONTINUOUS |
ES_SYSTEM_REQUIRED)`, cleared on the same thread; display-off left alone (separate option,
default off). Policy persisted with absolute expiry; re-evaluated on service start;
released at expiry **even offline**. Server shows desired / acknowledged (`keepawake.ack`) /
failed separately. Documented limits: does not defeat manual sleep, lid close, shutdown, or
group policy; never unlocks a screen.

## 10. Authorization & limits

- Roles: `admin` everything; `operator` announcements within group scope + schedule editing
  within scope; schoolwide broadcast is an explicit separate permission; `viewer` history/
  diagnostics only. Enforcement in APIs, not just pages. Login rate-limited; secure cookies;
  CSRF tokens for writes; Origin check on browser WS upgrades.
- Enrollment file (`--enrollment-file`) is deployment material: consumed/invalidated per
  policy, secret never logged. `--ca-file` optional when enterprise trust exists.
- Hard protocol bounds: control message ≤ 32 KB; media frame ≤ 1 KB; manifests compiled
  horizon ≤ 7 days; per-device queues bounded as above; all queue depths exposed via
  `/api/diagnostics` (visible configurable settings, no hidden device cap).
- No remote shell, no unauthenticated broadcast, no classroom-side capture.
- History authorization: schoolwide history visible to authorized staff; **targeted**
  transcripts require verified identity or an explicit room-history policy — otherwise
  office-only. Local cache on a shared PC must not leak the previous user's targeted
  transcripts (companion clears targeted content on identity change).

## 11. Media & recordings

Record-then-send: upload ≤ 5 min and ≤ 25 MB, mono 16 kHz WAV, converted server-side by a
restricted worker; stored under `/var/lib/schoolbell/audio` with sha256; queued to devices
as `kind: recorded` offers within expiry (default 24 h). Live source streams are recorded
server-side to the same store on completion. Transcript pipeline (§ plan 11) never blocks
playback.

## 12. Fixtures

`contracts/fixtures/*.json` carry representative messages of every type with the field set
frozen; server and clients must round-trip them in tests. Binary fixture:
`contracts/fixtures/frame_sbmf.bin` (one header + 320 samples).
