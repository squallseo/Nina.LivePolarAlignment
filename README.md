# N.I.N.A. Live Polar Alignment

Windows x64 plugin for N.I.N.A. 3.2.0.9001 or later. Current checkpoint: **0.1.0.90**.
It calibrates the mount's RA axis, returns to an adjustment reference, and guides
altitude/azimuth adjustment using a fixed star target followed by fine monitoring.
Absolute alignment accuracy across equipment combinations still needs field validation.

## Alignment methods

- **PoleMaster · Polaris pattern**: native QHY PoleMaster with its stock 25 mm lens
  and 1280 × 960 sensor. Start identifies the fixed Polaris pattern automatically;
  initial failure requests a click before RA rotation. References at approximately
  0°/40°/80° determine the axis and check consistency, followed by return and a fresh
  adjustment reference.
- **General camera · NINA plate solving**: uses NINA's configured primary and blind
  solvers. Automatic RA defaults to 90°; 60–90° and manual RA are supported. Automatic
  calibration returns RA before adjustment. Polaris is used when identified in frame;
  otherwise a detected reference star is boxed.

The connected camera selects the default method; a manual choice is retained for
that camera. The UI reports field of view, incompatible hardware, missing optics
and video limitations. General cameras without video use consecutive exposures.
Native QHY streaming support includes QHY600M and is determined by the driver/readout.
The plugin does not bundle ASTAP or replace NINA's configured solver.

## Workflow and interface

1. Connect the camera and equatorial mount. Configure site coordinates and main-camera
   focal length, pixel size and plate solving in NINA.
2. Select the method and press **Start**, directly below the selector. PoleMaster
   requests a Polaris click only if automatic identification fails.
3. Keep altitude, azimuth, Dec and camera mounting fixed during calibration. Check
   mechanical clearance for outgoing RA motion and return.
4. Move the selected star into the **fixed green target** using the adjustment bolts.
5. Start **fine monitoring** to inspect the current RA-axis error and celestial-pole target.

Both methods share a progress rail with completed checks and one highlighted stage.
General manual RA omits automatic return. Stop verifies RA stop; interrupted motion
does not cause a blind return. PoleMaster Home restart may move both RA and Dec.

**PA Image** and **PA Controls** dock independently and share one toolbar icon,
camera owner and session. Hiding one panel keeps capture active while the other is visible.

All settings are under **Plugins → Installed → Live Polar Alignment → Options**.
Capture exposure uses **milliseconds** in both methods. PoleMaster exposure locks
after calibration starts and unlocks after reset. Gain belongs to **NINA Equipment**;
old plugin-specific gain overrides are ignored.

Estimated image drift and independent drift verification are not user features.
Their numerical code remains for internal/offline tests. Optional axis remeasurement,
stability and field recording are separate from normal fine monitoring.

## Build, test and install

Requires Windows, .NET 8 SDK and an installed NINA distribution for native astrometry libraries.

```powershell
dotnet build src/Nina.LivePolarAlignment -c Release
dotnet test tests/Nina.LivePolarAlignment.Core.Tests -c Release
dotnet test tests/Nina.LivePolarAlignment.Integration.Tests -c Release
./scripts/Package-Plugin.ps1
```

Packages are written to `artifacts/packages/`. Extract the ZIP, close NINA and run
`Install-Plugin.ps1`. It installs only plugin files under
`%LOCALAPPDATA%/NINA/Plugins/3.0.0/Live Polar Alignment` and does not change NINA DLLs or SDKs.

## Source and documentation

| Directory | Contents |
| --- | --- |
| `src/Nina.LivePolarAlignment.Core/` | Geometry, matching/tracking, display and RA-motion calculations |
| `src/Nina.LivePolarAlignment/` | NINA integration, capture/session ownership, WPF panels and localization |
| `tests/` | Synthetic calculations, integration tests and optional local FITS replays |
| `tools/` | Synthetic UI preview, catalog simulator and offline review |
| `scripts/` | Plugin-only installation and packaging |

The five-star template derives from HYG v4.1. Its CC BY-SA 4.0 license and attribution
are in `src/Nina.LivePolarAlignment.Core/Data/` and the package. No QHY program code or artwork is included.

- [Method behavior and limitations](docs/polemaster.md)
- [Geometry and reference roles](docs/polar-alignment.md)
- [Automatic RA movement](docs/auto-ra-rotation.md)
- [Streaming and tracking](docs/streaming-review.md)
- [Optional remeasurement](docs/verification.md)
- [Field recording](docs/field-validation.md)
- [Installation](docs/install-package.txt)
- [Integration test setup](tests/Nina.LivePolarAlignment.Integration.Tests/README.md)
- [Synthetic WPF preview](tools/Nina.LivePolarAlignment.Preview/README.md)
