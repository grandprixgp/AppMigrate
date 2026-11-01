#requires -Version 5.1
<#
.SYNOPSIS
    Installs the applications listed in an Export-Apps.ps1 manifest.

.DESCRIPTION
    Run this on the new PC. By default it shows the plan, asks for confirmation,
    checks what is already installed, and installs the rest with WinGet.

    Manual entries (apps with no package ID) and failed Store installs are listed
    at the end with links/notes so nothing is silently lost.

.PARAMETER Manifest
    apps.json (or apps.csv) created by Export-Apps.ps1.

.PARAMETER Plan
    Show what would happen without installing anything.

.PARAMETER PlanJson
    Also write the plan as JSON to this path. Useful for GUI front ends.

.PARAMETER Yes
    Do not ask for confirmation.

.PARAMETER Force
    Install even if the app appears to be installed. Passes --force to WinGet.

.PARAMETER MatchScope
    Pass the recorded user/machine scope to WinGet. Off by default because some
    packages only support one scope.

.PARAMETER PinVersions
    Try to install the exact version recorded in the manifest. Many old versions
    are no longer available from WinGet.

.PARAMETER AllowInteractive
    Do not pass --silent/--disable-interactivity to WinGet. Useful for installers
    that fail when run silently.

.PARAMETER SkipStore
    Do not attempt Microsoft Store installs; report them as manual actions.

.PARAMETER Only
    Only process apps whose name or ID matches one of these wildcard patterns.

.PARAMETER Skip
    Skip apps whose name or ID matches one of these wildcard patterns.

.PARAMETER Retries
    Number of retries for transient failures (default 1).

.PARAMETER TimeoutMinutes
    Per-app install timeout (default 30).

.PARAMETER TrustDownloads
    Allow download entries without a SHA256 hash. Strongly discouraged.

.PARAMETER NoLog
    Do not write log/report files.

.EXAMPLE
    .\Install-Apps.ps1 -Plan

.EXAMPLE
    .\Install-Apps.ps1

.EXAMPLE
    .\Install-Apps.ps1 -Only '*Chrome*','*Code*'

.NOTES
    Windows PowerShell 5.1+ or PowerShell 7+. Most installs work without admin,
    but machine-wide packages may prompt for elevation.
#>
[CmdletBinding()]
param(
    [string]$Manifest,
    [switch]$Plan,
    [string]$PlanJson,
    [switch]$Yes,
    [switch]$Force,
    [switch]$MatchScope,
    [switch]$PinVersions,
    [switch]$AllowInteractive,
    [switch]$SkipStore,
    [string[]]$Only = @(),
    [string[]]$Skip = @(),
    [int]$Retries = 1,
    [int]$TimeoutMinutes = 30,
    [switch]$TrustDownloads,
    [switch]$NoLog
)

$ErrorActionPreference = 'Stop'
$script:LogLines = New-Object System.Collections.Generic.List[string]
$script:StartTime = Get-Date
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

if (-not $Manifest) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
    $Manifest = Join-Path $scriptDir 'apps.json'
    if (-not (Test-Path -LiteralPath $Manifest)) {
        $csv = [IO.Path]::ChangeExtension($Manifest, '.csv')
        if (Test-Path -LiteralPath $csv) { $Manifest = $csv }
    }
}
$Manifest = [IO.Path]::GetFullPath($Manifest)
$manifestDir = Split-Path -Parent $Manifest
if (-not $manifestDir) { $manifestDir = (Get-Location).Path }

# ---------------------------------------------------------------------------
# Helpers
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

function Add-Log {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date).ToString('HH:mm:ss'), $Message
    [void]$script:LogLines.Add($line)
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

function ConvertTo-NormalizedName {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }
    $n = $Name.ToLowerInvariant()
    $n = $n -replace '\([^)]*\)', ' '
    $n = $n -replace '\s+\d[\w.-]*\s*$', ' '
    $n = $n -replace '\b(v?ersion\s*)?v?\d+(\.\d+){1,4}\b', ' '
    $n = $n -replace '\b(x64|x86|64-bit|32-bit|user|machine)\b', ' '
    $n = $n -replace '[^a-z0-9]+', ''
    $n = $n -replace '^microsoft', ''
    if ($n -eq 'msteams') { $n = 'teams' }
    return $n
}

function ConvertTo-QuotedArgument {
    param([string]$Value)
    if ($null -eq $Value) { return '""' }
    if ($Value -match '[\s"]') {
        return '"' + ($Value -replace '"', '\"') + '"'
    }
    return $Value
}

