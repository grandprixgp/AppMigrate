# AppMigrate

<img src="docs/icon.png" width="76" align="right" alt="AppMigrate icon" />

A fast, native Windows application migration tool. Scan the old PC, choose which apps to keep, save a portable manifest, then rebuild them on a fresh Windows install.

![AppMigrate dark theme](docs/screenshot-dark.png)

## Download

Grab the latest self-contained Windows build from the [Releases page](https://github.com/grandprixgp/AppMigrate/releases/latest). The release build does not require a separate .NET runtime.

## Why this build

- **Native WPF on .NET 8** - no embedded PowerShell, no script extraction, no execution-policy bypass.
- **Live parallel inventory** - Registry, AppX, and WinGet are scanned concurrently. Rows appear while the scan is still running.
- **Parallel package resolution** - unresolved classic apps are matched to WinGet IDs with a bounded 8-way worker pool.
- **Fast, practical UI** - virtualized table, instant search, filter chips, selection summaries, and dark/light themes.
- **Native installer plan** - checks installed apps, shows skips/manual items, and streams WinGet output.
- **Portable manifest** - a readable `apps.json` plus `apps.csv` and `apps.md` for review.
- **No telemetry** - the app only talks to the local Windows APIs and the WinGet source.

## Requirements

- Windows 10 2004+ or Windows 11.
- WinGet / **App Installer** installed and working (`winget --version`).
- .NET 8 Desktop Runtime **only if** you use the framework-dependent build. The self-contained release does not need a separate runtime.

## Use it

### 1. Collect apps from the old PC

1. Start **AppMigrate**.
2. Click **Scan this PC**.
3. Watch the list populate live from Registry, AppX, and WinGet.
4. Search, filter, and check the apps you want.
5. Click **Save manifest** and keep `apps.json`.

The app writes:

- `apps.json` - canonical manifest.
- `apps.csv` - spreadsheet-friendly copy.
- `apps.md` - human-readable checklist.

### 2. Move the manifest

Copy `apps.json` to the new PC. The app auto-loads `apps.json` from its own folder on startup, or use **Load manifest...**.

Or skip the file entirely if both PCs are on the same network:

1. On the old PC, open **Nearby devices** and click **Share this PC**. The first time, Windows asks once (UAC) to allow AppMigrate through the firewall on private networks.
2. On the new PC, open **Nearby devices**, pick the old PC, and click **Connect and compare**.
3. Both screens show the same 6-digit code. If they match, click **Codes match, confirm** on the old PC.
4. Review what's missing on the new PC, then click **Install missing apps...** to send them to the Install tab.

Paired PCs remember each other, so later connections skip the code. If the old PC doesn't show up (guest Wi-Fi, VPN, a network set to Public), type the address shown on the old PC under **Connect by address**.

Sharing is read-only: a paired PC can download the app list and nothing else. The connection is TLS between per-device certificates, the pairing code comes from a commit/reveal exchange so it can't be guessed by someone in the middle, and anything received is sanitized before it can reach WinGet. Sharing turns itself off after 30 minutes without requests.

### 3. Install on the new PC

1. Open the **Install apps** tab.
2. Click **Preview plan**.
3. Review the install/skip/manual rows.
4. Click **Install selected** and confirm.

Options:

- **Force reinstall** - installs even when the app is already present, passing `--force` to WinGet.
- **Allow interactive** - stops passing `--silent` / `--disable-interactivity` for installers that need UI.
- **Skip Store** - leaves Microsoft Store entries as manual actions.
- **Match scope** - passes the recorded `user` / `machine` scope.
- **Pin versions** - tries the recorded version instead of the current WinGet package.

Reports and logs are written beside the manifest:

```text
install-log-YYYYMMDD-HHmmss.txt
install-report-YYYYMMDD-HHmmss.json
manual-actions-YYYYMMDD-HHmmss.txt
```

The log is written as the install runs, and **Cancel** stops after the current app and still writes a report of what ran and what didn't. Apps that installed but need a restart are reported as such instead of as failures. Loaded manifests are checked first: invalid package IDs become manual items, and unusual WinGet arguments are flagged in the log.

## Build from source

Prerequisites: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build AppMigrate.sln -c Release
dotnet run --project .\src\AppMigrate
```

### Publish

Self-contained single-file exe (recommended for releases; no runtime install needed):

```powershell
.\build.ps1
```

Framework-dependent single-file exe (smaller; requires the .NET 8 Desktop Runtime):

```powershell
.\build.ps1 -FrameworkDependent
```

If PowerShell blocks the build script, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Outputs go to `dist\win-x64\AppMigrate.exe` and `dist\AppMigrate-win-x64.zip`.

For ARM64:

```powershell
.\build.ps1 -Runtime win-arm64
```

## Project layout

```text
src/AppMigrate/
  Models/          app records, manifest schema, classification rules
  Services/        registry, AppX, WinGet, scan, manifest, install, theme
  ViewModels/      bindable state, commands, filtering
  Themes/          dark/light palettes and controls
  MainWindow.xaml  native WPF UI
  Assets/          generated app icon
tools/Make-Icon.ps1  regenerates the multi-size .ico and preview PNG
cli/                 optional PowerShell automation scripts
build.ps1            build/publish helper
```

## How the scan works

`ScanService` starts three independent inventory tasks:

1. **Registry** - HKLM 64/32-bit + HKCU uninstall entries.
2. **AppX** - `PackageManager.FindPackagesForUser`, with friendly display names, versions, publishers, and package families.
3. **WinGet** - one `winget list` call, parsed into package IDs, versions, and sources.

Each source's results are merged as soon as it finishes and published to the UI, so the table grows while the scan continues. Unresolved classic apps are then matched to WinGet IDs concurrently, with progress updates. A final pass deduplicates by package ID, applies category/default-selection rules, and marks manual items explicitly.

The installer uses `winget install` directly and never executes a script.

## Manifest compatibility

The native app uses the same JSON shape as the included CLI scripts, including `include`, `defaultInclude`, `installType`, IDs, versions, and manual notes. The GUI loads JSON; the CLI can also read `apps.csv`.

## Optional CLI scripts

The `cli/` folder contains the earlier PowerShell workflow for automation or scripted environments:

```cmd
cli\Export-Apps.cmd -Review
cli\Install-Apps.cmd -Plan
cli\Install-Apps.cmd
```

These scripts are separate from the GUI and do use a process-scoped execution-policy bypass. The native app does not.

## Limitations

- Application data, browser profiles, SSH keys, licenses, and settings are not migrated.
- Drivers, firmware, and Windows features are not installed as apps.
- Games managed by Steam/Epic/GOG need to be downloaded again through their launchers.
- Portable apps without an uninstall entry are invisible to the registry scan.
- Microsoft Store installs may require sign-in or a user prompt.
- Download-based installer entries are currently handled by the CLI, not the GUI.
- Version pins are best-effort; WinGet usually only keeps the current manifest.
- A reboot may be required after installers return `3010`.

## Security

This is a native C# application. It does not embed or drop PowerShell scripts, and it does not change your execution policy. It invokes `winget.exe` for inventory and installation.

Release binaries are unsigned unless you add code signing. Expect SmartScreen to warn on an unsigned download; for public releases, sign the executable with a code-signing certificate.

## CI

`.github/workflows/build.yml` builds the solution, runs the tests, and publishes a framework-dependent `win-x64` artifact on every push and pull request.

Run the tests locally with:

```powershell
dotnet test AppMigrate.sln
```
