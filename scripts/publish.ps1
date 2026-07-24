# Publishes WasabiDrive as a self-contained win-x64 app (no .NET runtime prerequisite).
# Output: src\WasabiDrive.App\bin\Release\net8.0-windows\win-x64\publish
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root "src\WasabiDrive.App\WasabiDrive.App.csproj"

dotnet publish $app `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=false

# The TFM folder carries the Windows platform version (e.g. net8.0-windows10.0.19041.0),
# so resolve it by glob rather than hard-coding.
$publishDir = Get-ChildItem -Path (Join-Path $root "src\WasabiDrive.App\bin\Release") -Directory `
    -Filter "net8.0-windows*" | Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName "win-x64\publish" }
Write-Host "Published to: $publishDir"
if (-not (Test-Path (Join-Path $publishDir "rclone.exe"))) {
    Write-Warning "rclone.exe is missing from the publish output — check third_party\rclone\rclone.exe exists."
}
