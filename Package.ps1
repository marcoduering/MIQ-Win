<#
.SYNOPSIS
    Builds QuickLook.Plugin.MIQ and packages it as a distributable .qlplugin.

.DESCRIPTION
    A .qlplugin is a ZIP of the plugin's runtime files. QuickLook installs it to
    %LOCALAPPDATA%\QuickLook\QuickLook.Plugin\<file-name-without-extension>, so
    the artifact is named QuickLook.Plugin.MIQ.qlplugin. QuickLook.Common.dll is
    deliberately excluded — the host provides it at runtime.

.PARAMETER Configuration
    Build configuration (default: Release).

.PARAMETER Dotnet
    Path to dotnet.exe. Defaults to the user-local SDK if present (this machine
    keeps the SDK in %LOCALAPPDATA%\dotnet; "C:\Program Files\dotnet" is
    runtime-only), otherwise falls back to whatever is on PATH (e.g. CI).

.PARAMETER Version
    Version stamped into QuickLook.Plugin.Metadata.config (QuickLook requires a
    non-empty <Version> or install fails with "version not defined"). When
    omitted, the version already in the committed config is kept. CI passes the
    release tag here (without the leading "v").
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Dotnet,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $Dotnet) {
    $local = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
    $Dotnet = if (Test-Path $local) { $local } else { 'dotnet' }
}

$proj    = Join-Path $root 'QuickLook.Plugin.MIQ\QuickLook.Plugin.MIQ.csproj'
$binDir  = Join-Path $root "QuickLook.Plugin.MIQ\bin\$Configuration"
$distDir = Join-Path $root 'dist'

Write-Host "Building $Configuration with $Dotnet ..." -ForegroundColor Cyan
& $Dotnet build $proj -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

# The metadata config QuickLook reads at install time must be in the package.
# Copy the pristine source over the build output so packing is deterministic
# (incremental builds may not re-copy, and a prior -Version run could have left
# a stamped value behind).
$srcMeta = Join-Path $root 'QuickLook.Plugin.MIQ\QuickLook.Plugin.Metadata.config'
$metaPath = Join-Path $binDir 'QuickLook.Plugin.Metadata.config'
Copy-Item $srcMeta $metaPath -Force
if ($Version) {
    $v = $Version.TrimStart('v')
    [xml]$meta = Get-Content $metaPath
    $meta.Metadata.Version = $v
    $meta.Save($metaPath)
    Write-Host "Stamped version $v into the metadata config." -ForegroundColor Cyan
}

# Validate a version is present (QuickLook rejects an empty <Version> at install),
# but keep the package FILENAME version-less. QuickLook installs to a folder named
# after the file (…\QuickLook.Plugin\<filename-without-extension>), so the name
# MUST be stable across releases — otherwise each version installs into a new
# folder instead of upgrading the existing one in place, breaking distribution.
[xml]$finalMeta = Get-Content $metaPath
if (-not $finalMeta.Metadata.Version) { throw "No <Version> found in $metaPath." }
$zipPath = Join-Path $distDir 'QuickLook.Plugin.MIQ.zip'
$pkgPath = Join-Path $distDir 'QuickLook.Plugin.MIQ.qlplugin'

# Runtime files only: drop debug symbols and the host-provided QuickLook.Common.
$files = Get-ChildItem $binDir -File |
    Where-Object { $_.Extension -ne '.pdb' -and $_.Name -ne 'QuickLook.Common.dll' }

if (-not $files) { throw "No build output found in $binDir." }

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Remove-Item $zipPath, $pkgPath -ErrorAction SilentlyContinue
Compress-Archive -Path $files.FullName -DestinationPath $zipPath -CompressionLevel Optimal
Move-Item $zipPath $pkgPath

Write-Host "Packaged:" -ForegroundColor Green
$files | ForEach-Object { "  $($_.Name)" }
Write-Host "-> $pkgPath" -ForegroundColor Green
