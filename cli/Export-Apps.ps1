#requires -Version 5.1
<#
.SYNOPSIS
    Inventories installed applications and creates a portable migration manifest.

.DESCRIPTION
    Run this on the old PC. It detects WinGet packages, Microsoft Store/MSIX apps,
    and classic desktop apps (via the uninstall registry). It then lets you review
    which apps to keep and writes apps.json, apps.csv, and apps.md.

    The generated manifest can be copied with Install-Apps.ps1 to a new Windows PC.

.PARAMETER OutFile
    Path for the JSON manifest. apps.csv and apps.md are written beside it.

.PARAMETER LiveJson
    Write periodic live scan snapshots here. Used by the GUI to populate the
    app list while the scan is still running.

.PARAMETER Review
    Open the interactive checkbox-style review screen before saving.

.PARAMETER IncludeAll
    Select every detected app. Runtime libraries and drivers are included too.

.PARAMETER NoModule
    Do not use/install Microsoft.WinGet.Client. Uses winget list output instead,
    which can be slower and may not resolve every package ID.

.PARAMETER NoResolve
    Do not look up WinGet/Store IDs for classic and Store apps. Faster, but more
    entries will require manual installation.

.PARAMETER NoStore
    Exclude Microsoft Store apps.

.PARAMETER Exclude
    Name or ID patterns to deselect. Wildcards are supported.

.PARAMETER Yes
    Accept prompts (module install, overwrite) automatically.

.EXAMPLE
    .\Export-Apps.ps1 -Review

.EXAMPLE
    .\Export-Apps.ps1 -IncludeAll -OutFile C:\Temp\apps.json

.NOTES
    Windows PowerShell 5.1+ or PowerShell 7+. Run on Windows 10/11 with WinGet.
#>
[CmdletBinding()]
param(
    [string]$OutFile,
    [string]$LiveJson,
    [switch]$Review,
    [switch]$IncludeAll,
    [switch]$NoModule,
    [switch]$NoResolve,
    [switch]$NoStore,
    [string[]]$Exclude = @(),
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$script:WingetResolveCache = @{}
$script:StoreResolveCache = @{}

if (-not $OutFile) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
    $OutFile = Join-Path $scriptDir 'apps.json'
}
$OutFile = [IO.Path]::GetFullPath($OutFile)

# ---------------------------------------------------------------------------
# Console helpers
# ---------------------------------------------------------------------------

function Write-Info {
    param([string]$Message)
    Write-Host $Message
}

function Write-Notice {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Cyan
}

function Write-Warn {
    param([string]$Message)
    Write-Host "WARNING: $Message" -ForegroundColor Yellow
}

function Write-Ok {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Green
}

function Test-Truthy {
    param($Value)
    if ($null -eq $Value) { return $false }
    if ($Value -is [bool]) { return $Value }
    return ([string]$Value).Trim() -match '^(1|true|yes|y|include|x)$'
}

function Test-WildcardMatch {
    param([string]$Text, [string]$Pattern)
    if ([string]::IsNullOrWhiteSpace($Pattern)) { return $false }
    if ($Pattern -match '[\*\?]') { return ($Text -like $Pattern) }
    return ($Text -match [regex]::Escape($Pattern))
}

# ---------------------------------------------------------------------------
# Name/category helpers
# ---------------------------------------------------------------------------

function ConvertTo-NormalizedName {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }

    $n = $Name.ToLowerInvariant()
    $n = $n -replace '\([^)]*\)', ' '                  # (x64), (User), (lite)
    $n = $n -replace '\s+\d[\w.-]*\s*$', ' '          # trailing version, e.g. " 2.28.2"
    $n = $n -replace '\b(v?ersion\s*)?v?\d+(\.\d+){1,4}\b', ' '
    $n = $n -replace '\b(x64|x86|64-bit|32-bit|user|machine)\b', ' '
    $n = $n -replace '[^a-z0-9]+', ''
    $n = $n -replace '^microsoft', ''
    if ($n -eq 'msteams') { $n = 'teams' }
    return $n
}

function Get-DedupeKey {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }

    # This key is deliberately stricter than ConvertTo-NormalizedName: it does
    # not strip version numbers, so side-by-side versions (Python 3.11/3.12,
    # Visual Studio 2019/2022) are not collapsed into one entry. Duplicates that
    # are merely named with/without a version are merged later by package ID.
    $n = $Name.ToLowerInvariant()
    $n = $n -replace '\([^)]*\)', ' '
    $n = $n -replace '\b(x64|x86|64-bit|32-bit|user|machine)\b', ' '
    $n = $n -replace '[^a-z0-9]+', ''
    $n = $n -replace '^microsoft', ''
    if ($n -eq 'msteams') { $n = 'teams' }
    return $n
}

function Get-AppCategory {
    param([string]$Name, [string]$Type)

    if ($Name -match '(?i)Visual C\+\+|\.NET|App Runtime|WinAppRuntime|WindowsAppRuntime|VCLibs|UI\.Xaml|Native (Framework|Runtime)|WebView2|DirectX|Windows SDK|Winget Source|Windows Package Manager|App Installer') {
        return 'Runtime'
    }
    if ($Name -match '(?i)\bDriver\b|Virtual Display|Virtual USB|NVIDIA Graphics|AMD Chipset|Realtek|Intel\(R\)') {
        return 'Driver'
    }
    if ($Name -match '(?i)Chrome|Firefox|Microsoft Edge|Brave|Opera|Vivaldi|Arc Browser|ego') {
        return 'Browser'
    }
    if ($Name -match '(?i)Parsec|RustDesk|TeamViewer|AnyDesk|Remote Desktop|VNC') {
        return 'Remote access'
    }
    if ($Name -match '(?i)Antigravity|Command Code|OpenCode|Visual Studio|VS Code|GitHub|Git\b|Python|Node\.js|Docker|Postman|Terminal|PowerShell') {
        return 'Development'
    }
    if ($Name -match '(?i)ChatGPT|Claude|Gemini|Copilot|Codex|Ollama|LM Studio') {
        return 'AI tools'
    }
    if ($Name -match '(?i)Teams|Slack|Discord|Zoom|WhatsApp|Telegram|Signal|Outlook|Thunderbird|Mail') {
        return 'Communication'
    }
    if ($Name -match '(?i)Spotify|VLC|iTunes|Media Player|OBS|Audacity|Clipchamp|Photos|Image|Video|Music') {
        return 'Media'
    }
    if ($Name -match '(?i)Steam|Epic Games|GOG|Xbox|Battle\.net|Game') {
        return 'Gaming'
    }
    if ($Name -match '(?i)Office|Word|Excel|PowerPoint|Notion|Obsidian|Todo|Sticky|PDF|Acrobat|OneNote') {
        return 'Productivity'
    }
    if ($Name -match '(?i)Security|Defender|1Password|Bitwarden|Malware|VPN|Password') {
        return 'Security'
    }
    if ($Type -eq 'msix' -or $Type -eq 'msstore') { return 'Store app' }
    return 'Utilities'
}

