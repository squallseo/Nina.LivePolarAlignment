# Polar alignment logic and references

This document describes the implemented calculation path. Geometry lives in `src/Nina.LivePolarAlignment.Core/`; capture, coordinate conversion, and session state live in `src/Nina.LivePolarAlignment/Capture/`.

## Core references and their roles

| Reference | Role in this implementation |
| --- | --- |
| [SharpCap 4.1 Polar Alignment](https://docs.sharpcap.co.uk/4.1/9_PolarAlignment.htm) | Primary behavioral reference: solve two images separated by RA rotation, identify the RA axis and celestial pole, then guide altitude and azimuth adjustments using repeated solved images. |
| [IAU SOFA](https://www.iausofa.org/current-software) through N.I.N.A. | Runtime astronomical coordinate conversion at each exposure's timestamp. The session calls N.I.N.A.'s `SOFA.CelestialToTopocentric`; its Julian-date helper uses NOVAS. |
| [PHD2 drift alignment tutorial](https://openphdguiding.org/man/Tutorials.htm) | Observing procedure for the optional, separate two-field declination-drift verification. |
| [N.I.N.A. imaging documentation](https://nighttime-imaging.eu/docs/master/site/tabs/imaging/) and [plugin template](https://github.com/isbeorn/nina.plugin.template) | Host capture, LiveView, and plugin integration. |

SharpCap is the main reference for the alignment workflow. The axis estimator itself is this project's relative 3D orientation calculation in [PolarAlignmentGeometry.cs](../src/Nina.LivePolarAlignment.Core/PolarAlignmentGeometry.cs). The repository does not attribute that estimator to a specific research paper. The equations below document our implementation; the SharpCap user guide is not a specification of its internal formulas. PHD2 supplies the drift-observation procedure, while the plate-solution regression is implemented in our own `DriftValidation` core.

## Capture and coordinate conversion

`NinaPolarAlignmentCaptureService.CaptureAndSolveAsync` uses N.I.N.A.'s camera and imaging mediators and the configured plate solver. Each frame carries center RA/Dec, position angle, parity, pixel scale, image dimensions, and exposure midpoint. Capture ownership is released on completion or cancellation.

`PolarAlignmentSession.ToHorizon` transforms the J2000 center and tangent directions with N.I.N.A.'s SOFA path using exposure-midpoint UTC and site latitude/longitude. Direction vectors use east, north, up (ENU) order. `FromEquatorial` constructs the image basis, with parity applied to the image-right axis. Missing or invalid DUT1 falls back to zero seconds and is flagged by `UsedApproximateEarthRotation`. Atmospheric refraction and site elevation are not currently applied.

## Estimating the RA axis from two positions

`PolarAlignmentSession.AddCalibration` compares two image orientations separated by RA-only rotation. Altitude, azimuth, Dec, and camera mounting must remain fixed.

`PolarAlignmentGeometry.EstimateAxis(SkyOrientation, SkyOrientation)` normalizes each basis and checks orthogonality and matching parity. For corresponding basis directions `u_i` and `v_i`, it computes:

```text
cos(theta) = clamp((sum(dot(u_i, v_i)) - 1) / 2, -1, 1)
axis = normalize(sum(cross(u_i, v_i)))
conditioning = sin(theta / 2)
```

The core rejects rotations below 1° or above 179°. The session requires at least 30° of measured rotation and calibration within 15° of the visible pole; 60–90° is recommended. The axis sign is selected to point toward that pole.

Two center directions alone do not uniquely determine the axis: image orientation is also required. A separate core overload fits a plane to three or more boresight directions, but the current two-position capture workflow does not use it.

## Polar error and image projection

`CalculateError` compares a normalized mount axis `a = (E, N, U)` with the visible celestial pole `p`. At latitude `phi`, the north pole is `(0, cos(phi), sin(phi))`; the south pole is its opposite. The axis is oriented toward the visible pole before calculating errors. Internal angles are radians; returned errors are arcminutes.

```text
total error = atan2(length(cross(a, p)), dot(a, p))
altitude error = asin(clamp(U, -1, 1)) - abs(phi)
azimuth error = wrapToPi(atan2(E, N) - atan2(p.E, p.N))
```

Positive altitude error means the axis is too high. Azimuth increases from north toward east, and its error is the signed offset from the target pole. These are measured offsets, not bolt-turn commands. Total error is calculated from the vectors rather than a simple quadratic sum of altitude and azimuth offsets.

`ProjectDirection` uses a gnomonic projection to display the axis and pole. Focal length in pixels is `206264.806 / pixel scale in arcseconds per pixel`. Image Y increases downward. Directions outside the forward hemisphere are not projected.

## Repeated measurements and verification

`GetGuidance` uses `TransformDirection` to carry the calibrated axis from the second reference orientation into the current solved orientation. Every coordinate conversion uses the frame timestamp. RA/Dec movement or camera remounting requires recalibration. The session rejects changed dimensions or parity, scale changes above 2%, and timestamps preceding the reference.

Every adjustment frame currently receives a full plate solve. Streaming overlaps acquisition and processing with a capacity-one `LatestFrameBuffer` that discards superseded frames. Fast star registration exists in `FrameRegistration` and its tests but is not connected to the live capture path.

`DriftValidation` estimates small polar errors from declination slopes and hour angles in two fields. Remeasurement captures a fresh reference pair in a new RA interval. Stability reports sample standard deviation over 20 stationary frames. Repeatability and absolute accuracy are separate properties.

- `tests/Nina.LivePolarAlignment.Core.Tests/`: axis estimation, hemispheres, parity, projection, registration, RA rotation, frame buffering, and drift calculations.
- `tests/Nina.LivePolarAlignment.Integration.Tests/`: independent coordinate inputs through actual N.I.N.A. SOFA/NOVAS libraries, timestamps, calibration changes, and capture-state boundaries.
- `tools/Nina.LivePolarAlignment.Preview/`: production WPF views and workflow with synthetic input.
- Physical axis accuracy, flexure, update cadence, and mount stopping behavior require field validation.
