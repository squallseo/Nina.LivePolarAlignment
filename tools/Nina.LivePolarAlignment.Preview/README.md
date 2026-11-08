# Synthetic WPF preview

Runs the production views with a synthetic capture service. No camera or mount is
connected. The NINA database/profile context uses a process-specific temporary folder.
Requires Windows, .NET 8 Desktop Runtime and an installed NINA distribution for native libraries.

```powershell
dotnet run --project tools/Nina.LivePolarAlignment.Preview -c Release
```

The main preview has an image area and controls with a draggable divider; a narrow
window stacks them. The installed plugin exports two independently dockable panels.
Exposure/capture timing and sky images in the preview are synthetic.

## Current regression suite

```powershell
dotnet run --project tools/Nina.LivePolarAlignment.Preview -c Release -- --render-polemaster-to artifacts/polemaster-preview
```

This exercises automatic identification and clicked fallback before motion, preview
ownership/cleanup, two 40-degree intervals, return, manual adjustment, monitoring
restart, shared dock panels, the single toolbar filter, plugin Options binding,
general-camera fixed-target presentation and progress rail, and camera-method defaults
and capability warnings. PNGs and a verification report are written to the requested folder.

Use `--render-markers-to artifacts/marker-preview` for isolated overlay rendering.
Use `--polemaster-demo` for an interactive synthetic daytime workflow. Press Start
to see calibration, return and adjustment; no physical equipment is controlled.

The older `--render-to` and `--render-adjustment-to` entries are developer fixtures
for additional numerical/UI scenarios, including internal diagnostics. They do not
define which tools are exposed in the current installed UI.

For another NINA installation, pass `-p:NinaInstallDirectory="C:/path/to/NINA"`.
The preview uses NINA theme resources and English/Korean localization. A successful
synthetic run does not establish hardware frame cadence or absolute alignment accuracy.