$script:InboxWingetPattern = '^(Microsoft Edge|Microsoft OneDrive|OneDrive|Microsoft Teams|Outlook for Windows|Microsoft 365 Copilot|Windows Terminal|App Installer|Windows App Runtime.*|Microsoft Visual C\+\+.*|Microsoft \.NET.*|Microsoft\.UI\.Xaml.*|Microsoft\.VCLibs.*|Windows Package Manager.*|WinGet.*)$'

$script:InboxAppxPatterns = @(
    '^Clipchamp\.',
    '^Microsoft\.(AV1|AVC|ApplicationCompat|BingNews|BingSearch|BingWeather|GamingApp|GetHelp|HEIF|HEVC|MPEG2|MicrosoftEdge|MicrosoftOfficeHub|MicrosoftSolitaire|MicrosoftStickyNotes|OutlookForWindows|Paint|PowerAutomate|RawImage|ScreenSketch|StartExperiences|StorePurchase|Todos|VP9|WebMedia|Webp|WidgetsPlatform|Windows\.(DevHome|Photos|Calculator|Camera|FeedbackHub|Notepad|SoundRecorder|Store|Terminal|Alarms)|Winget\.Source|Xbox|YourPhone|ZuneMusic|SecHealthUI|DesktopAppInstaller|UI\.Xaml|VCLibs|WindowsAppRuntime|NET\.Native)',
    '^MicrosoftCorporationII\.QuickAssist',
    '^MicrosoftWindows\.(Client\.WebExperience|CrossDevice)',
    '^Microsoft\.Windows\.DevHome',
    '^Microsoft\.OneDriveSync',
    '^MSTeams$'
)

function Test-InboxApp {
    param($App)

    if ($App.nonRemovable -eq $true) { return $true }

    $pfn = [string]$App.packageFamilyName
    if (-not $pfn -and ([string]$App.id) -like 'MSIX\*') {
        $pfn = ([string]$App.id).Substring(5)
    }
    $pkg = ($pfn -split '_')[0]

    if ($pkg) {
        $pkgNorm = ConvertTo-NormalizedName $pkg
        if ($pkgNorm -match '^(windows(store|calculator|camera|notepad|soundrecorder|alarms|feedbackhub|terminal|photos|devhome|clientwebexperience|crossdevice)|paint|screensketch|storepurchaseapp|gethelp|bing(news|search|weather)|gamingapp|xbox|yourphone|zunemusic|todos|powerautomatedesktop|rawimageextension|heifimageextension|hevcvideoextension|av1videoextension|avcencodervideoextension|mpeg2videoextension|vp9videoextensions|webmediaextensions|webpimageextension|microsoftedge|microsoftofficehub|microsoftsolitairecollection|microsoftstickynotes|outlookforwindows|sechealthui|desktopappinstaller|wingetsource|applicationcompatibilityenhancements|windowsappruntime|uixaml|vclibs|netnative|clipchamp|teams|onedrivesync|startexperiencesapp|widgetsplatformruntime|corporationiiquickassist)') {
            return $true
        }
        foreach ($pattern in $script:InboxAppxPatterns) {
            if ($pkg -match $pattern) { return $true }
        }
    }

    if ($App.name -match $script:InboxWingetPattern) { return $true }
    if ($App.name -match '^(Clipchamp|Microsoft Clipchamp|Solitaire & Casual Games|Sticky Notes|Microsoft Sticky Notes|Paint|Snipping Tool|Xbox|Game Bar|Phone Link|Quick Assist|Microsoft To Do|Power Automate|Windows Media Player|Windows Web Experience Pack|Mobile devices|Start Experiences App|Store Experience Host|Windows Application Compatibility Enhancements|Widgets Platform Runtime|Windows Package Manager Source.*|Game Speech Window|Xbox TCUI|Windows Security)$') {
        return $true
    }
    return $false
}

# ---------------------------------------------------------------------------
# WinGet client module (structured inventory)
# ---------------------------------------------------------------------------

function Import-WinGetClient {
    if ($NoModule) { return $false }
    if (Get-Command Get-WinGetPackage -ErrorAction SilentlyContinue) { return $true }

    $module = Get-Module -ListAvailable -Name Microsoft.WinGet.Client -ErrorAction SilentlyContinue |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1

    if (-not $module) {
        if ($NoModule) { return $false }

        $install = $Yes
        if (-not $install) {
            Write-Host ''
            Write-Host "Microsoft's WinGet client module gives reliable package IDs and is recommended." -ForegroundColor Cyan
            $answer = Read-Host "Install it for the current user from PSGallery? [Y/n]"
            $install = ($answer -eq '' -or $answer -match '^(y|yes)$')
        }
        if (-not $install) { return $false }

        try {
            Write-Info 'Installing Microsoft.WinGet.Client...'
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            if (-not (Get-PackageProvider -Name NuGet -ListAvailable -ErrorAction SilentlyContinue)) {
                Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Scope CurrentUser -Force -Confirm:$false | Out-Null
            }
            Install-Module Microsoft.WinGet.Client -Scope CurrentUser -Force -Confirm:$false -Repository PSGallery -ErrorAction Stop
            $module = Get-Module -ListAvailable -Name Microsoft.WinGet.Client -ErrorAction SilentlyContinue |
                Sort-Object -Property Version -Descending |
                Select-Object -First 1
        }
        catch {
            Write-Warn "Could not install Microsoft.WinGet.Client: $($_.Exception.Message)"
            Write-Warn 'Falling back to winget list. Some package IDs may be less precise.'
            return $false
        }
    }

    if (-not $module) { return $false }

    # Load the binary module directly. This avoids occasional format-file
    # validation problems when the module lives in a synchronized folder.
    $dll = $null
    if ($PSVersionTable.PSEdition -eq 'Core') {
        $dll = Join-Path $module.ModuleBase 'net8.0-windows10.0.26100.0\Microsoft.WinGet.Client.Cmdlets.dll'
    }
    if (-not $dll -or -not (Test-Path -LiteralPath $dll)) {
        $dll = Join-Path $module.ModuleBase 'net48\Microsoft.WinGet.Client.Cmdlets.dll'
    }
    if (-not (Test-Path -LiteralPath $dll)) {
        $dll = Get-ChildItem -Path $module.ModuleBase -Filter 'Microsoft.WinGet.Client.Cmdlets.dll' -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }

    try {
        if ($dll) { Import-Module -Name $dll -ErrorAction Stop }
        else { Import-Module -Name $module.Path -ErrorAction Stop }
        return [bool](Get-Command Get-WinGetPackage -ErrorAction SilentlyContinue)
    }
    catch {
        Write-Warn "Could not load Microsoft.WinGet.Client: $($_.Exception.Message)"
        return $false
    }
}

