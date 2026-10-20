# Alignment target and image-drift estimate

The adjustment panel exposes a polar-error target in arcseconds. The default is 60 arcseconds (1 arcminute), adjustable from 15 to 600 arcseconds. This is a user-selected operating target, not an absolute-accuracy certification.

- **Green:** fresh total polar error is at or below the target. The label says the target is reached.
- **Amber:** above the target but no more than twice the target. Fine adjustment is still needed.
- **Red:** more than twice the target. Continue adjusting the altitude and azimuth bolts.
- **Neutral:** no measurement, stopped acquisition, or stale data. Previous success colors and drift estimates are removed.

The label accompanies the color so the result does not depend on color perception alone. Altitude and azimuth signs remain geometric offsets, not bolt-turn directions. Independent remeasurement and drift checks remain available to detect inconsistent results.

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
