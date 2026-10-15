# Requires: Windows 10/11 x64, .NET 10 SDK, internet for initial NuGet restore.
# Run: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-LoungeTV.ps1
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'LoungeTV\LoungeTV.csproj'
$output = Join-Path $PSScriptRoot 'publish\LoungeTV-win-x64'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required to BUILD the application. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 (SDK, Windows x64).'
}

$sdkVersions = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdkVersions | Where-Object { $_ -match '^10\.' })) {
    throw 'Please install the .NET 10 SDK (not just the runtime), then reopen PowerShell.'
}

$mode = if ($FrameworkDependent) { 'false' } else { 'true' }
Write-Host 'Building LoungeTV (Release / win-x64)...' -ForegroundColor Cyan
Write-Host "Self-contained .NET runtime: $(-not $FrameworkDependent)"

& dotnet publish $project -c Release -r win-x64 --self-contained $mode `
    -p:PublishSingleFile=false -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

$exe = Join-Path $output 'LoungeTV.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Publish finished without LoungeTV.exe.' }
$libvlc = Get-ChildItem -Path $output -Filter libvlc.dll -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $libvlc) {
    Write-Warning 'libvlc.dll not found in output. Verify the VideoLAN.LibVLC.Windows NuGet package restored correctly.'
} else {
    Write-Host "Native playback library: $($libvlc.FullName)"
}

Write-Host ''
Write-Host 'Build output:' -ForegroundColor Green
Write-Host $output
Write-Host 'Run LoungeTV.exe from that folder; do not separate it from its supporting files.'
if ($Launch) { Start-Process -FilePath $exe -WorkingDirectory $output }
