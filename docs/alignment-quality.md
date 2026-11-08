# Alignment target and image-drift estimate

Current UI: the error target is configured in plugin Options (PoleMaster defaults
to 30 arcseconds; general-camera defaults to 60). The image-drift estimate below
is retained as developer documentation and is not exposed as a user feature.

Plugin Options exposes a polar-error target adjustable from 15 to 600 arcseconds.
PoleMaster defaults to 30 arcseconds and the general-camera defaults to 60.
This is a user-selected operating target, not an absolute-accuracy certification.

- **Green:** fresh total polar error is at or below the target. The label says the target is reached.
- **Amber:** above the target but no more than twice the target. Fine adjustment is still needed.
- **Red:** more than twice the target. Continue adjusting the altitude and azimuth bolts.
- **Neutral:** no measurement, stopped acquisition, or stale data. Previous success colors and drift estimates are removed.

The label accompanies the color so the result does not depend on color perception alone.
Altitude and azimuth signs remain geometric offsets, not bolt-turn directions.
Optional axis remeasurement remains available; the independent drift procedure is internal only.

## Why this is not a guiding RMS conversion

Polar error is an angle between two axes. Guiding RMS also depends on seeing, mount periodic error, backlash, cadence, guider settings, optics, and the target position. A single polar-error value cannot predict a 0.5-arcsecond guiding RMS. PHD2's [polar alignment guidance](https://www.openphdguiding.org/man/Tools.htm) explains that guiding can correct much declination drift while field rotation remains a separate concern.

The panel instead shows a conservative, position-independent upper bound on the image drift attributable to polar error without guiding. It also integrates that bound over a separate imaging-exposure setting, default 300 seconds, and divides by the solved pixel scale to show pixels. This exposure is separate from the short alignment captures.

## Model

Let the unit mount and pole axes be `a` and `p`, separated by `epsilon`, and use sidereal angular speed `omega = 2*pi / 86164.0905` radians per second. The difference between the two angular-velocity vectors has magnitude:

```text
maximum angular speed = |omega*a - omega*p| = 2*omega*sin(epsilon/2)
motion upper bound = maximum angular speed * exposure seconds
pixel upper bound = motion in arcseconds / solved arcseconds per pixel
```

The cross product with any unit viewing direction cannot exceed this magnitude, so integration gives an upper bound on displacement. It is not a prediction of the actual trajectory, a post-guiding residual, a seeing model, or a field-rotation calculation.

At 1 arcminute of polar error, the rate bound is about 0.263 arcseconds/minute and the five-minute bound is about 1.31 arcseconds. At the QHY600/1000 mm scale of approximately 0.776 arcseconds/pixel, that is about 1.69 pixels. Limiting this unguided contribution to 0.5 arcseconds over five minutes requires roughly 23 arcseconds of polar error or less; this does not promise 0.5-arcsecond guided performance.

Implementation: `PolarAlignmentImpact` in the core project. Tests compare the bound with independently propagated rigid rotations across sky directions, including zero error and invalid inputs. The WPF preview checks threshold boundaries, visible labels, actual colors, pixel display, and stale-state behavior.

## Operator guidance in 0.1.0.42

The status card separates the present state from the next action. Fresh adjustments show an actionable altitude or azimuth direction first, with residual errors below. The first suggested axis is chosen by comparing altitude error with the azimuth error projected onto the tangent sky plane. Within the user-selected total-error target, both axis instructions change to Hold; reaching the target does not certify hardware accuracy.

Errors above 30 arcminutes use a coarse-approach message. Markers outside the actual captured image use an off-image message, the location overview, and numerical errors; this is separate from cropping or zooming the display. The operator may stop, reframe, Reset, and recalibrate when the markers cannot be compared. Reframing during an active calibration is not recommended.

As a UI guardrail, errors exceeding 5 degrees or unavailable/non-finite marker geometry suppress adjustment advice, success colours, image-drift estimates, and the detail view, and request a check of pointing/camera configuration followed by Reset and new calibration. Five degrees is a conservative operating threshold for this adjustment interface, not a claim that every larger geometric estimate is mathematically invalid.

Stale measurements retain their matching old image and geometry but dim them, hide the correction arrow, and explicitly ask the operator to stop adjusting. Paused acquisition, camera disconnection, RA rotation/return, verification, solver failure, and calibration also receive distinct instructions. Left/right is defined while facing the visible celestial pole; it never specifies clockwise or counterclockwise bolt rotation. Large errors display degrees, ordinary errors arcminutes and arcseconds, so 30 arcseconds is displayed as 30″ rather than 0.50′.
