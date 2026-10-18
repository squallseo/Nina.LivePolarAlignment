$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root '.tools'
$version = '24.22.0'
$archive = "node-v$version-win-x64.zip"
New-Item -ItemType Directory -Force $tools | Out-Null
$nodeRoot = Join-Path $tools "node-v$version-win-x64"
if (!(Test-Path (Join-Path $nodeRoot 'node_modules/npm/bin/npm-cli.js'))) {
    Invoke-WebRequest "https://nodejs.org/dist/v$version/SHASUMS256.txt" -OutFile (Join-Path $tools 'SHASUMS256.txt')
    Invoke-WebRequest "https://nodejs.org/dist/v$version/$archive" -OutFile (Join-Path $tools $archive)
    $entry = Get-Content (Join-Path $tools 'SHASUMS256.txt') | Where-Object { $_.EndsWith("  $archive") }
    if (!$entry) { throw 'Node checksum entry missing' }
    $expected = ($entry -split '\s+')[0]
    if ((Get-FileHash (Join-Path $tools $archive) -Algorithm SHA256).Hash -ne $expected) { throw 'Node checksum mismatch' }
    Expand-Archive (Join-Path $tools $archive) -DestinationPath $tools -Force
}
& (Join-Path $nodeRoot 'node.exe') (Join-Path $nodeRoot 'node_modules/npm/bin/npm-cli.js') install --prefix (Join-Path $tools 'rea') --ignore-scripts --no-audit --no-fund rea-agents@6.5.0
if ($LASTEXITCODE -ne 0) { throw 'REA installation failed' }
& (Join-Path $nodeRoot 'node.exe') (Join-Path $tools 'rea/node_modules/rea-agents/scripts/rea.mjs') --version
