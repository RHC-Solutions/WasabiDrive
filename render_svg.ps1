$inkscape = 'C:\Program Files\Inkscape\bin\inkscape.exe'
$svg = 'D:\Cloud\roman@heimman.com\OneDrive - RH\Documents\Apps\WasabiDrive\wasabi_logo_icon_170229.svg'
$sizes = @(256, 128, 64, 48, 32, 16)
foreach ($size in $sizes) {
    $out = Join-Path $PSScriptRoot "logo_$size.png"
    & $inkscape --export-type=png --export-filename=$out --export-width=$size --export-height=$size --export-background-opacity=0 $svg 2>&1 | Out-Null
    if (Test-Path $out) {
        Write-Host "Rendered ${size}x${size}: $((Get-Item $out).Length) bytes"
    } else {
        Write-Host "FAILED: $size"
    }
}
