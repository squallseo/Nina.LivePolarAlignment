# Streaming adjustment

Current source: 0.1.0.90. Calibration references and the returned adjustment reference
use still exposures. PoleMaster also runs native video during its two RA intervals
and return so measured star boxes can follow current frames. Camera ownership is
drained before another still capture; video failure cancels and verifies motion stop.

## Driver eligibility and exposure

Native QHY, ZWO ASI and ToupTek RAW LiveView paths are supported where implemented.
QHY eligibility uses the native driver type, including QHY600M, rather than a model
allowlist or CanShowLiveView. The known Bin3x3 snapshot readout mode is excluded.
ASCOM and DSLR JPEG LiveView are outside this video path. A general camera without
video uses consecutive still exposures; unsupported PoleMaster video blocks its method.

Capture exposure is displayed in milliseconds in the single plugin Options view.
PoleMaster defaults to 100 ms and 1× binning; saved exposure takes precedence.
Its exposure locks after calibration starts and unlocks on reset. Gain is owned by
NINA Equipment. There is no plugin gain preset or queued gain-control feature.

## Current-frame guidance

A producer receives RAW frames into a capacity-one buffer, discarding superseded
frames. Preview preparation and original-RAW centroid measurement feed a spherical
fixed-reference tracker with parity and supported integer-binning checks. It does
not accumulate frame-to-frame transforms.

General-camera tracked guidance checks matched-star count, image coverage, geometry,
reference age and reprojection RMS. A background absolute solve refreshes the
reference and recovers lost tracking. At most one solve runs in flight, and an old
solved image must not replace a newer measured preview.

PoleMaster uses the fixed catalog template. An initial unseeded match requires at
least three stars; a clicked unambiguous fallback can use two. During rough adjustment,
the green destination is fixed at the returned reference and live pixels continue
without requiring a new solve for every display frame. Fine monitoring uses current
matched measurements for the axis error. A transient match failure retains coherent
guidance with freshness checks instead of presenting stale markers as a new measurement.

The general-camera fixed target uses identified Polaris when available, or an explicitly
boxed detected reference star. It continues through the normal NINA solve/tracking path.

## Ownership and validation limits

One capture gate owns acquisition and restores camera configuration for the same
connected camera. Stop and disconnect cancel the producer before releasing ownership.
The plugin does not create another native SDK connection.

Frame times estimate receive time minus half the requested exposure; native buffering
and transfer latency are unknown. USB bandwidth, readout, star signal and driver
behavior determine cadence. Short exposure alone does not guarantee a frame rate.

Synthetic checks cover RAW preview, star registration, fixed targets, background
recovery, continued preview during lost matching, cancellation and ownership cleanup.
Optional local FITS replays check captured images. Neither establishes physical
alignment accuracy, atmospheric/optical systematics or hardware throughput.
