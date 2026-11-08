# PoleMaster and general-camera alignment

Current behavior: **0.1.0.90**.

## Method selection and compatibility

The UI offers stock PoleMaster pattern matching and general-camera alignment using
NINA's configured primary/blind solvers. Connected PoleMaster defaults to pattern
matching; a main camera defaults to NINA solving. A manual choice stays selected
for that camera. No external solver executable is bundled.

Pattern mode requires native QHY PoleMaster, stock 25 mm optics, 3.75 µm pixels and
a 1280 × 960 sensor at 1×. It validates scale and a northern polar field without
overwriting the main telescope optical profile. The template uses HIP 11767,
37391, 115746, 19454 and 84535; provenance and licensing are in Core/Data.

Video capability follows the connected driver/readout, including native QHY600M.
General cameras without video use consecutive still exposures with a warning.
Incompatible PoleMaster hardware/video or missing general-camera optical settings
blocks Start with an explanation. Sensor dimensions, pixel size and focal length
provide the displayed field of view.

A 36 × 24 mm sensor at 1000 mm covers about 2.06° × 1.38°, or 1.24° from center to
corner. The nearest template neighbor is about 1.59° from Polaris. With Polaris
centered, even that neighbor is outside the image: use the general-camera method.

## PoleMaster calibration

1. Connect camera and mount. A motion-free live aiming preview starts.
2. Set exposure in plugin Options and gain in NINA Equipment. Default PoleMaster
   exposure is 100 ms; saved user exposure takes precedence.
3. Press Start. Initial automatic matching requires at least three template stars.
   Failure requests a Polaris click and resumes aiming without RA rotation or Home.
4. A click selects the nearest raw star centroid within 20 native pixels. The click
   marker is provisional; matching verifies identity. An unambiguous clicked
   match may use Polaris and one surrounding template star.
5. Capture at the initial position, rotate approximately 40°, capture the middle
   reference, rotate another approximately 40°, and capture the final reference.
6. Compare early, late and full-span axis estimates. The middle reference checks
   consistency; the full-span solution supplies the adjustment axis.
7. Return in the reverse RA direction and solve a fresh adjustment reference.

Motion uses supported mount rates. Exact 40.000° positioning is unnecessary because
measured orientations determine the axis. Return verifies its endpoint. Device
identity, Dec, pier side, direction, feedback freshness and stop checks remain active.
The default outgoing command is positive RA (`reverse=false`), matching the requested
counterclockwise appearance on the tested setup; driver reversal and viewing side matter.

Stop cancels and verifies RA stop. Failure after rotation requires fresh calibration
through explicit Home restart. No blind return is issued after interruption.
Home restart may move RA and Dec and retains capture settings.

## Fixed-target adjustment and monitoring

The returned reference determines one fixed target. Move Polaris into the green
circle with altitude/azimuth bolts. The target does not chase subsequent motion;
the enlarged view uses the same target and pixel geometry.

General alignment uses this presentation too. If the solution and detections
identify Polaris in frame, use Polaris. Otherwise a detected star near image center
is selected, boxed and labelled as the reference star. Southern fields or frames
without a usable reference retain axis guidance.

After rough placement, start fine monitoring. Current solved/tracked measurements
update the axis error against the celestial pole. The target square and measured
axis support final adjustment. A fixed rough target does not replace this current
error check. Stale measurements must not be presented as fresh guidance.

## Panels, settings and verification

PA Image and PA Controls dock independently and share one toolbar icon, capture
owner and session. One icon opens both if either is missing or hides both when
both are visible. Hiding one panel alone keeps capture active in the other.

Both methods share a progress rail. General automatic RA shows six stages; manual
RA omits automatic return. Start is directly below the method selector. The first
English PoleMaster stage is “Polaris” to fit one line.

All settings are in Plugins > Installed > Live Polar Alignment > Options. Exposure
uses milliseconds in both methods. PoleMaster exposure locks through calibration,
adjustment, monitoring and pauses, then unlocks after reset. Gain comes from NINA
Equipment; the plugin does not apply old saved gain overrides or a preset gain.

Estimated image drift and independent drift verification are not in the user UI.
Their numerical code remains for internal/offline tests. Optional axis remeasurement,
stability and field recording are separate from normal monitoring.

Synthetic tests cover calibration, guarded motion/return, identification failure
and clicked fallback, fixed targets, monitoring restart, capability warnings and
shared-panel ownership. Optional local FITS tests replay captured frames without
committing them. Driver/field attempts do not establish absolute alignment accuracy;
no atmospheric-refraction or lens-distortion model is calibrated.

The plugin package never changes NINA DLLs or SDKs. Separate local host work is
outside this repository. Its custom QHY gain-preservation change was rolled back;
this plugin does not require or distribute that patch.
