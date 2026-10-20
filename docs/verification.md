# Alignment remeasurement and stability

`Verify alignment` recalculates polar error from a fresh reference pair. Keep the altitude and azimuth bolts, Dec, and camera mounting fixed; only RA is rotated.

## Workflow

1. Complete calibration and adjust the bolts while watching live measurements.
2. Stop capture. Keep the bolts, Dec, and camera fixed.
3. Expand `Verify alignment`, select the rotation direction, and check clearance for an additional 60–90° rotation from the current position, including stopping margin. This differs from the original calibration path.
4. Select `Verify with auto RA rotation`. A connected, unparked equatorial mount with automatic RA support is required. Otherwise reset and use two manual reference captures for a separate remeasurement.
5. Capture a new first reference at the current position. The previous calibration model also measures this image to establish the comparison value.
6. Rotate only RA by the selected 60–90°, verify the stop, wait for settling, and capture the new second reference. Estimate the axis and residual error using only the new pair.
7. Compare the previous estimate, remeasured error, and Alt/Az differences. Successful calibration replaces the references used for live adjustment. There is no automatic return movement.

Failure or cancellation discards incomplete references and results. Capture new references before continuing. Movement handling and limits follow [automatic RA rotation](auto-ra-rotation.md). Physical mount accuracy and behavior remain unverified.

## When stability starts automatically

- **Normal live adjustment:** start `Field validation → Stability` manually after fixing the bolts.
- **Remeasurement:** `Then collect 20 stationary frames` defaults on. After successful remeasurement, 20 new frames are captured and solved automatically. Keep the bolts, RA, Dec, and camera stationary. Turning the option off limits the operation to the reference-pair comparison.
- Remeasurement and stability work with debug recording off. Enable recording before reference capture to retain files.

Stability reports the sample standard deviation of Alt/Az in arcseconds. It measures variation rather than absolute accuracy. Stopping, stale measurements, or duplicate frames cancel an in-progress check. Each sample requires exposure and plate-solving time.

## Interpreting and recording results

Remeasuring in a new RA interval helps compare rotation-dependent errors, reference-image problems, and optical movement. The same solver and linear WCS geometry can retain shared biases. Small differences or low sigma do not establish absolute accuracy. Use a separate method, such as drift observation, for independent verification.

`Previous` applies the old model to the new first image immediately before remeasurement. `Remeasured` uses the new pair. Alt/Az differences are new minus previous values; they are separate from the following 20-frame variation. Synthetic preview values do not demonstrate physical equipment accuracy.

With debug recording enabled, the plugin closes the previous session and opens a separate remeasurement folder. It records the new references, previous folder path, comparison, RA events, and optional stability result. Saved WCS data can be recalculated with the offline review tool.
