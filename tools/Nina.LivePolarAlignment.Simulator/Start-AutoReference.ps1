param(
    [Parameter(Mandatory = $true)][string]$Dataset,
    [Parameter(Mandatory = $true)][string]$LogPath,
    [switch]$Replay,
    [int]$NinaProcessId = 0
)
# Persistent synthetic reference replay. Never connects to or moves equipment.
$ErrorActionPreference = 'Stop'
$datasetPath = (Resolve-Path -LiteralPath $Dataset).Path
$manifest = Get-Content -LiteralPath (Join-Path $datasetPath 'manifest.json') -Raw | ConvertFrom-Json
if (!$manifest.synthetic -or $manifest.frames.Count -lt 2) { throw 'A synthetic reference-pair dataset is required.' }
$feedPath = Join-Path $datasetPath 'feed'
New-Item -ItemType Directory -Path $feedPath -Force | Out-Null
$sceneDataset = $datasetPath
function Select-Angle([int]$Angle) {
    $script:sceneDataset = if ($Angle -eq 90) { $datasetPath } else { Join-Path $datasetPath "angles\$Angle" }
    $script:manifest = Get-Content -LiteralPath (Join-Path $sceneDataset 'manifest.json') -Raw | ConvertFrom-Json
    if (!$manifest.synthetic -or $manifest.frames.Count -lt 2) { throw 'Invalid synthetic angular dataset.' }
    if ($manifest.rotationAngleDegrees -and $manifest.rotationAngleDegrees -ne $Angle) { throw 'Dataset angle does not match request.' }
}
function Publish-Scene([int]$Index) {
    $source = Join-Path $sceneDataset ('fits\' + $manifest.frames[$Index].name + '.fits')
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
    Write-Output 'Ready for repeated Auto RA runs at 60/70/80/90 degrees, including cancellation and retry. Keep the camera on the root feed directory with exposure >= 1 second.'
    $downloaded = $false
    $pendingCalibration = $false
    $switches = 0
    while ($true) {
        $line = $reader.ReadLine()
        if ($null -eq $line) {
            if ($NinaProcessId -gt 0 -and !(Get-Process -Id $NinaProcessId -ErrorAction SilentlyContinue)) { break }
            if ($Replay) { if ($switches -eq 0) { throw 'Replay contained no complete synthetic reference transition.' }; break }
            Start-Sleep -Milliseconds 50
            continue
        }
        if ($line -match 'Successfully connected mount\. Id: (\S+)') {
            $allowed = $Matches[1] -in @('ScopeSim.Telescope', 'ASCOM.Simulator.Telescope', 'ASCOM.OmniSim.Telescope')
        }
        if ($line -match 'Polar workflow: first reference requested; angle=(60|70|80|90)') {
            Select-Angle ([int]$Matches[1])
            Publish-Scene 0
            $pendingCalibration = $true
            $downloaded = $false
        }
        if ($pendingCalibration -and $line -match 'Polar preview: download complete') { $downloaded = $true }
        if ($allowed -and $pendingCalibration -and $downloaded -and $line -match 'Moving Primary Telescope Axis using rate (-?[0-9.]+)\.') {
            $rate = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
            if ($rate -ne 0) {
                Publish-Scene 1
                $pendingCalibration = $false
                $downloaded = $false
                $switches++
                Write-Output 'Second reference ready; continuing to monitor for a new calibration or retry.'
            }
        }
    }
} finally { $reader.Dispose() }