function New-AppEntry {
    param(
        [string]$Name,
        [string]$Id = '',
        [string]$Type = 'manual',
        [string]$Source = '',
        [string]$Version = '',
        [string]$Publisher = '',
        [string]$Scope = '',
        [string]$PackageFamilyName = '',
        [string]$PackageFullName = '',
        [string]$InstallLocation = '',
        [string]$UninstallString = '',
        [string]$QuietUninstallString = '',
        [string]$Notes = '',
        $NonRemovable = $false,
        [string]$SignatureKind = ''
    )

    [pscustomobject]@{
        name                = $Name
        id                  = $Id
        type                = $Type
        source              = $Source
        installType         = $Type
        version             = $Version
        publisher           = $Publisher
        scope               = $Scope
        packageFamilyName   = $PackageFamilyName
        packageFullName     = $PackageFullName
        installLocation     = $InstallLocation
        uninstallString     = $UninstallString
        quietUninstallString = $QuietUninstallString
        nonRemovable        = [bool]$NonRemovable
        signatureKind       = $SignatureKind
        storeSearchName     = $Name
        downloadUrl         = ''
        sha256              = ''
        installerType       = ''
        installerArgs       = ''
        wingetArgs          = ''
        category            = ''
        notes               = $Notes
        defaultInclude      = $true
        include             = $true
        isStore             = ($Type -eq 'msix' -or $Type -eq 'msstore')
    }
}

function Get-WinGetInventory {
    $items = @()
    $packages = Get-WinGetPackage -ErrorAction Stop

    foreach ($pkg in $packages) {
        $id = [string]$pkg.Id
        $source = [string]$pkg.Source
        $type = 'manual'
        $pfn = ''
        $fullName = ''

        if ($id -like 'MSIX\*') {
            $type = 'msix'
            $fullName = $id.Substring(5)
            $source = 'msix'
        }
        elseif ($id -like 'ARP\*') {
            $type = 'arp'
            $source = 'registry'
        }
        elseif ($source -eq 'msstore') {
            $type = 'msstore'
        }
        elseif ($id) {
            $type = 'winget'
            if (-not $source) { $source = 'winget' }
        }

        $items += New-AppEntry -Name ([string]$pkg.Name) -Id $id -Type $type -Source $source `
            -Version ([string]$pkg.InstalledVersion) -PackageFamilyName $pfn -PackageFullName $fullName
    }

    return @($items)
}

# ---------------------------------------------------------------------------
# Fallback: parse winget list output
# ---------------------------------------------------------------------------

function Get-WingetListRows {
    $lines = @()
    try {
        $raw = & winget list --accept-source-agreements --disable-interactivity 2>&1 | Out-String
    }
    catch {
        return @()
    }

    $raw = $raw -replace '\x1b\[[0-9;]*[A-Za-z]', ''
    $allLines = $raw -split "`r?`n"
    $header = $allLines | Where-Object { $_ -match '^Name\s+Id\s+Version' } | Select-Object -First 1
    if (-not $header) { return @() }

    $iId = $header.IndexOf('Id')
    $iVer = $header.IndexOf('Version')
    $iAvail = $header.IndexOf('Available')
    $iSrc = $header.IndexOf('Source')
    if ($iAvail -lt 0) { $iAvail = $iSrc }
    if ($iSrc -lt 0) { $iSrc = $header.Length }
    if ($iId -le 0 -or $iVer -le $iId) { return @() }

    $cleanCell = {
        param([string]$Value)
        $Value = $Value -replace ([string][char]0xFFFD), ''
        $Value = $Value -replace ([string][char]0x2026), ''
        return $Value.Trim()
    }

    $headerSeen = $false
    foreach ($line in $allLines) {
        if (-not $headerSeen) {
            if ($line -eq $header) { $headerSeen = $true }
            continue
        }
        if ($line.Length -lt $iVer) { continue }
        if ($line -notmatch '^\S') { continue }
        if ($line -match '^\s*-+\s*$') { continue }

        $rawName = $line.Substring(0, $iId)
        $rawId = $line.Substring($iId, $iVer - $iId)
        $name = & $cleanCell $rawName
        $id = & $cleanCell $rawId
        $version = if ($iAvail -gt $iVer) { & $cleanCell $line.Substring($iVer, $iAvail - $iVer) } else { & $cleanCell $line.Substring($iVer) }
        $source = if ($line.Length -gt $iSrc) { & $cleanCell $line.Substring($iSrc) } else { '' }

        # IDs and versions must be plain ASCII to be usable. Winget's table
        # renderer can leave a partial ellipsis glyph in these columns.
        $idTruncated = ($rawId -match '[^\x20-\x7E]') -or ($rawId.TrimEnd().Length -ge ($iVer - $iId - 1))
        $nameTruncated = ($rawName -match '[\u2026\uFFFD]') -or ($rawName.TrimEnd().Length -ge ($iId - 1))
        $id = ($id -replace '[^\x20-\x7E]', '').Trim()
        $version = ($version -replace '[^\x20-\x7E]', '').Trim()

        if (-not $name) { continue }
        if ($name -match '^[-\\|/ ]+$') { continue }
        if ($id -notmatch '^(MSIX|ARP)\\|^[A-Za-z0-9._-]+$') { continue }

        $lines += [pscustomobject]@{
            Name          = $name
            Id            = $id
            Version       = $version
            Source        = $source
            IdTruncated   = $idTruncated
            NameTruncated = $nameTruncated
            Truncated     = ($idTruncated -or $nameTruncated)
        }
    }
    return @($lines)
}

function Get-WingetListInventory {
    $items = @()
    foreach ($row in @(Get-WingetListRows)) {
        $type = 'arp'
        $source = 'registry'
        $pfn = ''
        $id = $row.Id

        if ($row.Id -like 'MSIX\*') {
            $type = 'msix'
            $source = 'msix'
            $pfn = $row.Id.Substring(5)
        }
        elseif ($row.Id -like 'ARP\*') {
            $type = 'arp'
            $source = 'registry'
        }
        elseif ($row.Source -eq 'msstore') {
            $type = 'msstore'
            $source = 'msstore'
        }
        elseif ($row.Source -eq 'winget' -or $row.Id -match '^[A-Za-z0-9]') {
            $type = 'winget'
            $source = 'winget'
        }

        # Truncated cells contain an ellipsis or a replacement character.
        $truncated = [bool]$row.Truncated
        $notes = ''
        if ($truncated) {
            $notes = 'Name or ID was truncated by winget list.'
            if ($type -eq 'winget' -and $row.IdTruncated) { $id = '' }
        }

        $items += New-AppEntry -Name $row.Name -Id $id -Type $type -Source $source `
            -Version $row.Version -PackageFamilyName $pfn -Notes $notes
    }
    return @($items)
}

# ---------------------------------------------------------------------------
# Registry and AppX inventory
# ---------------------------------------------------------------------------

