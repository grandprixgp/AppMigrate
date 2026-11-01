# WinGet manifests

Drafts for `winget install TeaChest`, in the multi-file layout the community repo
uses. They are not submitted anywhere — that is the owner's decision.

## Check them

`winget validate` reads every file in a manifest folder as a manifest, and this
README isn't one, so validate a copy that holds only the yaml files:

```powershell
$tmp = Join-Path $env:TEMP 'teachest-winget'
New-Item -ItemType Directory -Force $tmp | Out-Null
Copy-Item packaging/winget/*.yaml $tmp -Force
winget validate --manifest $tmp
```

This only reads the files. Don't run `winget install` against these manifests
while testing; it installs the package into your real profile.

## Bump the version

1. Set `PackageVersion` to the new version in all three files.
2. Point both `InstallerUrl`s at the new release tag, `v<version>`.
3. Replace each `InstallerSha256` with the hash of that release's
   `TeaChest-win-x64.zip` or `TeaChest-win-arm64.zip`, as listed on the release
   page.
4. Run the check above again.

Keep `PackageIdentifier` and the locale alone unless the publisher or the app
itself changes.
