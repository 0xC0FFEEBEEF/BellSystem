# Reconstructed spec (plan files did not arrive)

The chat uploaded `schoolbell-application-plan.md` and
`schoolbell-overnight-agent-brief.md`, but only the citation lines reached this
machine — the file contents were never written to `uploads/` or anywhere else
(searched the whole filesystem and the session database: only the filenames
exist). Per the user's instruction to "build it all", this application was
built from the project name, the user's answers, and standard school-bell-system
features:

Assumed requirements (all implemented):
1. Web admin console: bell schedules (periods with start/end/pre-bell), weekly
   calendar with per-date overrides, holidays, settings (school name, timezone,
   per-bell-type sounds), live "next bell" countdown, client status, activity log.
2. Resolution priority: holiday > date override > weekday default; an override
   may explicitly mean "no bells".
3. A classroom bell client (agent) that syncs the schedule and server time,
   corrects for clock skew, plays sounds at exact bell times, heartbeats, and
   reports fired bells. Windows packaging via PyInstaller (untested on Windows,
   as the user forgave).
4. Server: Python + SQLite; empty at first start (no seeded data).

If the real plan differs, re-attach it and the gaps will be reconciled.
