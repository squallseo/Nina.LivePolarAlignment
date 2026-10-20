param(
    [Parameter(Mandatory = $true)]
    [string]$Dataset
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$datasetPath = (Resolve-Path -LiteralPath $Dataset).Path
$manifest = Get-Content -LiteralPath (Join-Path $datasetPath 'manifest.json') -Raw | ConvertFrom-Json
if (!$manifest.synthetic) { throw 'A synthetic dataset manifest is required.' }
$feedPath = Join-Path $datasetPath 'feed'
New-Item -ItemType Directory -Path $feedPath -Force | Out-Null
$currentFrame = Join-Path $feedPath 'current.fits'

function Publish-Frame([string]$Source) {
    $temporaryFrame = Join-Path $feedPath 'current.pending'
    Copy-Item -LiteralPath $Source -Destination $temporaryFrame -Force
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            if (Test-Path -LiteralPath $currentFrame) {
                $backupFrame = Join-Path $feedPath 'previous.fits'
                [IO.File]::Replace($temporaryFrame, $currentFrame, $backupFrame)
                Remove-Item -LiteralPath $backupFrame -Force
            } else {
                [IO.File]::Move($temporaryFrame, $currentFrame)
            }
            return
        } catch [IO.IOException] {
            if ($attempt -eq 29) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
}

$form = New-Object Windows.Forms.Form
$form.Text = 'QHY600 / 1000 mm - Polar Alignment Simulation'
$form.Size = New-Object Drawing.Size(560, 490)
$form.StartPosition = 'CenterScreen'
$form.BackColor = [Drawing.Color]::FromArgb(28, 34, 43)
$form.ForeColor = [Drawing.Color]::WhiteSmoke
$form.Font = New-Object Drawing.Font('Segoe UI', 10)
$layout = New-Object Windows.Forms.FlowLayoutPanel
$layout.Dock = 'Fill'
$layout.FlowDirection = 'TopDown'
$layout.WrapContents = $false
$layout.Padding = New-Object Windows.Forms.Padding(14)
$form.Controls.Add($layout)
$help = New-Object Windows.Forms.Label
$help.Text = "1. Capture reference 1 in N.I.N.A.`n2. Select the RA 90-degree scene, then capture reference 2.`n3. Start live adjustment; select correction scenes below."
$help.Size = New-Object Drawing.Size(510, 72)
$layout.Controls.Add($help)
$status = New-Object Windows.Forms.Label
$status.Size = New-Object Drawing.Size(510, 36)
$layout.Controls.Add($status)
$index = 0
foreach ($frame in $manifest.frames) {
    $button = New-Object Windows.Forms.Button
    $button.Size = New-Object Drawing.Size(510, 33)
    $button.FlatStyle = 'Flat'
    $button.ForeColor = [Drawing.Color]::WhiteSmoke
    $description = if ($index -eq 0) { 'Reference 1 - keep selected until first capture finishes' }
        elseif ($index -eq 1) { 'Rotate RA 90 degrees - capture the second reference' }
        else { 'Bolts corrected {0:0}% - nominal residual {1:0.00} arcmin' -f ($frame.correctionFraction * 100), $frame.expectedTotalArcminutes }
    $button.Text = $description
    $button.Tag = [pscustomobject]@{ Source = Join-Path $datasetPath ('fits\' + $frame.name + '.fits'); Description = $description }
    $button.Add_Click({
        try {
            Publish-Frame $this.Tag.Source
            $status.Text = 'Current scene: ' + $this.Tag.Description
        } catch {
            [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Frame update failed') | Out-Null
        }
    })
    $layout.Controls.Add($button)
    $index++
}
Publish-Frame (Join-Path $datasetPath ('fits\' + $manifest.frames[0].name + '.fits'))
$status.Text = 'Current scene: reference 1'
[Windows.Forms.Application]::Run($form)
