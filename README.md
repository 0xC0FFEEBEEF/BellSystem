# SchoolBell

A locally hosted school bell and announcement system. An Ubuntu server hosts the office web interface
and `/admin`. Windows classroom computers play scheduled bells, "ring now" bells, live push-to-talk and
recorded announcements, and report what they actually played through a tray application.

**Status: working pilot.** Read [`HANDOFF.md`](HANDOFF.md) for what is proven, what is untested, and
what is deliberately limited. This is not a certified emergency notification system.

## Install

Download the latest release from [Releases](https://github.com/0xC0FFEEBEEF/BellSystem/releases).

**Server (Ubuntu 24.04 LTS, x86-64):**

```bash
tar -xzf schoolbell-server-linux-x64.tar.gz
cd schoolbell-<version>
sudo bash install.sh --hostname bells.school.example --timezone America/Chicago
```

Open `https://bells.school.example/` and create the admin account with the one-time setup code the
installer prints. Re-running the installer keeps every setting and all data.

**Windows classrooms:** in **Admin → Windows deployment**, select **Download Windows Installer**. That
EXE includes enrollment and the school CA, and needs only the server address:

```
SchoolBellSetup.exe --silent --server bells.school.example
```

The generic `SchoolBellSetup.exe` from the release takes the files separately:
`--enrollment-file schoolbell-enroll.json --ca-file school-ca.cer`. See
[Windows deployment](docs/windows-deployment.md) and [Operations](docs/OPERATIONS.md).

**Updates:** the server checks this repository's GitHub Releases (**Admin → Server software**) and
installs only releases signed with the SchoolBell release key. Each server release carries the matching
Windows client, rolled out from **Admin → Windows client updates**.

## Components

| Path | What |
| --- | --- |
| `PROTOCOL.md` | Protocol v1 plus additive v1.1 messages (control WebSocket, media frames, manifest, enrollment, receipts) |
| `src/Contracts/` | Shared C# types, crypto and the SBMF media frame codec (conformance-tested) |
| `src/Server/` | ASP.NET Core server: API, schedules, devices, announcements, media relay, updates (loopback 5001; HTTPS via Caddy) |
| `src/Client.Service/` | Windows service: control connection, bell cache and ledger, keep-awake, named pipe, updater |
| `src/Client.Desktop/` | Windows tray companion: audio rendering, history window |
| `src/Client.Audio/` | NAudio WASAPI engine: master and app-session volume override and restore |
| `src/Client.Core/` | Platform-neutral client logic (scheduler, ledger, jitter buffer, reconnect policy) |
| `web/` | Office web UI (React + TypeScript): Home, Announce, Bells, Endpoints, History, Admin |
| `install/server/` | Ubuntu installer, root updater, systemd units, Caddy template |
| `install/windows/` | Inno Setup script (`SchoolBellSetup.iss`), update runner, tray logon task |
| `build/release.sh` | Builds a full signed release into `dist/` |
| `tools/EnrollTool/`, `tools/Simulator/` | Enrollment CLI used by the installer; simulated devices for load tests |
| `tests/` | Server, contract-fixture and client-logic test suites |
| `docs/` | Architecture, operations, Windows deployment, known issues, test evidence |

## Development

```bash
# .NET 10 SDK (version pinned in global.json) and Node.js for the web UI
dotnet build SchoolBell.sln
dotnet test tests/Server.Tests && dotnet test tests/Client.Tests
(cd web && npm ci && npm run build)      # office UI -> web/dist, served by the server
dotnet run --project src/Server          # http://127.0.0.1:5001, prints "SETUP CODE: ..." once
```

A full release (server bundle, Windows installer, signed manifest) is built with
`bash build/release.sh VERSION --iscc-vm <windows-vm>`, which compiles the installer with Inno Setup
inside a Windows VM and signs the manifest with the release key. See `HANDOFF.md` §3.

## License

SchoolBell is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version. See `LICENSE`. Third-party components and their
licenses are listed in `LICENSES.md`.
