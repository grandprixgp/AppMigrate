# Changelog

## 1.2.0

### New: Nearby devices

- Find other PCs running AppMigrate on your local network, pair them with a 6-digit code, and pull the old PC's app list straight onto the new one. No manifest file needed.
- See what's **missing here**, **already here**, and **only on this PC**, then send the missing apps straight to the install plan.
- Paired PCs are remembered, so the code is only needed once.
- **Connect by address** for networks where discovery is blocked.
- A warning (with a shortcut to the right setting) when the sharing PC is on a network Windows considers **Public**.
- Under the hood: mutual TLS with per-device certificates (key encrypted for your Windows account), commit-reveal pairing codes, local-subnet-only connections, a Private-profile firewall rule scoped to the exe, read-only access, and sharing switches itself off after 30 idle minutes.

### Fixes

- **Non-English Windows:** WinGet's table output is now parsed without relying on English column names. Previously the whole WinGet inventory could silently disappear on a localized system. Tested against WinGet's real German, Japanese, Korean, and Traditional Chinese headers.
- **Install results** are judged by WinGet's return codes instead of English messages. Installs that need a restart are reported as **Reboot required** rather than failed.
- **"Already installed"** is decided by package ID when one exists, so Python 3.11 no longer causes Python 3.12 to be skipped (the same goes for JDKs, .NET SDKs, and so on).
- **Name matching** is stricter: an exact name wins, and a loose match is only accepted when it's the only candidate.
- **Truncated package IDs** in `winget list` output are repaired from `winget export` (only when truncation actually happens, so scans stay quick).
- **Cancel is safe:** the install log is streamed to disk as it goes, and the report and manual-actions files are always written, even after a cancel. The report also counts the apps that never got their turn.
- **Manifests are checked when loaded:** invalid package IDs become manual items and unexpected WinGet flags are removed. Extra arguments are now split with proper quote handling.
- Version pinning skips WinGet's range-style versions (like `> 270.4`) instead of failing the install.

### Behind the scenes

- A new xUnit test project (95 tests), which CI now runs on every push.
- A rewritten README, and this changelog.

## 1.1.0

- Initial public release.
