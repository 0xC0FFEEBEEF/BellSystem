# SchoolBell

A locally hosted school bell and announcement workaround: an Ubuntu server hosts an office
web interface and `/admin`; Windows computers automatically play bells and announcements and
show delivery status through a tray application. Built against `uploads/schoolbell-application-plan.md`
(the governing spec) with `uploads/schoolbell-overnight-agent-brief.md` as the work brief.

**Status: working pilot.** Read `HANDOFF.md` for what is proven, what is untested, and what
is deliberately limited. This is not a certified emergency notification system.

## Components

| Path | What |
| --- | --- |
| `PROTOCOL.md` | Frozen protocol v1 (control WS, media frames, manifest, enrollment, receipts) |
| `src/Contracts/` | Shared C# types + crypto + SBMF frame codec (frozen; conformance-tested) |
| `src/Server/` | ASP.NET Core server: API, schedules, devices, announcements, media relay (loopback 5001; HTTPS via Caddy) |
| `src/Client.Service/` | Windows service: control connection, cache, bell ledger, keep-awake, named pipe |
| `src/Client.Desktop/` | Windows tray companion: WASAPI rendering, volume override/restore, history window |
| `src/Client.Core/` | Platform-neutral client logic (scheduler, ledger, jitter buffer, reconnect policy) |
| `web/` + `src/Server/wwwroot/` | Office web UI (dashboard, endpoints, schedules, announcements, history, `/admin`) |
| `install/server/install.sh` | Ubuntu 24.04 installer (`/opt` + `/etc` + `/var/lib` layout, systemd, Caddy) |
| `install/windows/SchoolBellSetup.inno` | Windows installer script (silent mode, `--server`, enrollment + CA inputs) |
| `tools/EnrollTool/` | Device enrollment CLI used by the Windows installer |
| `tools/Simulator/` | Simulated devices for load measurement (100 / 500) |
| `tests/` | Server, contract-fixture, and client-logic test suites |
| `docs/` | ARCHITECTURE, OPERATIONS, TEST-RESULTS, KNOWN-ISSUES, HANDOFF |

## Quick start (development)

```bash
# .NET 8 SDK required (repo pins net8.0)
export PATH=$HOME/.dotnet:$PATH
dotnet build SchoolBell.sln
dotnet test tests/Server.Tests && dotnet test tests/Client.Tests
dotnet run --project src/Server          # office UI on http://127.0.0.1:5001
# first run prints "SETUP CODE: ..." — complete admin setup in the browser, set the
# school timezone, create an enrollment token, then run build/publish.sh + installer.
```

## Deploy (Ubuntu 24.04 target)

```bash
bash build/publish.sh                      # dist/publish/ bundles
sudo bash install/server/install.sh --public-host bells.school.lan
journalctl -u schoolbell | grep 'SETUP CODE'   # first-run admin bootstrap
# Windows clients: SchoolBellSetup.exe /silent /SERVER=https://bells.school.lan \
#   /ENROLLMENTFILE=token.txt /CAFILE=ca-root.crt
```

Artifacts and checksums land in `dist/` (gitignored). See `docs/OPERATIONS.md`.

For Windows deployment, **Admin → Enrollment → Download Windows Installer** creates
one EXE with enrollment and the school CA certificate included. The page shows the
PDQ parameters; the server address stays a parameter. See [Windows deployment](docs/windows-deployment.md).
