param()
$ErrorActionPreference = 'Stop'
if (Get-Process -Name NINA -ErrorAction SilentlyContinue) {
    throw 'Close all N.I.N.A. instances, then run this installer again.'
}
$source = Join-Path $PSScriptRoot 'plugin'
$target = Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0\Live Polar Alignment'
$files = @('Nina.LivePolarAlignment.dll', 'Nina.LivePolarAlignment.Core.dll', 'ko-KR\Nina.LivePolarAlignment.resources.dll')
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $source $file))) { throw "Missing package file: $file" }
}
foreach ($file in $files) {
    $destination = Join-Path $target $file
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $destination -Force
}
Write-Host "Installed to $target"
Write-Host 'Start N.I.N.A. 3.2.0.9001 or newer. Plugins > Installed > Live Polar Alignment > Options: Enable debug recording.'
