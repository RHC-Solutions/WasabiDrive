# Publishes the app and compiles the Inno Setup installer.
# Requires Inso Setup 6+ (ISCC.exe). Download: https://jrsoftware.org/isdl.php
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot "publish.ps1")

# Sign the app exe before it is packaged, so the installed program is trusted too.
$sign = Join-Path $PSScriptRoot "sign.ps1"
$publishedExe = Get-ChildItem -Path (Join-Path $root "src\WasabiDrive.App\bin\Release") -Directory `
    -Filter "net8.0-windows*" | Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName "win-x64\publish\WasabiDrive.exe" }
if ($publishedExe -and (Test-Path $publishedExe)) { & $sign -Path $publishedExe }

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        # Some editors/IDEs bundle Inno Setup (e.g. Antigravity IDE's innosetup npm package).
        (Join-Path $env:LOCALAPPDATA "Programs\Antigravity IDE\resources\app\node_modules\innosetup\bin\ISCC.exe")
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    # Last resort: search common install roots for any Inno Setup 6 ISCC.exe.
    $iscc = Get-ChildItem -Path @($env:LOCALAPPDATA, ${env:ProgramFiles(x86)}, $env:ProgramFiles) `
        -Recurse -Filter ISCC.exe -ErrorAction SilentlyContinue -Depth 6 |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $iscc) {
    Write-Error "ISCC.exe (Inno Setup compiler) not found. Install Inno Setup 6+ and re-run."
    exit 1
}
Write-Host "Using ISCC: $iscc"

$iss = Join-Path $root "installer\WasabiDrive.iss"
& $iscc $iss
Write-Host "Installer written to installer\output\"

# Sign the finished installer(s).
Get-ChildItem -Path (Join-Path $root "installer\output") -Filter "WasabiDrive-Setup*.exe" -ErrorAction SilentlyContinue |
    ForEach-Object { & $sign -Path $_.FullName }