function Get-ShortText {
    param([string]$Text, [int]$Length = 300)
    if (-not $Text) { return '' }
    $clean = ($Text -replace '\x1b\[[0-9;]*[A-Za-z]', '') -replace '\s+', ' '
    $clean = $clean.Trim()
    if ($clean.Length -gt $Length) { return $clean.Substring(0, $Length - 3) + '...' }
    return $clean
}

function Get-OutputTail {
    param([string]$Text, [int]$Lines = 12)
    if (-not $Text) { return @() }
    $clean = $Text -replace '\x1b\[[0-9;]*[A-Za-z]', ''
    $all = @($clean -split "`r?`n" | ForEach-Object { $_.TrimEnd() } | Where-Object { $_ -match '\S' })
    return @($all | Select-Object -Last $Lines)
}

function Get-ExitCodeHex {
    param([int]$Code)
    $value = [int64]$Code
    if ($value -lt 0) { $value += 4294967296 }
    return ('0x{0:X8}' -f $value)
}

# ---------------------------------------------------------------------------
# Manifest
# ---------------------------------------------------------------------------

function Read-AppManifest {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Manifest not found: $Path"
    }

    $extension = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    switch ($extension) {
        '.json' {
            $data = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($null -eq $data.apps) { throw 'JSON manifest does not contain an "apps" array.' }
            $items = @($data.apps)
        }
        '.csv' {
            $items = @(Import-Csv -LiteralPath $Path -Encoding UTF8)
        }
        default {
            throw "Unsupported manifest type '$extension'. Use the .json or .csv file from Export-Apps.ps1."
        }
    }

    $apps = @()
    foreach ($item in $items) {
        $name = ([string]$item.name).Trim()
        if (-not $name) { $name = ([string]$item.id).Trim() }
        if (-not $name) { continue }

        $installType = ([string]$item.installType).Trim().ToLowerInvariant()
        $source = ([string]$item.source).Trim().ToLowerInvariant()
        $id = ([string]$item.id).Trim()
        $downloadUrl = ([string]$item.downloadUrl).Trim()
        $sha256 = ([string]$item.sha256).Trim() -replace '(?i)^sha256\s*[:=]\s*', ''
        $sha256 = ($sha256 -replace '[^A-Fa-f0-9]', '').ToUpperInvariant()

        if (-not $installType) {
            if ($source -eq 'msstore' -or $id -match '^9[A-Z0-9]{11}$') { $installType = 'msstore' }
            elseif ($source -eq 'winget') { $installType = 'winget' }
            elseif ($downloadUrl) { $installType = 'download' }
            else { $installType = 'manual' }
        }
        if ($installType -eq 'manual' -and $downloadUrl) { $installType = 'download' }
        if (-not $source) {
            if ($installType -eq 'winget') { $source = 'winget' }
            elseif ($installType -eq 'msstore') { $source = 'msstore' }
        }

        $include = $true
        if ($null -ne $item.include) { $include = Test-Truthy $item.include }
        if (-not $include) { continue }

        $app = [pscustomobject]@{
            name           = $name
            id             = $id
            source         = $source
            installType    = $installType
            version        = [string]$item.version
            publisher      = [string]$item.publisher
            scope          = ([string]$item.scope).ToLowerInvariant()
            packageFamilyName = [string]$item.packageFamilyName
            category       = [string]$item.category
            notes          = [string]$item.notes
            storeSearchName = if ($item.storeSearchName) { [string]$item.storeSearchName } else { $name }
            downloadUrl    = $downloadUrl
            sha256         = $sha256
            installerType  = ([string]$item.installerType).Trim().ToLowerInvariant()
            installerArgs  = ([string]$item.installerArgs).Trim()
            wingetArgs     = ([string]$item.wingetArgs).Trim()
        }
        $apps += $app
    }

    return @($apps)
}

function Test-AppFilter {
    param($App, [string[]]$Patterns)
    foreach ($pattern in $Patterns) {
        if ((Test-WildcardMatch -Text $App.name -Pattern $pattern) -or
            (Test-WildcardMatch -Text ([string]$App.id) -Pattern $pattern)) {
            return $true
        }
    }
    return $false
}

# ---------------------------------------------------------------------------
# Installed-app detection
# ---------------------------------------------------------------------------

