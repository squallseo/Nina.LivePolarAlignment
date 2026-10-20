# N.I.N.A. Live Polar Alignment

A N.I.N.A. plugin that estimates the mount's right ascension axis from two reference images and guides altitude and azimuth adjustments using axis and celestial pole overlays.

This experimental version includes camera capture, plate solving, polar error calculation, and an adjustment interface. Accuracy and update speed have not yet been validated on physical equipment.

## Features

- **Two-position calibration**: Estimate the RA axis from image orientations before and after an RA rotation. Automatic rotation defaults to 90° and supports 60–90°. Manual rotation is also supported.
- **Live adjustment guidance**: Repeated captures and plate solves update total, altitude, and azimuth errors, axis and pole overlays, and a close-up view.
- **Alignment quality and image impact**: A configurable polar-error target drives green, amber, and red guidance. An unguided drift upper bound is shown in arcseconds per minute and in arcseconds/pixels over the selected imaging exposure.
- **Camera streaming acquisition**: Solve the latest frame from native ZWO ASI and ToupTek video paths, with fallback to repeated still captures. Display updates still wait for a complete plate solution; continuous video with tracked-star guidance is not implemented.
- **Alignment verification**: Remeasure using fresh reference images, collect a 20-frame stability check, and optionally perform an independent drift check.
- **Field recording**: Optionally save reference images, measurements, and notes for offline recalculation.
- **N.I.N.A. integration**: An Imaging tab docking panel with host theme support and English and Korean localization.

## Workflow

1. Connect a camera and configure plate solving, site coordinates, focal length, and pixel size.
2. Point within 15° of the visible celestial pole and select `Start` to capture the first reference.
3. Automatic mode rotates RA and captures the second reference. In manual mode, keep altitude, azimuth, and Dec fixed, rotate only RA by 60–90°, and capture the second reference. The measured rotation must be at least 30°.
4. Live adjustment starts after both references are solved. Use the mount's altitude and azimuth bolts to bring the RA axis marker onto the celestial pole marker; the current implementation updates guidance only after each full plate solve.
5. Stop when finished and optionally run remeasurement, stability, or drift checks. Reset calibration after changing the camera mounting or capture configuration.

Before automatic rotation, check that the selected direction and rotation range are clear of equipment interference. Keep RA, Dec, and camera mounting fixed during adjustment. Signed errors describe geometric offsets; they do not specify which way to turn an adjustment bolt.

Atmospheric refraction correction is not currently applied. The absolute site latitude must be at least 1° and less than 89.5°. Every live update requires a full plate solve; the star registration core is not yet connected to the capture workflow.

## Current implementation checkpoint

Version 0.1.0.38 retains the last solved image and matching overlay while another frame is analyzed, and reuses successful coordinates as search hints to avoid repeated blind searches. These changes improve the existing solve-based workflow; they do not implement PoleMaster-style continuous tracking.

The next implementation task is to separate live image acquisition and star tracking from absolute plate solving: establish calibration from solved references, track the current star field for responsive adjustment guidance, and re-solve for recovery or independent verification. The registration core exists but is not wired into this workflow. A fixed old overlay on unrelated new pixels must not be presented as measured guidance.

Validation includes 148 automated tests, synthetic UI workflows at 60/70/80/90 degrees in both directions, second-reference solve retries, and full-resolution catalog-image replay through N.I.N.A. and ASTAP. Physical hardware cadence and alignment accuracy remain unverified. See [streaming behavior and limitations](docs/streaming-review.md) for the current acquisition boundary.

## Source layout

| Path | Contents |
| --- | --- |
| `src/Nina.LivePolarAlignment.Core/` | Axis estimation, error calculation, image registration, RA rotation, and drift calculations |
| `src/Nina.LivePolarAlignment/` | N.I.N.A. capture integration, alignment sessions, WPF views, and localization |
| `tests/` | Core calculation and N.I.N.A. integration tests |
| `tools/` | Synthetic UI preview and offline field-session review |
| `scripts/` | Installation and packaging |
| `docs/` | Core calculations, references, and feature guides |

## Build and install

Targets Windows x64, the .NET 8 SDK, and N.I.N.A. 3.2.0.9001 or later.

```powershell
dotnet build src/Nina.LivePolarAlignment -c Release
dotnet test tests/Nina.LivePolarAlignment.Core.Tests -c Release
dotnet test tests/Nina.LivePolarAlignment.Integration.Tests -c Release
.\scripts\Package-Plugin.ps1
```

Packages are generated in `artifacts/packages/`. Extract the ZIP, close N.I.N.A., and run the included `Install-Plugin.ps1`. Restart N.I.N.A. and open the `Live Polar Alignment` panel in the Imaging tab. [Installation guide](docs/install-package.txt)

Integration tests and the UI preview require native libraries from an installed N.I.N.A. distribution. [Integration test setup](tests/Nina.LivePolarAlignment.Integration.Tests/README.md) · [Synthetic UI preview](tools/Nina.LivePolarAlignment.Preview/README.md)

## References

The primary workflow reference is [SharpCap Polar Alignment](https://docs.sharpcap.co.uk/4.1/9_PolarAlignment.htm). The RA axis estimator uses this project's relative 3D orientation geometry. Astronomical coordinate conversion uses N.I.N.A.'s [IAU SOFA](https://www.iausofa.org/current-software) path; the optional drift check follows the observing procedure in the [PHD2 drift alignment tutorial](https://openphdguiding.org/man/Tutorials.htm). See [polar alignment logic and references](docs/polar-alignment.md) for the formulas, source mapping, and scope of each reference.

## Implementation and usage guides

- [Polar alignment logic and references](docs/polar-alignment.md)
- [Alignment target and image-drift estimate](docs/alignment-quality.md)
- [QHY600 catalog-image simulator](tools/Nina.LivePolarAlignment.Simulator/README.md)
- [Automatic RA rotation](docs/auto-ra-rotation.md)
- [Streaming behavior and limitations](docs/streaming-review.md)
- [Alignment remeasurement](docs/verification.md)
- [Independent drift verification](docs/independent-drift.md)
- [Field recording and offline review](docs/field-validation.md)
