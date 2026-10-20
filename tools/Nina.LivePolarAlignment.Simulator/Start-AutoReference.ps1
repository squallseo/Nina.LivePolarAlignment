param(
    [Parameter(Mandatory = $true)][string]$Dataset,
    [Parameter(Mandatory = $true)][string]$LogPath,
    [switch]$Replay
)
# One synthetic reference-pair run. Never connects to or moves equipment.
$ErrorActionPreference = 'Stop'
$datasetPath = (Resolve-Path -LiteralPath $Dataset).Path
$manifest = Get-Content -LiteralPath (Join-Path $datasetPath 'manifest.json') -Raw | ConvertFrom-Json
if (!$manifest.synthetic -or $manifest.frames.Count -lt 2) { throw 'A synthetic reference-pair dataset is required.' }
$feedPath = Join-Path $datasetPath 'feed'
New-Item -ItemType Directory -Path $feedPath -Force | Out-Null
function Publish-Scene([int]$Index) {
    $source = Join-Path $datasetPath ('fits\' + $manifest.frames[$Index].name + '.fits')
    $pending = Join-Path $feedPath 'auto.pending'
    Copy-Item -LiteralPath $source -Destination $pending -Force
    $current = Join-Path $feedPath 'current.fits'
    if (Test-Path -LiteralPath $current) {
        $backup = Join-Path $feedPath 'auto.previous'
        [IO.File]::Replace($pending, $current, $backup)
        Remove-Item -LiteralPath $backup -Force
    } else { [IO.File]::Move($pending, $current) }
    Write-Output ('Published synthetic scene: ' + $manifest.frames[$Index].name)
}
$allowed = $false
# Require an explicit known simulator connection in this NINA session.
foreach ($line in Get-Content -LiteralPath $LogPath) {
    if ($line -match 'Successfully connected mount\. Id: (\S+)') {
        $allowed = $Matches[1] -in @('ScopeSim.Telescope', 'ASCOM.Simulator.Telescope', 'ASCOM.OmniSim.Telescope')
    }
}
$file = [IO.File]::Open((Resolve-Path -LiteralPath $LogPath).Path, 'Open', 'Read', 'ReadWrite')
$reader = New-Object IO.StreamReader($file)
try {
    if (!$Replay) { $file.Seek(0, [IO.SeekOrigin]::End) | Out-Null }
    Publish-Scene 0
    Write-Output 'Ready for one Auto RA run. Keep the NINA camera simulator on this dataset feed directory.'
    $downloaded = $false
    while ($true) {
        $line = $reader.ReadLine()
        if ($null -eq $line) {
            if ($Replay) { throw 'Replay ended before a downloaded reference and simulator rotation were observed.' }
            Start-Sleep -Milliseconds 50
            continue
        }
        if ($line -match 'Successfully connected mount\. Id: (\S+)') {
            $allowed = $Matches[1] -in @('ScopeSim.Telescope', 'ASCOM.Simulator.Telescope', 'ASCOM.OmniSim.Telescope')
        }
        if ($line -match 'Polar preview: download complete') { $downloaded = $true }
        if ($allowed -and $downloaded -and $line -match 'Moving Primary Telescope Axis using rate (-?[0-9.]+)\.') {
            $rate = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
            if ($rate -ne 0) {
                Publish-Scene 1
                Write-Output 'Second reference ready before rotation stops. Live captures retain this scene; use the manual controller for bolt-correction scenes. Restart this helper before a new calibration.'
                break
            }
        }
    }
} finally { $reader.Dispose() }