function Get-RegistryInventory {
    $paths = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $items = @()
    foreach ($path in $paths) {
        foreach ($item in @(Get-ItemProperty -Path $path -ErrorAction SilentlyContinue)) {
            if (-not $item.DisplayName) { continue }
            if ($item.SystemComponent -eq 1) { continue }
            if ($item.NoDisplay -eq 1) { continue }
            if ($item.ParentKeyName) { continue }
            if ($item.ReleaseType -in @('Update', 'Hotfix', 'Security Update')) { continue }
            if ($item.DisplayName -match '^(KB\d+|Update for |Security Update for |Hotfix for )') { continue }
            if ($item.DisplayName -match '(?i)\b(Update|Updater|Helper|Telemetry)\b') { continue }
            if (-not $item.UninstallString -and -not $item.QuietUninstallString) { continue }

            $scope = if ($item.PSPath -like '*HKEY_CURRENT_USER*') { 'user' } else { 'machine' }
            $items += New-AppEntry -Name ([string]$item.DisplayName).Trim() -Type 'arp' -Source 'registry' `
                -Version ([string]$item.DisplayVersion) -Publisher ([string]$item.Publisher) `
                -Scope $scope -InstallLocation ([string]$item.InstallLocation) `
                -UninstallString ([string]$item.UninstallString) `
                -QuietUninstallString ([string]$item.QuietUninstallString)
        }
    }
    return @($items)
}

