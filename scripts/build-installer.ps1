# Publishes the app and compiles the Inno Setup installer.
# Requires Inso Setup 6+ (ISCC.exe). Download: https://jrsoftware.org/isdl.php
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot "publish.ps1")

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    Write-Error "ISCC.exe (Inno Setup compiler) not found. Install Inno Setup 6+ and re-run."
    exit 1
}

$iss = Join-Path $root "installer\WasabiDrive.iss"
& $iscc $iss
Write-Host "Installer written to installer\output\"
