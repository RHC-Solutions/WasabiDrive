<#
.SYNOPSIS
    Cuts a WasabiDrive release: bumps the version everywhere, builds the installer, and publishes a
    GitHub Release with the setup .exe attached (so the in-app updater can find it).

.EXAMPLE
    scripts\release.ps1 -Version 0.2.0
    scripts\release.ps1 -Version 0.2.0 -Notes "Adds cache location and updater." -Prerelease
    scripts\release.ps1 -Version 0.2.0 -SkipPush      # build + tag locally, don't create the GitHub release

.NOTES
    Requires the .NET SDK, Inno Setup 6+ (for build-installer.ps1) and the GitHub CLI (gh),
    authenticated with push access to RHC-Solutions/WasabiDrive.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Notes,
    [switch]$Prerelease,
    [switch]$SkipPush
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = 'RHC-Solutions/WasabiDrive'
$tag  = "v$Version"

function Update-File($path, $pattern, $replacement) {
    $full = Join-Path $root $path
    $text = Get-Content -Raw -LiteralPath $full
    $updated = [regex]::Replace($text, $pattern, $replacement)
    if ($updated -eq $text) { Write-Warning "No version match updated in $path" }
    Set-Content -LiteralPath $full -Value $updated -NoNewline
    Write-Host "  bumped $path"
}

Write-Host "Bumping version to $Version ..."
# App project <Version>x.y.z</Version>
Update-File 'src\WasabiDrive.App\WasabiDrive.App.csproj' '(?<=<Version>)\d+\.\d+\.\d+(?=</Version>)' $Version
# Installer #define AppVersion "x.y.z"
Update-File 'installer\WasabiDrive.iss' '(?<=#define AppVersion ")\d+\.\d+\.\d+(?=")' $Version
# App manifest assemblyIdentity version="x.y.z.0"
Update-File 'src\WasabiDrive.App\app.manifest' '(?<=assemblyIdentity version=")\d+\.\d+\.\d+\.\d+(?=")' "$Version.0"

Write-Host "Building installer ..."
& (Join-Path $PSScriptRoot 'build-installer.ps1')

$setup = Join-Path $root "installer\output\WasabiDrive-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Installer not found at $setup" }
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"

# A fixed-name copy so the README's "download latest" link
# (releases/latest/download/WasabiDrive-Setup.exe) always resolves.
$setupStable = Join-Path $root "installer\output\WasabiDrive-Setup.exe"
Copy-Item $setup $setupStable -Force

# Commit the version bump on the current branch.
Write-Host "Committing version bump ..."
git -C $root add 'src/WasabiDrive.App/WasabiDrive.App.csproj' 'installer/WasabiDrive.iss' 'src/WasabiDrive.App/app.manifest'
git -C $root commit -m "Release $tag" | Out-Null

if ($SkipPush) {
    git -C $root tag $tag
    Write-Host "Tagged $tag locally. Skipping push / GitHub release (-SkipPush)." -ForegroundColor Yellow
    return
}

Write-Host "Pushing commit and creating GitHub release $tag ..."
git -C $root push

if (-not $Notes) { $Notes = "WasabiDrive $Version" }
$ghArgs = @('release', 'create', $tag, $setup, $setupStable,
            '--repo', $repo, '--title', "WasabiDrive $Version", '--notes', $Notes, '--target', 'main')
if ($Prerelease) { $ghArgs += '--prerelease' }
gh @ghArgs

Write-Host "Done. Release $tag published at https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
