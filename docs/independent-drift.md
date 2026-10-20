# Independent drift verification

`Independent drift verification` estimates residual polar error from declination drift in two fields during sidereal tracking. It does not reuse the axis estimated by the two-position RA calibration. Move between fields manually. This check sends no GoTo, axis movement, sync, tracking on/off, or guiding-stop commands.

## Workflow

1. Finish polar adjustment and stop capture. Optionally retain remeasurement and stability results first.
2. Expand the independent verification panel and enable it. Observation time defaults to 180 seconds per field, adjustable from 120 to 540 seconds. Target error defaults to 60 arcseconds, adjustable from 15 to 600 arcseconds.
3. Confirm that the mount is unparked, equatorial, and tracking at the sidereal rate. Disable guiding, dithering, shift tracking, and all Dec corrections, including those from external programs.
4. Manually move to a field within 25° of the celestial equator, within 15° of the meridian, and at least 25° above the horizon. Wait for movement and vibration to stop, confirm the stationary-state switch, and start the first observation.
5. Keep the bolts, mount, and camera fixed. The check solves at least 20 distinct exposures. It always uses still captures even when Streaming is enabled. Exposure, solving, and waiting can extend the requested observation time.
6. Without adjusting the bolts, move to a second field near the celestial equator with an absolute hour angle of 45–75°, east or west. Altitude must remain at least 25°. Suitable second fields may be unavailable at high latitudes.
7. Recheck tracking, guiding, and stationary conditions, then start the second observation. Complete both fields within 30 minutes from the first exposure to the last.
8. Review the assessment and estimated range. If realignment is recommended, save the session, disable independent mode, manually return near the celestial pole, and capture fresh calibration references before adjusting the bolts.

Disabling independent mode or resetting clears its result but retains saved files. Stopping, errors, or connection changes during observation discard both field results and produce an inconclusive assessment. There is no automatic return movement.

## Assessments

- **Realignment recommended:** remeasured residual error exceeds the target, or the conservative lower bound of the drift estimate exceeds it. Check equipment and settings as well.
- **Checks disagree:** remeasurement and drift estimates differ substantially. Check guiding, tracking, refraction, and camera rigidity, then repeat.
- **Inconclusive:** variation or uncertainty is too high, or field, timing, frame, or equipment conditions are invalid. This alone does not establish poor alignment. Repeat under stable conditions with a longer observation.
- **No excess detected by current checks:** available checks did not detect error above the target within their detection limits. This does not certify absolute accuracy.
- **Independent verification pending:** stability and RA remeasurement alone do not complete independent verification.

Stability starts manually during normal adjustment and defaults to automatic after RA remeasurement. Independent drift is a separate optional check and does not start automatically afterward.

## Calculation and limits

N.I.N.A.'s SOFA path converts timestamped J2000 plate solutions to observed declination and hour angle without atmospheric refraction. `DriftValidation` fits declination against time and combines the two fields' hour-angle coefficients to estimate two small polar-error components. It applies latitude and hemisphere signs and reports total error. Synthetic RA-tracking data with known axis offsets check signs and magnitudes in both hemispheres.

Regression requires at least 20 frames over 120 seconds. Frame intervals must be 1–30 seconds, with an individual observation span no longer than 600 seconds. Duplicate or old timestamps, changed image dimensions, parity or scale, movement inconsistent with expected sidereal hour-angle change, and excessive scatter are rejected. Insufficient field separation or a combined observation span over 30 minutes prevents an assessment.

The displayed range is an engineering guard: a 3-sigma noise allowance accounting for regression standard error and positive residual correlation, plus a minimum 30-arcsecond allowance. Each observation uses a noise floor of `max(0.3 arcseconds, 0.1 × pixel scale)`. This range has not been validated as a confidence interval for atmospheric, optical, or solver systematics. A small drift over 180 seconds may not resolve a 60-arcsecond target; longer observations or comparison equipment may be needed.

The calculation differs from the calibration-axis estimate but shares its camera and plate solver. Refraction, flexure, solver bias, tracking models, and bolt movement can affect results. External corrections and physical bolt movement cannot all be detected automatically. Physical equipment accuracy and behavior remain unverified.

## Recording and offline review

With debug recording enabled, a separate session stores each frame's observed coordinates, hour angle, altitude, original WCS, both regression results, final estimate, target, and assessment. The first and last original FITS images from each field are saved when available. The check also works without file recording.

FieldReview replays `drift_sample` records and writes results to `independentDrift` and `driftFrames` in `review.json`. Incomplete or cancelled records are not treated as passing results. `frames.csv` contains only polar calibration and adjustment frames. Agreement during replay is separate from physical accuracy.

The observing-field procedure follows the [PHD2 drift alignment tutorial](https://openphdguiding.org/man/Tutorials.htm). The plugin does not connect to PHD2; it uses its own regression over changes in N.I.N.A. plate solutions. See [core logic and reference roles](polar-alignment.md).