function Get-InstalledAppIndex {
    $index = [pscustomobject]@{
        Names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        Ids   = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        Pfns  = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    }

    # Prefer the structured WinGet client if it is already available. Never
    # install it here; the installer must work on a clean PC without PSGallery.
    $loadedModule = $false
    try {
        if (Get-Command Get-WinGetPackage -ErrorAction SilentlyContinue) {
            $loadedModule = $true
        }
        else {
            $module = Get-Module -ListAvailable -Name Microsoft.WinGet.Client -ErrorAction SilentlyContinue |
                Sort-Object -Property Version -Descending | Select-Object -First 1
            if ($module) {
                $dll = Join-Path $module.ModuleBase 'net48\Microsoft.WinGet.Client.Cmdlets.dll'
                if ($PSVersionTable.PSEdition -eq 'Core') {
                    $dll = Join-Path $module.ModuleBase 'net8.0-windows10.0.26100.0\Microsoft.WinGet.Client.Cmdlets.dll'
                }
                if (Test-Path -LiteralPath $dll) {
                    Import-Module -Name $dll -ErrorAction Stop
                    $loadedModule = [bool](Get-Command Get-WinGetPackage -ErrorAction SilentlyContinue)
                }
            }
        }
        if ($loadedModule) {
            foreach ($pkg in @(Get-WinGetPackage -ErrorAction Stop)) {
                $pkgName = [string]$pkg.Name
                $pkgId = [string]$pkg.Id
                if ($pkgName -and $pkgName -notmatch '[^\x20-\x7E]') { [void]$index.Names.Add((ConvertTo-NormalizedName $pkgName)) }
                if ($pkgId) { [void]$index.Ids.Add($pkgId) }
                if ($pkgId -like 'MSIX\*') { [void]$index.Pfns.Add($pkgId.Substring(5)) }
            }
        }
    }
    catch {
        $loadedModule = $false
    }

    if (-not $loadedModule) {
        try {
            $raw = & winget list --accept-source-agreements --disable-interactivity 2>&1 | Out-String
            $raw = $raw -replace '\x1b\[[0-9;]*[A-Za-z]', ''
            $allLines = $raw -split "`r?`n"
            $header = $allLines | Where-Object { $_ -match '^Name\s+Id\s+Version' } | Select-Object -First 1
            if ($header) {
                $iId = $header.IndexOf('Id')
                $iVer = $header.IndexOf('Version')
                if ($iId -gt 0 -and $iVer -gt $iId) {
                    $seen = $false
                    foreach ($line in $allLines) {
                        if (-not $seen) { if ($line -eq $header) { $seen = $true }; continue }
                        if ($line.Length -lt $iVer -or $line -notmatch '^\S') { continue }
                        $rowName = $line.Substring(0, $iId).Trim()
                        $rowId = $line.Substring($iId, $iVer - $iId).Trim()
                        if ($rowName -and $rowName -notmatch '[^\x20-\x7E]') { [void]$index.Names.Add((ConvertTo-NormalizedName $rowName)) }
                        if ($rowId -and $rowId -notmatch '[^\x20-\x7E]') {
                            [void]$index.Ids.Add($rowId)
                            if ($rowId -like 'MSIX\*') { [void]$index.Pfns.Add($rowId.Substring(5)) }
                        }
                    }
                }
            }
        }
        catch { }
    }

    # Registry and AppX add coverage for apps WinGet does not recognize.
    $registryPaths = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($path in $registryPaths) {
        foreach ($item in @(Get-ItemProperty -Path $path -ErrorAction SilentlyContinue)) {
            if ($item.DisplayName) { [void]$index.Names.Add((ConvertTo-NormalizedName ([string]$item.DisplayName))) }
        }
    }
    foreach ($pkg in @(Get-AppxPackage -ErrorAction SilentlyContinue)) {
        if ($pkg.IsFramework) { continue }
        if ($pkg.Name) { [void]$index.Names.Add((ConvertTo-NormalizedName ([string]$pkg.Name))) }
        if ($pkg.PackageFamilyName) { [void]$index.Pfns.Add([string]$pkg.PackageFamilyName) }
    }

    return $index
}

function Test-AppInstalled {
    param($App, $Index)

    if ($App.id -and $Index.Ids.Contains($App.id)) { return $true }
    if ($App.packageFamilyName -and $Index.Pfns.Contains($App.packageFamilyName)) { return $true }
    $key = ConvertTo-NormalizedName $App.name
    if ($key -and $Index.Names.Contains($key)) { return $true }
    return $false
}

# ---------------------------------------------------------------------------
# Process execution
# ---------------------------------------------------------------------------

