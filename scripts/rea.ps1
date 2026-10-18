$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$node = Join-Path $root '.tools/node-v24.22.0-win-x64/node.exe'
$cli = Join-Path $root '.tools/rea/node_modules/rea-agents/scripts/rea.mjs'
if (!(Test-Path $node) -or !(Test-Path $cli)) {
    throw 'Run scripts/bootstrap.ps1 first.'
}
& $node $cli @args
exit $LASTEXITCODE