function Get-AppxInventory {
    $items = @()
    foreach ($pkg in @(Get-AppxPackage -ErrorAction SilentlyContinue)) {
        if ($pkg.IsFramework) { continue }
        if ($pkg.Name -match '^Microsoft\.(UI\.Xaml|VCLibs|WindowsAppRuntime|NET\.Native)') { continue }

        $items += New-AppEntry -Name ([string]$pkg.Name) -Id ('MSIX\' + [string]$pkg.PackageFamilyName) `
            -Type 'msix' -Source 'msix' -Version ([string]$pkg.Version) `
            -Publisher ([string]$pkg.PublisherDisplayName) `
            -PackageFamilyName ([string]$pkg.PackageFamilyName) `
            -PackageFullName ([string]$pkg.PackageFullName) `
            -InstallLocation ([string]$pkg.InstallLocation) `
            -NonRemovable $pkg.NonRemovable -SignatureKind ([string]$pkg.SignatureKind)
    }
    return @($items)
}

function Get-AppxDisplayName {
    param($Package)

    try {
        $fullName = [string]$Package.packageFullName
        if (-not $fullName -and $Package.packageFamilyName) {
            $raw = Get-AppxPackage -PackageFamilyName ([string]$Package.packageFamilyName) -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($raw) { $fullName = [string]$raw.PackageFullName }
        }
        if (-not $fullName) { return $null }

        $manifest = Get-AppxPackageManifest -Package $fullName -ErrorAction Stop
        $displayName = [string]$manifest.Package.Properties.DisplayName
        if ($displayName -and $displayName -notmatch '^(ms-resource|@\{)') {
            return $displayName.Trim()
        }
    }
    catch { }
    return $null
}

# ---------------------------------------------------------------------------
# Package ID resolution
# ---------------------------------------------------------------------------

function Resolve-WingetIdByName {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    if ($script:WingetResolveCache.ContainsKey($Name)) { return $script:WingetResolveCache[$Name] }

    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add($Name)
    $withoutVersion = $Name -replace '\s+\d[\w.-]*\s*$', ''
    if ($withoutVersion -ne $Name -and $withoutVersion) { $candidates.Add($withoutVersion) }
    $withoutParens = $Name -replace '\s*\([^)]*\)', ''
    if ($withoutParens -ne $Name -and $withoutParens) { $candidates.Add($withoutParens) }
    $withoutParensVersion = $withoutParens -replace '\s+\d[\w.-]*\s*$', ''
    if ($withoutParensVersion -ne $withoutParens -and $withoutParensVersion) { $candidates.Add($withoutParensVersion) }

    $result = $null
    foreach ($candidate in $candidates) {
        $out = ''
        try {
            $out = & winget show --name $candidate --exact --accept-source-agreements --disable-interactivity 2>&1 | Out-String
        }
        catch { continue }

        $out = $out -replace '\x1b\[[0-9;]*[A-Za-z]', ''
        $match = [regex]::Match($out, '(?m)^\s*Found\s+.+?\s+\[([^\]]+)\]\s*$')
        if ($match.Success) {
            $id = $match.Groups[1].Value.Trim()
            if ($id -and $id -notmatch '^(MSIX|ARP)\\') {
                $result = [pscustomobject]@{ Id = $id; How = 'winget show' }
                $script:WingetResolveCache[$Name] = $result
                return $result
            }
        }

        # Fallback: an exact-name search. This helps with names that winget
        # show does not match but search does (for example parenthetical names).
        try {
            $search = & winget search --name $candidate --source winget --accept-source-agreements --disable-interactivity 2>&1 | Out-String
            $search = $search -replace '\x1b\[[0-9;]*[A-Za-z]', ''
            foreach ($line in ($search -split "`r?`n")) {
                if ($line -notmatch '^\S' -or $line -match '^Name\s+Id\s') { continue }
                $parts = $line -split '\s{2,}'
                if ($parts.Count -lt 3) { continue }
                if ($parts[1] -notmatch '^[A-Za-z0-9._-]{3,}$') { continue }
                if ($parts[0] -match '[^\x20-\x7E]' -or $parts[1] -match '[^\x20-\x7E]') { continue }
                if ((ConvertTo-NormalizedName $parts[0]) -eq (ConvertTo-NormalizedName $candidate)) {
                    $result = [pscustomobject]@{ Id = $parts[1]; How = 'winget search' }
                    $script:WingetResolveCache[$Name] = $result
                    return $result
                }
            }
        }
        catch { }
    }

    $script:WingetResolveCache[$Name] = $result
    return $result
}

function Resolve-StoreIdByName {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    if ($script:StoreResolveCache.ContainsKey($Name)) { return $script:StoreResolveCache[$Name] }

    $result = $null
    try {
        $out = & winget search --name $Name --exact --source msstore --accept-source-agreements --disable-interactivity 2>&1 | Out-String
        $out = $out -replace '\x1b\[[0-9;]*[A-Za-z]', ''
        $lines = $out -split "`r?`n"
        $header = $lines | Where-Object { $_ -match '^Name\s+Id\s+Version' } | Select-Object -First 1

        if ($header) {
            $iId = $header.IndexOf('Id')
            $iVer = $header.IndexOf('Version')
            if ($iId -gt 0 -and $iVer -gt $iId) {
                foreach ($line in $lines) {
                    if ($line.Length -lt $iVer -or $line -notmatch '^\S') { continue }
                    if ($line -match '^Name\s+Id\s') { continue }
                    $candidate = $line.Substring($iId, $iVer - $iId).Trim()
                    if ($candidate -match '^[A-Z0-9]{12}$') {
                        $script:StoreResolveCache[$Name] = $candidate
                        return $candidate
                    }
                }
            }
        }

        if (-not $result) {
            foreach ($line in $lines) {
                if ($line -notmatch '^\S' -or $line -match '^Name\s+Id\s') { continue }
                $match = [regex]::Match($line, '(?i)(?<id>[A-Z0-9]{12})(?:\s|$)')
                if ($match.Success) {
                    $result = $match.Groups['id'].Value.ToUpperInvariant()
                    $script:StoreResolveCache[$Name] = $result
                    return $result
                }
            }
        }
    }
    catch { }

    $script:StoreResolveCache[$Name] = $result
    return $result
}

# ---------------------------------------------------------------------------
# Merge and classify
# ---------------------------------------------------------------------------

function Merge-AppMetadata {
    param($Primary, $Other)

    foreach ($field in @('publisher', 'scope', 'packageFamilyName', 'installLocation', 'uninstallString', 'quietUninstallString', 'signatureKind')) {
        if (-not $Primary.$field -and $Other.$field) { $Primary.$field = $Other.$field }
    }
    if (-not $Primary.version -and $Other.version) { $Primary.version = $Other.version }
    if ($Other.nonRemovable -eq $true) { $Primary.nonRemovable = $true }
    if (-not $Primary.notes -and $Other.notes) { $Primary.notes = $Other.notes }
}

function Get-TypePriority {
    param([string]$Type)
    switch ($Type) {
        'winget'  { return 4 }
        'msstore' { return 3 }
        'msix'    { return 2 }
        'arp'     { return 1 }
        default   { return 0 }
    }
}

function Merge-Inventory {
    param([object[]]$Entries)

    $groups = @{}
    foreach ($entry in $Entries) {
        if (-not $entry.name) { continue }
        $key = Get-DedupeKey $entry.name
        if (-not $key) { continue }
        if (-not $groups.ContainsKey($key)) {
            $groups[$key] = New-Object System.Collections.ArrayList
        }
        [void]$groups[$key].Add($entry)
    }

    $merged = @()
    foreach ($key in $groups.Keys) {
        $items = @($groups[$key])
        $primary = $items |
            Sort-Object -Property @{ Expression = { Get-TypePriority $_.type }; Descending = $true },
                                  @{ Expression = { if ($_.id) { $_.id.Length } else { 0 } }; Descending = $true } |
            Select-Object -First 1

        foreach ($other in $items) {
            if (-not [object]::ReferenceEquals($primary, $other)) {
                Merge-AppMetadata -Primary $primary -Other $other
            }
        }
        $merged += $primary
    }
    return @($merged)
}

function Merge-ResolvedDuplicates {
    param([object[]]$Apps)

    # Registry entries such as "Everything 1.4.1.1032 (x64)" and module entries
    # such as "Everything" have different names but resolve to the same package
    # ID. Keep one friendly entry per package ID.
    $byId = @{}
    $result = @()
    $ordered = $Apps |
        Sort-Object -Property @{ Expression = { Get-TypePriority $_.type }; Descending = $true },
                              @{ Expression = { if ($_.name) { $_.name.Length } else { 0 } }; Descending = $false }

    foreach ($app in $ordered) {
        if ($app.id -and $app.type -in @('winget', 'msstore')) {
            $idKey = ('{0}|{1}' -f $app.type, $app.id.ToLowerInvariant())
            if ($byId.ContainsKey($idKey)) {
                $primary = $byId[$idKey]
                Merge-AppMetadata -Primary $primary -Other $app
                if ($app.name -and $primary.name -and $app.name.Length -lt $primary.name.Length) { $primary.name = $app.name }
                continue
            }
            $byId[$idKey] = $app
        }
        $result += $app
    }

    # A registry entry may still carry a version suffix that WinGet does not
    # recognize (for example "Everything 1.4.1.1032 (x64)"). If stripping the
    # version leaves exactly one actionable app, fold the metadata into it.
    $looseMap = @{}
    foreach ($app in $result) {
        if ($app.type -in @('winget', 'msstore')) {
            $looseKey = ConvertTo-NormalizedName $app.name
            if ($looseKey) {
                if (-not $looseMap.ContainsKey($looseKey)) {
                    $looseMap[$looseKey] = New-Object System.Collections.ArrayList
                }
                [void]$looseMap[$looseKey].Add($app)
            }
        }
    }

    $final = @()
    foreach ($app in $result) {
        if ($app.type -notin @('winget', 'msstore')) {
            $looseKey = ConvertTo-NormalizedName $app.name
            if ($looseKey -and $looseMap.ContainsKey($looseKey) -and $looseMap[$looseKey].Count -eq 1) {
                $primary = $looseMap[$looseKey][0]
                Merge-AppMetadata -Primary $primary -Other $app
                continue
            }
        }
        $final += $app
    }
    return @($final)
}

# ---------------------------------------------------------------------------
# Review UI
# ---------------------------------------------------------------------------

function Show-AppReview {
    param([object[]]$Apps)

    if (-not [Environment]::UserInteractive -or [Console]::IsInputRedirected) {
        Write-Warn 'No interactive console is available. Saving the default selection.'
        return $true
    }

    $filter = ''
    $page = 0
    $pageSize = 20
    $saved = $false

    while ($true) {
        if ($filter) {
            $visible = @($Apps | Where-Object {
                $_.name -match [regex]::Escape($filter) -or $_.id -match [regex]::Escape($filter) -or $_.category -match [regex]::Escape($filter)
            })
        }
        else {
            $visible = @($Apps)
        }

        $pageCount = [Math]::Max(1, [Math]::Ceiling($visible.Count / $pageSize))
        if ($page -ge $pageCount) { $page = $pageCount - 1 }
        if ($page -lt 0) { $page = 0 }
        $start = $page * $pageSize
        $end = [Math]::Min($visible.Count - 1, $start + $pageSize - 1)

        Clear-Host
        Write-Host ''
        Write-Host '  APP MIGRATION - REVIEW' -ForegroundColor Cyan
        Write-Host ("  Selected: {0} of {1} detected" -f @($Apps | Where-Object { $_.include }).Count, $Apps.Count) -ForegroundColor Green
        if ($filter) { Write-Host ("  Filter: '{0}'  ({1} matches)" -f $filter, $visible.Count) -ForegroundColor Yellow }
        Write-Host ''
        Write-Host ('  {0,3}  {1,3}  {2,-38} {3,-10} {4,-14} {5}' -f '#', 'Sel', 'Name', 'Type', 'Version', 'ID')
        Write-Host ('  ' + ('-' * 112)) -ForegroundColor DarkGray

        if ($visible.Count -eq 0) {
            Write-Host '  No apps match the filter.' -ForegroundColor Yellow
        }
        else {
            for ($i = $start; $i -le $end; $i++) {
                $app = $visible[$i]
                $mark = if ($app.include) { '[x]' } else { '[ ]' }
                $color = if ($app.include) { 'Green' } else { 'DarkGray' }
                $name = [string]$app.name
                $id = [string]$app.id
                $version = [string]$app.version
                if ($name.Length -gt 38) { $name = $name.Substring(0, 37) + '~' }
                if ($id.Length -gt 42) { $id = $id.Substring(0, 41) + '~' }
                if ($version.Length -gt 14) { $version = $version.Substring(0, 13) + '~' }
                Write-Host ('  {0,3}  {1,3}  {2,-38} {3,-10} {4,-14} {5}' -f ($i + 1), $mark, $name, $app.type, $version, $id) -ForegroundColor $color
            }
        }

        Write-Host ''
        Write-Host ("  Page {0}/{1}   Commands: number(s) toggle | n/p page | a all | x none | d defaults | f filter | r reset | s save | q quit" -f ($page + 1), $pageCount) -ForegroundColor DarkGray
        if ($filter) { Write-Host ("  Filter active: '{0}'" -f $filter) -ForegroundColor Yellow }

        $userInput = Read-Host '  >'
        if ($null -eq $userInput) { $userInput = '' }
        $userInput = $userInput.Trim()

        if ($userInput -eq '') {
            $page = ($page + 1) % $pageCount
            continue
        }

        switch -Regex ($userInput) {
            '^(n|next)$' { $page = ($page + 1) % $pageCount; continue }
            '^(p|prev)$' { $page = ($page - 1 + $pageCount) % $pageCount; continue }
            '^(a|all)$' {
                foreach ($app in $visible) { $app.include = $true }
                continue
            }
            '^(x|none)$' {
                foreach ($app in $visible) { $app.include = $false }
                continue
            }
            '^(d|defaults)$' {
                foreach ($app in $Apps) { $app.include = [bool]$app.defaultInclude }
                $filter = ''
                $page = 0
                continue
            }
            '^(f|filter)$' {
                $filter = (Read-Host '  Filter text (blank clears)').Trim()
                $page = 0
                continue
            }
            '^(r|reset)$' { $filter = ''; $page = 0; continue }
            '^(s|save)$' {
                if (-not (@($Apps | Where-Object { $_.include }).Count)) {
                    $answer = Read-Host '  Nothing is selected. Save anyway? [y/N]'
                    if ($answer -notmatch '^(y|yes)$') { continue }
                }
                $saved = $true
                break
            }
            '^(q|quit)$' {
                $answer = Read-Host '  Quit without saving? [y/N]'
                if ($answer -match '^(y|yes)$') { return $false }
                continue
            }
            '^(\?|help)$' {
                Clear-Host
                Write-Host ''
                Write-Host '  Review help' -ForegroundColor Cyan
                Write-Host '  - Enter numbers separated by commas or spaces, e.g. 2,5,9-12'
                Write-Host '  - a selects everything currently shown; x clears it'
                Write-Host '  - d restores the automatic defaults'
                Write-Host '  - f filters by name, ID, or category; r clears the filter'
                Write-Host '  - s saves; q quits without saving'
                Write-Host ''
                Read-Host '  Press Enter to continue' | Out-Null
                continue
            }
            default {
                $tokens = $userInput -split '[,\s]+' | Where-Object { $_ }
                $changed = $false
                foreach ($token in $tokens) {
                    if ($token -match '^(\d+)-(\d+)$') {
                        $from = [int]$Matches[1]
                        $to = [int]$Matches[2]
                        if ($from -gt $to) { $tmp = $from; $from = $to; $to = $tmp }
                        for ($n = $from; $n -le $to; $n++) {
                            $index = $n - 1
                            if ($index -ge $start -and $index -le $end -and $index -lt $visible.Count) {
                                $visible[$index].include = -not $visible[$index].include
                                $changed = $true
                            }
                        }
                    }
                    elseif ($token -match '^\d+$') {
                        $index = [int]$token - 1
                        if ($index -ge $start -and $index -le $end -and $index -lt $visible.Count) {
                            $visible[$index].include = -not $visible[$index].include
                            $changed = $true
                        }
                    }
                    else {
                        Write-Host '  Unrecognized command or number.' -ForegroundColor Yellow
                        Start-Sleep -Milliseconds 500
                    }
                }
                if (-not $changed) {
                    Write-Host '  Enter numbers from the current page, or a command.' -ForegroundColor Yellow
                    Start-Sleep -Milliseconds 700
                }
                continue
            }
        }

        if ($saved) { break }
    }

    return $true
}

# ---------------------------------------------------------------------------
# Saving
# ---------------------------------------------------------------------------

function Save-Manifest {
    param([object[]]$Apps, [string]$Path)

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $selected = @($Apps | Where-Object { $_.include })
    $manifest = [ordered]@{
        schemaVersion  = 1
        generatedAt    = (Get-Date).ToString('o')
        generatedBy    = 'Export-Apps.ps1'
        sourceComputer = $env:COMPUTERNAME
        windowsVersion = [Environment]::OSVersion.VersionString
        stats          = [ordered]@{
            detected = $Apps.Count
            selected = $selected.Count
        }
        apps           = @($Apps | ForEach-Object {
            [ordered]@{
                name                 = $_.name
                id                   = $_.id
                source               = $_.source
                installType          = $_.installType
                version              = $_.version
                publisher            = $_.publisher
                scope                = $_.scope
                packageFamilyName    = $_.packageFamilyName
                packageFullName      = $_.packageFullName
                category             = $_.category
                include              = [bool]$_.include
                defaultInclude       = [bool]$_.defaultInclude
                notes                = $_.notes
                uninstallString      = $_.uninstallString
                installLocation      = $_.installLocation
                storeSearchName      = $_.storeSearchName
                downloadUrl          = $_.downloadUrl
                sha256               = $_.sha256
                installerType        = $_.installerType
                installerArgs        = $_.installerArgs
                wingetArgs           = $_.wingetArgs
            }
        })
    }

    $json = $manifest | ConvertTo-Json -Depth 8
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $json, $utf8)

    $csvPath = [IO.Path]::ChangeExtension($Path, '.csv')
    $Apps | Select-Object `
        @{ Name = 'Include'; Expression = { if ($_.include) { 'TRUE' } else { 'FALSE' } } },
        Name, Id, Source, InstallType, Version, Category, Publisher, Scope,
        PackageFamilyName, StoreSearchName, Notes, UninstallString, InstallLocation,
        DownloadUrl, Sha256, InstallerType, InstallerArgs, WingetArgs |
        Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8

    $mdPath = [IO.Path]::ChangeExtension($Path, '.md')
    $md = New-Object System.Collections.Generic.List[string]
    $md.Add('# Apps to reinstall')
    $md.Add('')
    $md.Add(("Generated {0} on {1}." -f (Get-Date).ToString('yyyy-MM-dd HH:mm'), $env:COMPUTERNAME))
    $md.Add(("Selected **{0}** of {1} detected apps." -f $selected.Count, $Apps.Count))
    $md.Add('')
    $md.Add('| App | Type | Version | ID |')
    $md.Add('|---|---|---|---|')
    foreach ($app in ($selected | Sort-Object category, name)) {
        $name = ([string]$app.name) -replace '\|', '\|'
        $id = ([string]$app.id) -replace '\|', '\|'
        $typeLabel = if ($app.installType) { $app.installType } else { $app.type }
        $md.Add(("| {0} | {1} | {2} | ``{3}`` |" -f $name, $typeLabel, $app.version, $id))
    }
    [IO.File]::WriteAllLines($mdPath, $md, $utf8)

    return [pscustomobject]@{
        Json = $Path
        Csv  = $csvPath
        Md   = $mdPath
        Selected = $selected.Count
        Total = $Apps.Count
    }
}

function Write-LiveSnapshot {
    param(
        [object[]]$Apps,
        [string]$Path,
        [int]$Phase
    )

    if (-not $Path) { return }

    try {
        $records = @()
        $selected = 0
        foreach ($app in $Apps) {
            $installType = 'manual'
            switch ($app.type) {
                'winget'  { $installType = 'winget' }
                'msstore' { $installType = 'msstore' }
            }

            $category = Get-AppCategory -Name $app.name -Type $app.type
            $runtime = ($category -eq 'Runtime')
            $driver = ($category -eq 'Driver')
            $inbox = Test-InboxApp -App $app
            $isStore = ($app.type -in @('msix', 'msstore') -or [string]$app.source -in @('msix', 'msstore'))

            $include = $true
            if (-not $IncludeAll) {
                if ($runtime -or $driver -or $inbox) { $include = $false }
            }
            if ($NoStore -and $isStore) { $include = $false }
            foreach ($pattern in $Exclude) {
                if ((Test-WildcardMatch -Text $app.name -Pattern $pattern) -or
                    (Test-WildcardMatch -Text ([string]$app.id) -Pattern $pattern)) {
                    $include = $false
                }
            }
            if ($include) { $selected++ }

            $records += [ordered]@{
                name              = $app.name
                id                = $app.id
                source            = $app.source
                installType       = $installType
                version           = $app.version
                publisher         = $app.publisher
                scope             = $app.scope
                packageFamilyName = $app.packageFamilyName
                category          = $category
                include           = [bool]$include
                defaultInclude    = [bool]$include
                notes             = $app.notes
            }
        }

        $data = [ordered]@{
            schemaVersion = 1
            generatedAt   = (Get-Date).ToString('o')
            scanComplete  = $false
            phase         = $Phase
            stats         = [ordered]@{ detected = $Apps.Count; selected = $selected }
            apps          = @($records)
        }

        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $tempPath = $Path + '.tmp'
        [IO.File]::WriteAllText($tempPath, ($data | ConvertTo-Json -Depth 6), $utf8)
        Move-Item -LiteralPath $tempPath -Destination $Path -Force
    }
    catch {
        Write-Warn "Could not write live scan preview: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '  APP MIGRATION - EXPORT' -ForegroundColor Cyan
Write-Host '  Scanning installed applications...' -ForegroundColor DarkGray

$winget = Get-Command winget -ErrorAction SilentlyContinue
if (-not $winget) {
    Write-Warn 'WinGet is not available on this PC. Install/update App Installer from the Microsoft Store.'
    Write-Warn 'Continuing with registry and Store app inventory only.'
}

$primary = @()
$usingModule = $false
if ($winget) {
    $usingModule = Import-WinGetClient
    if ($usingModule) {
        try {
            $primary = @(Get-WinGetInventory)
            Write-Ok ("Found {0} packages through the WinGet client." -f $primary.Count)
        }
        catch {
            Write-Warn "WinGet client inventory failed: $($_.Exception.Message)"
            $usingModule = $false
        }
    }
    if (-not $usingModule) {
        Write-Info '  Reading winget list...'
        $primary = @(Get-WingetListInventory)
        Write-Ok ("Found {0} rows through winget list." -f $primary.Count)
    }
}
else {
    Write-Info '  Reading the uninstall registry and Store packages...'
}

$registry = @(Get-RegistryInventory)
$appx = @(Get-AppxInventory)

# The module already reports MSIX/Store apps with friendly display names.
# Add only AppX packages it did not report, and skip inbox/system apps that a
# fresh Windows install will already contain.
$primaryFullNames = @{}
$primaryNameKeys = @{}
foreach ($item in $primary) {
    if ($item.packageFullName) { $primaryFullNames[$item.packageFullName.ToLowerInvariant()] = $true }
    $key = Get-DedupeKey $item.name
    if ($key) { $primaryNameKeys[$key] = $true }
}

$appxExtra = @()
foreach ($pkg in $appx) {
    if ($pkg.packageFullName -and $primaryFullNames.ContainsKey($pkg.packageFullName.ToLowerInvariant())) { continue }
    $key = Get-DedupeKey $pkg.name
    if ($key -and $primaryNameKeys.ContainsKey($key)) { continue }
    $shortName = ([string]$pkg.name -split '\.')[-1]
    $shortKey = Get-DedupeKey $shortName
    if ($shortKey -and $primaryNameKeys.ContainsKey($shortKey)) { continue }
    if (Test-InboxApp -App $pkg) { continue }

    # Use the package's real display name when available. This catches apps
    # whose package identity differs from the name people know (for example
    # OpenAI.Codex is displayed as "ChatGPT").
    $displayName = Get-AppxDisplayName -Package $pkg
    if ($displayName) {
        $pkg.name = $displayName
        $displayKey = Get-DedupeKey $displayName
        if ($displayKey -and $primaryNameKeys.ContainsKey($displayKey)) { continue }
    }

    $appxExtra += $pkg
}

$all = @()
$all += $primary
$all += $registry
$all += $appxExtra

# Index AppX packages for enrichment.
$appxByFullName = @{}
$appxByPfn = @{}
$appxByName = @{}
foreach ($pkg in $appx) {
    if ($pkg.packageFullName) { $appxByFullName[$pkg.packageFullName.ToLowerInvariant()] = $pkg }
    if ($pkg.packageFamilyName) { $appxByPfn[$pkg.packageFamilyName.ToLowerInvariant()] = $pkg }
    $nameKey = Get-DedupeKey $pkg.name
    if ($nameKey -and -not $appxByName.ContainsKey($nameKey)) { $appxByName[$nameKey] = $pkg }
}

$merged = @(Merge-Inventory -Entries $all)

# Enrich with AppX metadata (NonRemovable, family name, publisher).
foreach ($app in $merged) {
    $pfn = [string]$app.packageFamilyName
    $fullName = [string]$app.packageFullName
    $appxInfo = $null
    if ($fullName -and $appxByFullName.ContainsKey($fullName.ToLowerInvariant())) {
        $appxInfo = $appxByFullName[$fullName.ToLowerInvariant()]
    }
    elseif ($pfn -and $appxByPfn.ContainsKey($pfn.ToLowerInvariant())) {
        $appxInfo = $appxByPfn[$pfn.ToLowerInvariant()]
    }
    elseif ($appxByName.ContainsKey((Get-DedupeKey $app.name))) {
        $appxInfo = $appxByName[(Get-DedupeKey $app.name)]
    }
    if ($appxInfo) {
        if (-not $app.packageFamilyName) { $app.packageFamilyName = $appxInfo.packageFamilyName }
        if (-not $app.packageFullName) { $app.packageFullName = $appxInfo.packageFullName }
        if (-not $app.publisher) { $app.publisher = $appxInfo.publisher }
        if ($appxInfo.nonRemovable) { $app.nonRemovable = $true }
        if (-not $app.signatureKind) { $app.signatureKind = $appxInfo.signatureKind }
    }
}

# Resolve classic apps and Store apps to installable IDs.
if ($LiveJson) { Write-LiveSnapshot -Apps $merged -Path $LiveJson -Phase 1 }

$resolveTargets = @($merged | Where-Object {
    $_.type -eq 'arp' -and -not $NoResolve -and
    -not ($_.name -match '(?i)\bDriver\b|Virtual Display|Virtual USB|NVIDIA Graphics|AMD Chipset|Realtek|Intel\(R\)')
})
if ($resolveTargets.Count -gt 0) {
    Write-Info ("  Matching {0} classic app(s) to WinGet..." -f $resolveTargets.Count)
    for ($i = 0; $i -lt $resolveTargets.Count; $i++) {
        $app = $resolveTargets[$i]
        Write-Progress -Activity 'Matching apps to WinGet' -Status $app.name -PercentComplete (($i / $resolveTargets.Count) * 100)
        $resolved = Resolve-WingetIdByName -Name $app.name
        if ($resolved) {
            $app.type = 'winget'
            $app.source = 'winget'
            $app.id = $resolved.Id
            $app.notes = ('Matched to WinGet by name ({0}).' -f $resolved.How)
        }
    }
    Write-Progress -Activity 'Matching apps to WinGet' -Completed
}

if ($LiveJson) { Write-LiveSnapshot -Apps $merged -Path $LiveJson -Phase 2 }

# Any unresolved/truncated winget entries get a name-based lookup too.
$unresolved = @($merged | Where-Object {
    $_.type -eq 'winget' -and -not $NoResolve -and
    (-not $_.id -or $_.id -match '[^\x20-\x7E]')
})
foreach ($app in $unresolved) {
    $resolved = Resolve-WingetIdByName -Name $app.name
    if ($resolved) {
        $app.id = $resolved.Id
        $app.source = 'winget'
        $app.notes = ('Resolved truncated winget ID by name ({0}).' -f $resolved.How)
    }
    else {
        $app.type = 'manual'
        $app.source = 'winget'
        $app.notes = 'WinGet reported this app but its package ID could not be resolved. Install manually.'
    }
}

# Store apps: resolve to a Microsoft Store product ID when possible.
$storeTargets = @($merged | Where-Object {
    $_.type -eq 'msix' -and -not $NoResolve -and -not (Test-InboxApp $_)
})
if ($storeTargets.Count -gt 0) {
    Write-Info ("  Matching {0} Store app(s) to Microsoft Store IDs..." -f $storeTargets.Count)
    for ($i = 0; $i -lt $storeTargets.Count; $i++) {
        $app = $storeTargets[$i]
        Write-Progress -Activity 'Matching Store apps' -Status $app.name -PercentComplete (($i / $storeTargets.Count) * 100)
        $storeId = Resolve-StoreIdByName -Name $app.name
        if ($storeId) {
            $app.type = 'msstore'
            $app.source = 'msstore'
            $app.id = $storeId
            $app.notes = 'Matched to a Microsoft Store product ID.'
        }
        else {
            $app.type = 'manual'
            $app.source = 'msix'
            $app.notes = 'Store app could not be matched automatically. Reinstall it from the Microsoft Store.'
        }
    }
    Write-Progress -Activity 'Matching Store apps' -Completed
}

if ($LiveJson) { Write-LiveSnapshot -Apps $merged -Path $LiveJson -Phase 3 }

# Merge entries that resolved to the same package ID (classic and module names
# can differ by a version suffix).
$merged = @(Merge-ResolvedDuplicates -Apps $merged)

# Classify and choose defaults.
foreach ($app in $merged) {
    switch ($app.type) {
        'winget'  { $app.installType = 'winget'; if (-not $app.source) { $app.source = 'winget' } }
        'msstore' { $app.installType = 'msstore'; $app.source = 'msstore'; $app.isStore = $true }
        'msix'    { $app.installType = 'manual'; $app.source = 'msix'; $app.isStore = $true }
        default   { $app.installType = 'manual'; $app.isStore = ([string]$app.source -eq 'msix') }
    }

    $app.category = Get-AppCategory -Name $app.name -Type $app.type
    if ($app.installType -eq 'manual' -and -not $app.notes) {
        if ([string]$app.source -eq 'msix') {
            $app.notes = 'Store/MSIX app. Reinstall it from the Microsoft Store.'
        }
        else {
            $app.notes = 'No WinGet package found. Reinstall manually or add DownloadUrl/Sha256 to apps.csv.'
        }
    }
    $runtime = ($app.category -eq 'Runtime')
    $driver = ($app.category -eq 'Driver')
    $inbox = Test-InboxApp -App $app
    $isStore = [bool]$app.isStore

    $include = $true
    if (-not $IncludeAll) {
        if ($runtime -or $driver -or $inbox) { $include = $false }
    }
    if ($NoStore -and $isStore) { $include = $false }
    foreach ($pattern in $Exclude) {
        if ((Test-WildcardMatch -Text $app.name -Pattern $pattern) -or (Test-WildcardMatch -Text ([string]$app.id) -Pattern $pattern)) {
            $include = $false
        }
    }
    $app.defaultInclude = $include
    $app.include = $include
}

$merged = @($merged | Sort-Object -Property @{ Expression = { $_.category }; Descending = $false }, @{ Expression = { $_.name }; Descending = $false })

Write-Host ''
Write-Host ("  Detected {0} apps: {1} selected by default." -f $merged.Count, @($merged | Where-Object { $_.include }).Count) -ForegroundColor Green
$byType = $merged | Group-Object type | Sort-Object Name
foreach ($group in $byType) {
    Write-Host ("    {0,-10} {1,3} detected, {2,3} selected" -f $group.Name, $group.Count, @($group.Group | Where-Object { $_.include }).Count)
}

if ($Review) {
    $proceed = Show-AppReview -Apps $merged
    if (-not $proceed) {
        Write-Warn 'Review cancelled. Nothing was saved.'
        exit 0
    }
}

if (Test-Path -LiteralPath $OutFile) {
    $overwrite = $Yes
    if (-not $overwrite) {
        $answer = Read-Host ("  {0} already exists. Overwrite? [Y/n]" -f $OutFile)
        $overwrite = ($answer -eq '' -or $answer -match '^(y|yes)$')
    }
    if (-not $overwrite) {
        Write-Warn 'Not overwritten. Use -OutFile to choose another path.'
        exit 0
    }
}

$result = Save-Manifest -Apps $merged -Path $OutFile
Write-Host ''
Write-Ok ("Saved {0} selected apps (of {1} detected) to:" -f $result.Selected, $result.Total)
Write-Host ("    {0}" -f $result.Json) -ForegroundColor White
Write-Host ("    {0}  (edit Include and optional download fields in a spreadsheet)" -f $result.Csv) -ForegroundColor White
Write-Host ("    {0}  (human-readable checklist)" -f $result.Md) -ForegroundColor White
Write-Host ''
Write-Host '  Next steps:' -ForegroundColor Cyan
Write-Host '    1. Copy this folder (or at least apps.json and Install-Apps.ps1) to the new PC.'
Write-Host '    2. On the new PC:  .\Install-Apps.ps1 -Plan'
Write-Host '    3. Then run:       .\Install-Apps.ps1'
Write-Host ''