function Invoke-External {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [int]$TimeoutSeconds
    )

    $argumentString = (@($Arguments | ForEach-Object { ConvertTo-QuotedArgument ([string]$_) }) -join ' ')
    Add-Log ("RUN: {0} {1}" -f $FilePath, $argumentString)

    $process = $null
    try {
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $FilePath
        $startInfo.Arguments = $argumentString
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.WorkingDirectory = [IO.Path]::GetTempPath()

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $startInfo
        [void]$process.Start()

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        $timedOut = $false
        if ($TimeoutSeconds -gt 0) {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $timedOut = $true
                try { & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null } catch { }
                try { $process.Kill() } catch { }
                try { $process.WaitForExit(10000) | Out-Null } catch { }
            }
        }
        else {
            $process.WaitForExit()
        }

        $stdout = ''
        $stderr = ''
        try { $stdout = $stdoutTask.GetAwaiter().GetResult() } catch { }
        try { $stderr = $stderrTask.GetAwaiter().GetResult() } catch { }
        $combined = (@($stdout, $stderr) | Where-Object { $_ }) -join "`r`n"

        $exitCode = -1
        try { $exitCode = $process.ExitCode } catch { }
        return [pscustomobject]@{
            ExitCode = $exitCode
            Output   = $combined
            TimedOut = $timedOut
        }
    }
    catch {
        return [pscustomobject]@{
            ExitCode = -1
            Output   = $_.Exception.Message
            TimedOut = $false
        }
    }
    finally {
        if ($process) {
            try { $process.Dispose() } catch { }
        }
    }
}

function Get-WingetArguments {
    param($App)

    $arguments = @(
        'install',
        '--id', $App.id,
        '--exact',
        '--source', $App.source,
        '--accept-package-agreements',
        '--accept-source-agreements'
    )

    if ($App.installType -eq 'winget') {
        if (-not $AllowInteractive) {
            $arguments += '--silent'
            $arguments += '--disable-interactivity'
        }
        if ($MatchScope -and $App.scope -in @('user', 'machine')) {
            $arguments += @('--scope', $App.scope)
        }
        if ($PinVersions -and $App.version) {
            $arguments += @('--version', $App.version)
        }
    }
    else {
        # Microsoft Store installs do not accept --silent.
        if (-not $AllowInteractive) { $arguments += '--disable-interactivity' }
    }

    if ($Force) { $arguments += '--force' }
    if ($App.wingetArgs) {
        $arguments += @($App.wingetArgs -split '\s+' | Where-Object { $_ })
    }
    return $arguments
}

function Get-WingetResult {
    param([int]$ExitCode, [string]$Output, [bool]$TimedOut)

    $hex = Get-ExitCodeHex -Code $ExitCode
    if ($TimedOut) {
        return [pscustomobject]@{ Status = 'Failed'; Detail = 'Timed out'; Code = $hex }
    }

    if ($Output -match '(?i)No newer package versions are available|No applicable update|already installed.*no.*(update|installer)') {
        return [pscustomobject]@{ Status = 'AlreadyInstalled'; Detail = 'Already installed / no newer version'; Code = $hex }
    }
    if ($ExitCode -eq 0) {
        return [pscustomobject]@{ Status = 'Installed'; Detail = 'Installed successfully'; Code = $hex }
    }
    if ($Output -match '(?i)already installed') {
        return [pscustomobject]@{ Status = 'AlreadyInstalled'; Detail = 'Already installed'; Code = $hex }
    }
    if ($Output -match '(?i)No package found|No applicable installer|No available installer|No matching package') {
        return [pscustomobject]@{ Status = 'Failed'; Detail = 'No matching WinGet installer'; Code = $hex }
    }
    if ($Output -match '(?i)hash mismatch|hash does not match|installer hash') {
        return [pscustomobject]@{ Status = 'Failed'; Detail = 'Installer hash mismatch'; Code = $hex }
    }
    if ($Output -match '(?i)requires administrator|elevation|access is denied') {
        return [pscustomobject]@{ Status = 'Failed'; Detail = 'Needs elevation - rerun from an elevated PowerShell'; Code = $hex }
    }

    $tail = (Get-OutputTail -Text $Output -Lines 1)
    $detail = if ($tail.Count) { Get-ShortText $tail[0] } else { 'WinGet returned an error' }
    return [pscustomobject]@{ Status = 'Failed'; Detail = $detail; Code = $hex }
}

