# SchoolBell dependency and license inventory

Generated from the committed lock files (`*/packages.lock.json`, `web/package-lock.json`).
SchoolBell's own code: see the repository license.

## Shipped runtime components

| Component | Version | License | Where |
|---|---|---|---|
| .NET runtime + ASP.NET Core (self-contained) | 10.0.13 | MIT | server bundle, Windows client |
| Windows Desktop runtime (WinForms) | 10.0.13 | MIT | Windows client |
| SQLite (via SQLitePCLRaw bundle_e_sqlite3) | bundled | Public domain (SQLite) / Apache-2.0 (wrapper) | server |
| Caddy | 2.11.7 | Apache-2.0 | server bundle `bin/caddy` (+ `caddy.LICENSE`) |
| Inno Setup (installer engine stub inside SchoolBellSetup.exe) | 6.7.3 | Inno Setup License (free, incl. commercial) | Windows installer |
| whisper.cpp + small.en model | not shipped in this pilot | MIT (code) / MIT (model) | — (transcription omitted for now) |

## NuGet packages (resolved)

| Package | Version | License | Used by |
|---|---|---|---|
| Microsoft.Data.Sqlite | 10.0.13 | MIT (.NET Foundation / Microsoft) | Server |
| Microsoft.Data.Sqlite.Core | 10.0.13 | MIT (.NET Foundation / Microsoft) | Server |
| Microsoft.Extensions.Configuration | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Configuration.Binder | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Configuration.CommandLine | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Configuration.FileExtensions | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Configuration.Json | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Configuration.UserSecrets | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.DependencyInjection | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Audio, Client.Core, Client.Desktop, Client.Service |
| Microsoft.Extensions.Diagnostics | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.FileProviders.Physical | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.FileSystemGlobbing | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Hosting | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Logging | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Logging.Abstractions | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Audio, Client.Core, Client.Desktop, Client.Service |
| Microsoft.Extensions.Logging.Configuration | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Logging.Console | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Logging.Debug | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Logging.EventLog | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Logging.EventSource | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Options | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| Microsoft.Extensions.Primitives | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| NAudio.Core | 2.2.1 | MIT | Client.Audio, Client.Desktop, Client.Service |
| NAudio.Wasapi | 2.2.1 | MIT | Client.Audio, Client.Desktop, Client.Service |
| NodaTime | 3.1.11 | Apache-2.0 | Server |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | Apache-2.0 | Server |
| SQLitePCLRaw.core | 2.1.13 | Apache-2.0 | Server |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | Apache-2.0 | Server |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | Apache-2.0 | Server |
| System.Diagnostics.EventLog | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |
| System.ServiceProcess.ServiceController | 10.0.13 | MIT (.NET Foundation / Microsoft) | Client.Service |

## Web UI (npm, shipped as compiled static files)

| Package | Version | License |
|---|---|---|
| react | 19.2.9 | MIT |
| react-dom | 19.2.9 | MIT |
| scheduler | 0.27.0 | MIT |

## Web build tools (not shipped)

| Package | Version | License |
|---|---|---|
| @oxc-project/types | 0.156.0 | MIT |
| @rolldown/binding-android-arm-eabi | 1.2.16 | MIT |
| @rolldown/binding-android-arm64 | 1.2.16 | MIT |
| @rolldown/binding-darwin-arm64 | 1.2.16 | MIT |
| @rolldown/binding-darwin-x64 | 1.2.16 | MIT |
| @rolldown/binding-freebsd-x64 | 1.2.16 | MIT |
| @rolldown/binding-linux-arm-gnueabihf | 1.2.16 | MIT |
| @rolldown/binding-linux-arm64-gnu | 1.2.16 | MIT |
| @rolldown/binding-linux-arm64-musl | 1.2.16 | MIT |
| @rolldown/binding-linux-ppc64-gnu | 1.2.16 | MIT |
| @rolldown/binding-linux-s390x-gnu | 1.2.16 | MIT |
| @rolldown/binding-linux-x64-gnu | 1.2.16 | MIT |
| @rolldown/binding-linux-x64-musl | 1.2.16 | MIT |
| @rolldown/binding-openharmony-arm64 | 1.2.16 | MIT |
| @rolldown/binding-win32-arm64-msvc | 1.2.16 | MIT |
| @rolldown/binding-win32-x64-msvc | 1.2.16 | MIT |
| @rolldown/pluginutils | 1.0.1 | MIT |
| @types/react | 19.2.18 | MIT |
| @types/react-dom | 19.2.7 | MIT |
| @vitejs/plugin-react | 6.1.2 | MIT |
| csstype | 3.2.3 | MIT |
| detect-libc | 2.1.2 | Apache-2.0 |
| fdir | 6.5.0 | MIT |
| fsevents | 2.3.3 | MIT |
| lightningcss | 1.33.0 | MPL-2.0 |
| lightningcss-android-arm64 | 1.33.0 | MPL-2.0 |
| lightningcss-darwin-arm64 | 1.33.0 | MPL-2.0 |
| lightningcss-darwin-x64 | 1.33.0 | MPL-2.0 |
| lightningcss-freebsd-x64 | 1.33.0 | MPL-2.0 |
| lightningcss-linux-arm-gnueabihf | 1.33.0 | MPL-2.0 |
| lightningcss-linux-arm64-gnu | 1.33.0 | MPL-2.0 |
| lightningcss-linux-arm64-musl | 1.33.0 | MPL-2.0 |
| lightningcss-linux-x64-gnu | 1.33.0 | MPL-2.0 |
| lightningcss-linux-x64-musl | 1.33.0 | MPL-2.0 |
| lightningcss-win32-arm64-msvc | 1.33.0 | MPL-2.0 |
| lightningcss-win32-x64-msvc | 1.33.0 | MPL-2.0 |
| nanoid | 3.3.20 | MIT |
| picocolors | 1.1.1 | ISC |
| picomatch | 4.0.7 | MIT |
| postcss | 8.5.32 | MIT |
| rolldown | 1.2.16 | MIT |
| source-map-js | 1.2.2 | BSD-3-Clause |
| tinyglobby | 0.2.17 | MIT |
| typescript | 6.0.3 | Apache-2.0 |
| vite | 8.3.4 | MIT |
