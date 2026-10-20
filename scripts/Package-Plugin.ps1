param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$assembly = Join-Path $root 'src\Nina.LivePolarAlignment\bin\Release\net8.0-windows\Nina.LivePolarAlignment.dll'
if (!(Test-Path $assembly)) { throw 'Build the Release plugin first.' }
$version = (Get-Item $assembly).VersionInfo.FileVersion
$stage = Join-Path $root "artifacts\package-$version"
if (Test-Path $stage) { throw "Staging already exists: $stage. Inspect existing artifacts or use a fresh version." }
$plugin = Join-Path $stage 'plugin'
New-Item -ItemType Directory -Force (Join-Path $plugin 'ko-KR') | Out-Null
$build = Split-Path $assembly
foreach ($file in @('Nina.LivePolarAlignment.dll','Nina.LivePolarAlignment.Core.dll','ko-KR\Nina.LivePolarAlignment.resources.dll')) {
    Copy-Item -LiteralPath (Join-Path $build $file) -Destination (Join-Path $plugin $file)
}
Copy-Item (Join-Path $PSScriptRoot 'Install-Plugin.ps1') $stage
Copy-Item (Join-Path $root 'docs\field-validation.md') $stage
Copy-Item (Join-Path $root 'docs\auto-ra-rotation.md') $stage
Copy-Item (Join-Path $root 'docs\verification.md') $stage
Copy-Item (Join-Path $root 'docs\independent-drift.md') $stage
Copy-Item (Join-Path $root 'docs\install-package.txt') (Join-Path $stage 'INSTALL.txt')
Copy-Item (Join-Path $root 'docs\images\plugin-icon.png') $stage
$output = Join-Path $root 'artifacts\packages'
New-Item -ItemType Directory -Force $output | Out-Null
$zip = Join-Path $output "Nina.LivePolarAlignment.$version.zip"
if (Test-Path $zip) { throw "Archive already exists: $zip" }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256" -Encoding ASCII
Write-Host "Created $zip ($hash)"
