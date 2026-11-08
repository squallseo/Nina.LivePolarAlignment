param()
$ErrorActionPreference = 'Stop'
if (Get-Process -Name NINA -ErrorAction SilentlyContinue) {
    throw 'Close all N.I.N.A. instances, then run this installer again.'
}
$source = Join-Path $PSScriptRoot 'plugin'
$target = Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0\Live Polar Alignment'
$files = @('Nina.LivePolarAlignment.dll', 'Nina.LivePolarAlignment.Core.dll', 'ko-KR\Nina.LivePolarAlignment.resources.dll', 'External\PolarisPattern\LICENSE.txt', 'External\PolarisPattern\NOTICE.txt')
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $source $file))) { throw "Missing package file: $file" }
}
foreach ($file in $files) {
    $destination = Join-Path $target $file
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $destination -Force
}
# Remove only this plugin's obsolete bundled solver files, never NINA's solver.
foreach ($legacy in @('astap_cli.exe','LICENSE.txt','README.txt')) {
    $legacyFile = Join-Path $target ("External\ASTAP\" + $legacy)
    if (Test-Path -LiteralPath $legacyFile -PathType Leaf) { Remove-Item -LiteralPath $legacyFile }
}
Write-Host "Installed to $target"
Write-Host 'Start N.I.N.A. 3.2.0.9001 or newer. Settings: Plugins > Installed > Live Polar Alignment > Options. Field recording is optional.'
