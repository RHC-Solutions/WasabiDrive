<#
.SYNOPSIS
    Authenticode-signs a file (app exe or installer) if a signing certificate is configured.

.DESCRIPTION
    Configure ONE of these via environment variables, then builds/releases sign automatically:

      # A) Certificate already in the Windows cert store (self-signed or purchased on a token):
      $env:WASABIDRIVE_SIGN_THUMBPRINT = "ABCD...1234"

      # B) A PFX file on disk:
      $env:WASABIDRIVE_SIGN_PFX = "C:\path\WasabiDrive.pfx"
      $env:WASABIDRIVE_SIGN_PFX_PASSWORD = "..."   # optional

    If none is set, signing is skipped with a warning (the build still succeeds, but the
    output will trigger SmartScreen). A timestamp is always applied so signatures stay valid
    after the certificate expires.

.EXAMPLE
    scripts\sign.ps1 -Path .\installer\output\WasabiDrive-Setup.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

if (-not (Test-Path $Path)) { throw "File to sign not found: $Path" }

$thumbprint = $env:WASABIDRIVE_SIGN_THUMBPRINT
$pfx        = $env:WASABIDRIVE_SIGN_PFX
if (-not $thumbprint -and -not $pfx) {
    Write-Warning "No signing certificate configured (WASABIDRIVE_SIGN_THUMBPRINT or WASABIDRIVE_SIGN_PFX). Skipping signing of '$Path'."
    return
}

# Locate the newest signtool.exe from the installed Windows SDKs.
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) {
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
}
if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK." }

$common = @("sign", "/fd", "sha256", "/tr", $TimestampUrl, "/td", "sha256")
if ($thumbprint) {
    $args = $common + @("/sha1", $thumbprint, $Path)
}
else {
    if (-not (Test-Path $pfx)) { throw "PFX not found: $pfx" }
    $args = $common + @("/f", $pfx)
    if ($env:WASABIDRIVE_SIGN_PFX_PASSWORD) { $args += @("/p", $env:WASABIDRIVE_SIGN_PFX_PASSWORD) }
    $args += $Path
}

Write-Host "Signing $Path ..."
& $signtool @args
if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE) for $Path" }
Write-Host "Signed and timestamped: $Path"
