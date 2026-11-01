#requires -Version 5.1
<#
.SYNOPSIS
    Builds and publishes AppMigrate.

.DESCRIPTION
    Produces a self-contained single-file Windows build by default, so a fresh
    Windows PC does not need the .NET runtime installed. Use -FrameworkDependent
    for a small build that requires the .NET 8 Desktop Runtime.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -FrameworkDependent -Runtime win-x64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$project = Join-Path $root 'src\AppMigrate\AppMigrate.csproj'

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { $null }
if (-not $dotnet) {
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) { $dotnet = $local }
}
if (-not $dotnet) { throw 'The .NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0.' }

$outputName = if ($FrameworkDependent) { "dist\$Runtime-framework" } else { "dist\$Runtime" }
$output = Join-Path $root $outputName
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $output,
    '--nologo'
)

if ($FrameworkDependent) {
    $arguments += '--self-contained'
    $arguments += 'false'
    $arguments += '-p:PublishSingleFile=true'
    $arguments += '-p:IncludeNativeLibrariesForSelfExtract=true'
}
else {
    $arguments += '--self-contained'
    $arguments += 'true'
    $arguments += '-p:PublishSingleFile=true'
    $arguments += '-p:IncludeNativeLibrariesForSelfExtract=true'
    $arguments += '-p:EnableCompressionInSingleFile=false'
}

Write-Host "Publishing AppMigrate ($Runtime, $(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }))..." -ForegroundColor Cyan
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $output 'AppMigrate.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish completed, but AppMigrate.exe was not found in $output." }

if (-not $NoZip) {
    $zip = Join-Path $root "dist\AppMigrate-$Runtime.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    $attempt = 0
    while ($true) {
        try {
            Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -CompressionLevel Optimal -ErrorAction Stop
            break
        }
        catch {
            $attempt++
            if ($attempt -ge 5) { throw }
            try { if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue } } catch { }
            Start-Sleep -Seconds 1
        }
    }
    Write-Host "Archive: $zip" -ForegroundColor Green
}

$size = [Math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
Write-Host ''
Write-Host "Published: $exe ($size MB)" -ForegroundColor Green
if ($FrameworkDependent) {
    Write-Host 'Requires the .NET 8 Desktop Runtime on the target PC.' -ForegroundColor Yellow
}