function Install-DownloadApp {
    param($App)

    if (-not $App.downloadUrl) {
        return [pscustomobject]@{ Status = 'Manual'; Detail = 'Download URL is empty'; Code = '' }
    }
    if ($App.downloadUrl -notmatch '^https://') {
        return [pscustomobject]@{ Status = 'Manual'; Detail = 'Download URL is not HTTPS'; Code = '' }
    }
    if (-not $App.sha256 -and -not $TrustDownloads) {
        return [pscustomobject]@{ Status = 'Manual'; Detail = 'Add Sha256 to the manifest or pass -TrustDownloads'; Code = '' }
    }

    $extension = [IO.Path]::GetExtension(([uri]$App.downloadUrl).AbsolutePath).ToLowerInvariant()
    $installerType = $App.installerType
    if (-not $installerType) {
        if ($extension -eq '.msi') { $installerType = 'msi' }
        elseif ($extension -in @('.msix', '.appx', '.msixbundle', '.appxbundle')) { $installerType = 'msix' }
        else { $installerType = 'exe' }
    }

    $tempFile = Join-Path ([IO.Path]::GetTempPath()) ('app-migrate-' + [guid]::NewGuid().ToString('N') + $extension)
    Add-Log ("DOWNLOAD: {0}" -f $App.downloadUrl)
    try {
        $oldProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try { Invoke-WebRequest -Uri $App.downloadUrl -OutFile $tempFile -UseBasicParsing -TimeoutSec ($TimeoutMinutes * 60) -ErrorAction Stop }
        finally { $ProgressPreference = $oldProgress }

        if ($App.sha256) {
            $actual = (Get-FileHash -LiteralPath $tempFile -Algorithm SHA256).Hash.ToUpperInvariant()
            if ($actual -ne $App.sha256) {
                return [pscustomobject]@{ Status = 'Failed'; Detail = 'SHA256 mismatch'; Code = '' }
            }
        }

        if ($installerType -eq 'msix') {
            try {
                Add-AppxPackage -Path $tempFile -ErrorAction Stop
                return [pscustomobject]@{ Status = 'Installed'; Detail = 'MSIX package installed'; Code = '' }
            }
            catch {
                return [pscustomobject]@{ Status = 'Failed'; Detail = (Get-ShortText $_.Exception.Message); Code = '' }
            }
        }
        if ($installerType -notin @('msi', 'exe')) {
            return [pscustomobject]@{ Status = 'Manual'; Detail = "Unsupported installer type '$installerType'"; Code = '' }
        }

        if ($installerType -eq 'msi') {
            $arguments = @('/i', $tempFile, '/qn', '/norestart')
            if ($App.installerArgs) { $arguments += @($App.installerArgs -split '\s+' | Where-Object { $_ }) }
        }
        else {
            $arguments = @()
            if ($App.installerArgs) { $arguments += @($App.installerArgs -split '\s+' | Where-Object { $_ }) }
            $result = Invoke-External -FilePath $tempFile -Arguments $arguments -TimeoutSeconds ($TimeoutMinutes * 60)
            if ($result.ExitCode -in @(0, 3010)) {
                return [pscustomobject]@{ Status = 'Installed'; Detail = 'Installer completed'; Code = (Get-ExitCodeHex -Code $result.ExitCode) }
            }
            return [pscustomobject]@{ Status = 'Failed'; Detail = (Get-ShortText (($result.Output -split "`r?`n" | Where-Object { $_ -match '\S' } | Select-Object -Last 1) -join ' ')); Code = (Get-ExitCodeHex -Code $result.ExitCode) }
        }

        $result = Invoke-External -FilePath 'msiexec.exe' -Arguments $arguments -TimeoutSeconds ($TimeoutMinutes * 60)
        if ($result.ExitCode -in @(0, 3010)) {
            return [pscustomobject]@{ Status = 'Installed'; Detail = 'MSI installed'; Code = (Get-ExitCodeHex -Code $result.ExitCode) }
        }
        return [pscustomobject]@{ Status = 'Failed'; Detail = (Get-ShortText (($result.Output -split "`r?`n" | Where-Object { $_ -match '\S' } | Select-Object -Last 1) -join ' ')); Code = (Get-ExitCodeHex -Code $result.ExitCode) }
    }
    catch {
        return [pscustomobject]@{ Status = 'Failed'; Detail = (Get-ShortText $_.Exception.Message); Code = '' }
    }
    finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '  APP MIGRATION - INSTALL' -ForegroundColor Cyan

try {
    $allApps = @(Read-AppManifest -Path $Manifest)
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

$apps = @($allApps)
if ($Only.Count -gt 0) {
    $apps = @($apps | Where-Object { Test-AppFilter -App $_ -Patterns $Only })
}
if ($Skip.Count -gt 0) {
    $apps = @($apps | Where-Object { -not (Test-AppFilter -App $_ -Patterns $Skip) })
}
if ($SkipStore) {
    $apps = @($apps | Where-Object { $_.installType -ne 'msstore' -and $_.source -notin @('msix', 'msstore') })
}

if ($apps.Count -eq 0) {
    Write-Warn 'No included apps matched the manifest and filters.'
    exit 0
}

$admin = $false
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $admin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
catch { }

if (-not $admin) {
    Write-Warn 'Not running elevated. User-scope installs are fine; some machine-wide installs may prompt for UAC or fail.'
}

$winget = Get-Command winget -ErrorAction SilentlyContinue

Write-Info ("  Manifest: {0}" -f $Manifest)
Write-Info ("  Loaded {0} included app(s)." -f $apps.Count)
Write-Info '  Checking what is already installed...'

$installedIndex = Get-InstalledAppIndex
$planItems = @()

foreach ($app in $apps) {
    $state = 'Install'
    $detail = ''

    if ($app.installType -eq 'manual') {
        $state = 'Manual'
        $detail = if ($app.notes) { $app.notes } else { 'No automatic installer' }
    }
    elseif ($app.installType -eq 'download') {
        if (-not $app.downloadUrl) { $state = 'Manual'; $detail = 'Download URL is empty' }
        elseif ($app.downloadUrl -notmatch '^https://') { $state = 'Manual'; $detail = 'Download URL is not HTTPS' }
        elseif (-not $app.sha256 -and -not $TrustDownloads) { $state = 'Manual'; $detail = 'Add Sha256 or pass -TrustDownloads' }
        elseif (-not $Force -and (Test-AppInstalled -App $app -Index $installedIndex)) { $state = 'Skip'; $detail = 'Already installed' }
    }
    elseif ($app.installType -in @('winget', 'msstore')) {
        if (-not $app.id) {
            $state = 'Manual'
            $detail = 'Missing package ID'
        }
        elseif ($SkipStore -and $app.installType -eq 'msstore') {
            $state = 'Manual'
            $detail = 'Store installs skipped'
        }
        elseif (-not $Force -and (Test-AppInstalled -App $app -Index $installedIndex)) {
            $state = 'Skip'
            $detail = 'Already installed'
        }
        if ($app.installType -eq 'winget' -and $app.id -match '^(MSIX|ARP)\\') {
            $state = 'Manual'
            $detail = 'Manifest ID is not a WinGet package ID'
        }
    }
    else {
        $state = 'Manual'
        $detail = 'Unknown install type'
    }

    $planItems += [pscustomobject]@{
        App    = $app
        State  = $state
        Detail = $detail
    }
}

$installCount = @($planItems | Where-Object State -eq 'Install').Count
$skipCount = @($planItems | Where-Object State -eq 'Skip').Count
$manualCount = @($planItems | Where-Object State -eq 'Manual').Count

Write-Host ''
Write-Host '  PLAN' -ForegroundColor Cyan
Write-Host ('  {0,3}  {1,-7} {2,-9} {3,-38} {4}' -f '#', 'Action', 'Type', 'Name', 'ID')
Write-Host ('  ' + ('-' * 112)) -ForegroundColor DarkGray
for ($i = 0; $i -lt $planItems.Count; $i++) {
    $item = $planItems[$i]
    $name = [string]$item.App.name
    $id = [string]$item.App.id
    if ($name.Length -gt 38) { $name = $name.Substring(0, 37) + '~' }
    if ($id.Length -gt 42) { $id = $id.Substring(0, 41) + '~' }
    $color = switch ($item.State) {
        'Install' { 'White' }
        'Skip'    { 'DarkGray' }
        'Manual'  { 'Yellow' }
        default   { 'Gray' }
    }
    Write-Host ('  {0,3}  {1,-7} {2,-9} {3,-38} {4}' -f ($i + 1), $item.State, $item.App.installType, $name, $id) -ForegroundColor $color
    if ($item.Detail -and $item.State -ne 'Install') {
        Write-Host ('       {0}' -f (Get-ShortText $item.Detail 120)) -ForegroundColor DarkGray
    }
}
Write-Host ''
Write-Host ("  {0} to install, {1} already installed, {2} manual action(s)." -f $installCount, $skipCount, $manualCount) -ForegroundColor Green

if ($PlanJson) {
    $planOutput = [ordered]@{
        generatedAt  = (Get-Date).ToString('o')
        manifest     = $Manifest
        installCount = $installCount
        items        = @($planItems | ForEach-Object {
            [ordered]@{
                name   = $_.App.name
                id     = $_.App.id
                type   = $_.App.installType
                state  = $_.State
                detail = $_.Detail
            }
        })
    }
    try {
        $planOutput | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $PlanJson -Encoding UTF8
    }
    catch {
        Write-Warn "Could not write the plan JSON: $($_.Exception.Message)"
    }
}

if ($Plan) {
    Write-Host ''
    Write-Info '  Dry run only. Nothing was installed.'
    exit 0
}

if (-not $winget -and $installCount -gt 0) {
    Write-Host ''
    Write-Host 'ERROR: WinGet is not available.' -ForegroundColor Red
    Write-Host '  Install or update "App Installer" from the Microsoft Store, then retry:' -ForegroundColor Yellow
    Write-Host '  ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1' -ForegroundColor Yellow
    Write-Host '  https://aka.ms/getwinget' -ForegroundColor Yellow
    exit 2
}

if ($installCount -eq 0) {
    Write-Host ''
    Write-Ok 'Nothing to install automatically.'
    Write-Host '  Writing a report for the manual and already-present items...' -ForegroundColor DarkGray
}

if ($installCount -gt 0 -and -not $Yes) {
    if ([Console]::IsInputRedirected -or -not [Environment]::UserInteractive) {
        Write-Warn 'No interactive console is available. Re-run with -Yes to confirm the installs.'
        exit 0
    }
    $answer = Read-Host ("  Proceed with {0} install(s)? [Y/n]" -f $installCount)
    if ($answer -and $answer -notmatch '^(y|yes)$') {
        Write-Warn 'Cancelled. Nothing was installed.'
        exit 0
    }
}

$logPath = Join-Path $manifestDir ('install-log-{0}.txt' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$reportPath = Join-Path $manifestDir ('install-report-{0}.json' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$manualPath = Join-Path $manifestDir ('manual-actions-{0}.txt' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$results = @()
$number = 0

Add-Log ("Manifest: {0}" -f $Manifest)
Add-Log ("Computer: {0}; admin: {1}; winget: {2}" -f $env:COMPUTERNAME, $admin, $(if ($winget) { $winget.Source } else { 'missing' }))

foreach ($item in $planItems) {
    $app = $item.App
    $number++
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()

    if ($item.State -eq 'Skip') {
        $results += [pscustomobject]@{
            name = $app.name; id = $app.id; type = $app.installType; status = 'AlreadyInstalled';
            detail = $item.Detail; seconds = 0; code = ''; output = ''
        }
        Write-Host ("  [{0}/{1}] {2} ... already installed" -f $number, $planItems.Count, $app.name) -ForegroundColor DarkGray
        Add-Log ("SKIP (installed): {0}" -f $app.name)
        continue
    }

    if ($item.State -eq 'Manual') {
        $results += [pscustomobject]@{
            name = $app.name; id = $app.id; type = $app.installType; status = 'Manual';
            detail = $item.Detail; seconds = 0; code = ''; output = ''
        }
        Write-Host ("  [{0}/{1}] {2} ... manual: {3}" -f $number, $planItems.Count, $app.name, (Get-ShortText $item.Detail 100)) -ForegroundColor Yellow
        Add-Log ("MANUAL: {0} - {1}" -f $app.name, $item.Detail)
        continue
    }

    Write-Host ("  [{0}/{1}] {2} ({3})" -f $number, $planItems.Count, $app.name, $app.installType) -ForegroundColor White
    Add-Log ("START: {0} [{1}]" -f $app.name, $app.installType)

    $result = $null
    $external = $null
    $attempt = 0
    $maxAttempts = [Math]::Max(1, $Retries + 1)

    if ($app.installType -eq 'download') {
        $result = Install-DownloadApp -App $app
    }
    else {
        while ($attempt -lt $maxAttempts) {
            $attempt++
            $arguments = Get-WingetArguments -App $app
            $external = Invoke-External -FilePath $winget.Source -Arguments $arguments -TimeoutSeconds ($TimeoutMinutes * 60)
            $result = Get-WingetResult -ExitCode $external.ExitCode -Output $external.Output -TimedOut $external.TimedOut

            Add-Log ("Attempt {0}: exit {1} ({2}); {3}" -f $attempt, $external.ExitCode, $result.Code, $result.Status)
            foreach ($line in (Get-OutputTail -Text $external.Output -Lines 10)) {
                Add-Log ("  | {0}" -f $line)
            }

            if ($result.Status -notin @('Failed')) { break }
            if ($result.Detail -match 'No matching|hash mismatch|Timed out|Needs elevation') { break }
            if ($attempt -lt $maxAttempts) {
                Write-Host ("         retrying in 3 seconds ({0}/{1})..." -f $attempt, ($maxAttempts - 1)) -ForegroundColor DarkYellow
                Start-Sleep -Seconds 3
            }
        }
    }

    $stopwatch.Stop()
    $seconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)

    if ($result.Status -eq 'Installed') {
        Write-Host ("         OK ({0}s)" -f $seconds) -ForegroundColor Green
        Add-Log ("DONE: {0} in {1}s" -f $app.name, $seconds)
    }
    elseif ($result.Status -eq 'AlreadyInstalled') {
        Write-Host ("         already installed ({0})" -f $result.Code) -ForegroundColor DarkGray
        Add-Log ("ALREADY INSTALLED: {0}" -f $app.name)
    }
    elseif ($result.Status -eq 'Manual') {
        Write-Host ("         manual: {0}" -f (Get-ShortText $result.Detail 160)) -ForegroundColor Yellow
        Add-Log ("MANUAL: {0} - {1}" -f $app.name, $result.Detail)
    }
    else {
        Write-Host ("         FAILED ({0}): {1}" -f $result.Code, (Get-ShortText $result.Detail 160)) -ForegroundColor Red
        Add-Log ("FAILED: {0} - {1}" -f $app.name, $result.Detail)
        if ($app.installType -eq 'winget' -and -not $AllowInteractive) {
            Write-Host '         If this is an interactive installer, retry with -AllowInteractive.' -ForegroundColor DarkYellow
        }
        if ($app.installType -eq 'msstore' -and $app.id) {
            Write-Host ("         Store page: ms-windows-store://pdp/?ProductId={0}" -f $app.id) -ForegroundColor DarkYellow
        }
    }

    $results += [pscustomobject]@{
        name = $app.name; id = $app.id; type = $app.installType; status = $result.Status;
        detail = $result.Detail; seconds = $seconds; code = $result.Code;
        output = (Get-OutputTail -Text $external.Output -Lines 20) -join "`n"
    }
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

$installed = @($results | Where-Object status -eq 'Installed')
$already = @($results | Where-Object status -eq 'AlreadyInstalled')
$failed = @($results | Where-Object status -eq 'Failed')
$manual = @($results | Where-Object status -eq 'Manual')

Write-Host ''
Write-Host '  SUMMARY' -ForegroundColor Cyan
Write-Host ("    Installed:       {0}" -f $installed.Count) -ForegroundColor Green
Write-Host ("    Already present: {0}" -f $already.Count) -ForegroundColor DarkGray
Write-Host ("    Manual actions:  {0}" -f $manual.Count) -ForegroundColor Yellow
Write-Host ("    Failed:          {0}" -f $failed.Count) -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'DarkGray' })

if ($manual.Count -gt 0 -or $failed.Count -gt 0) {
    Write-Host ''
    Write-Host '  Needs attention:' -ForegroundColor Yellow
    foreach ($entry in @($manual + $failed)) {
        Write-Host ("    - {0}: {1}" -f $entry.name, (Get-ShortText $entry.detail 160))
        if ($entry.type -eq 'msstore' -and $entry.id) {
            Write-Host ("      ms-windows-store://pdp/?ProductId={0}" -f $entry.id) -ForegroundColor DarkGray
        }
        elseif ($entry.id -match '^MSIX\\') {
            Write-Host ("      Search the Microsoft Store for '{0}'." -f $entry.name) -ForegroundColor DarkGray
        }
    }
}

if (-not $NoLog) {
    Add-Log ''
    Add-Log 'SUMMARY'
    Add-Log ("Installed: {0}; already present: {1}; manual: {2}; failed: {3}" -f $installed.Count, $already.Count, $manual.Count, $failed.Count)

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    try { [IO.File]::WriteAllLines($logPath, $script:LogLines, $utf8) }
    catch { Write-Warn "Could not write the install log: $($_.Exception.Message)" }

    $report = [ordered]@{
        schemaVersion = 1
        generatedAt   = (Get-Date).ToString('o')
        computer      = $env:COMPUTERNAME
        manifest      = $Manifest
        durationSeconds = [Math]::Round(((Get-Date) - $script:StartTime).TotalSeconds, 1)
        summary       = [ordered]@{
            installed = $installed.Count
            alreadyInstalled = $already.Count
            manual = $manual.Count
            failed = $failed.Count
        }
        results       = @($results)
    }
    try { $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8 }
    catch { Write-Warn "Could not write the install report: $($_.Exception.Message)" }

    $manualLines = New-Object System.Collections.Generic.List[string]
    $manualLines.Add('Manual actions from App Migration')
    $manualLines.Add(("Manifest: {0}" -f $Manifest))
    $manualLines.Add(("Generated: {0}" -f (Get-Date).ToString('yyyy-MM-dd HH:mm')))
    $manualLines.Add('')
    foreach ($entry in @($manual + $failed)) {
        $manualLines.Add(("- {0} [{1}]" -f $entry.name, $entry.type))
        $manualLines.Add(("  {0}" -f $entry.detail))
        if ($entry.type -eq 'msstore' -and $entry.id) {
            $manualLines.Add(("  ms-windows-store://pdp/?ProductId={0}" -f $entry.id))
        }
        elseif ($entry.id -match '^MSIX\\') {
            $manualLines.Add(("  Search Microsoft Store for: {0}" -f $entry.name))
        }
        $manualLines.Add('')
    }
    if (@($manual + $failed).Count -gt 0) {
        try { [IO.File]::WriteAllLines($manualPath, $manualLines, $utf8) }
        catch { Write-Warn "Could not write the manual-actions file: $($_.Exception.Message)" }
    }

    Write-Host ''
    Write-Info ("  Log:    {0}" -f $logPath)
    Write-Info ("  Report: {0}" -f $reportPath)
    if (@($manual + $failed).Count -gt 0) { Write-Info ("  Manual: {0}" -f $manualPath) }
}

if ($failed.Count -gt 0) { exit 1 }
exit 0
