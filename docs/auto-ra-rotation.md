# Automatic RA rotation

`Auto RA rotation` prepares the mount (including Unpark when required), then captures the first reference. Once its raw data and quick-look preview are available, first-image solving and RA rotation run concurrently. Both must succeed, including verified stop and settling, before the second reference capture and continuous live measurement. It defaults on. Unsupported equipment switches the option off and falls back to manual rotation. In manual mode, Start captures only the first reference and displays an explicit RA-rotation prompt. Rotate RA, then press Continue; continuous live measurement starts automatically after that reference. The user adjusts altitude and azimuth bolts during live measurement.

## Settings and prerequisites

- Connect the camera in N.I.N.A. Equipment to enable Start. Manual polar alignment requires only the camera; automatic RA rotation additionally requires a connected, supported mount. Missing camera and mount connections use N.I.N.A.'s red circled-X indicator and a compact Not connected label beside Start and Auto RA respectively. Connected indicators are hidden; details appear on hover. Rotation settings collapse when Auto RA is off. Configure the solver, site, focal length, and pixel size, then point near the visible celestial pole.
- A connected equatorial mount must support RA `MoveAxis`, tracking on/off control, and a speed within 0.1–1°/s. Stop other sequences, guiding, and manual movement.
- Rotation defaults to 90°. Available angles are 60°, 70°, 80°, and 90°; remeasurement uses the same setting.
- `Reverse RA direction` defaults on and sends the same negative RA command as N.I.N.A.'s manual W button. Off uses the positive command of its E button. N.I.N.A.'s `PrimaryReversed` setting also applies. W/E icons indicate command direction, not image clockwise/counterclockwise motion or live encoder feedback.
- Check clearance for the selected direction, full rotation, and stopping margin, including the telescope, tripod, and cables. Remeasurement adds another rotation from the current position, so check that path again.

## Movement and stopping

Only RA is moved. The plugin does not move Dec, issue GoTo or sync, or return to the starting position. It temporarily disables tracking and selects a supported speed no higher than 1°/s. Known simulator driver IDs (`ScopeSim.Telescope`, `ASCOM.Simulator.Telescope`, and `ASCOM.OmniSim.Telescope`, case-insensitive exact matches) may use a supported speed up to 4 degrees/s: about 23 seconds for 90 degrees, plus stop verification and settling. All other drivers retain the 1 degree/s cap. Simulator rotation retains feedback, cancellation, and verified stopping. RA coordinates are unwrapped across the 24-hour boundary to accumulate rotation. Axis estimation uses the rotation measured from the two images.

The primary button shows a play icon before starting. During reference capture or RA rotation it becomes a stop icon with Cancel; during live measurement it shows Stop. The same button requests cancellation, shows Stopping while cleanup runs, and returns to its next start/resume action only after cleanup. Reset is a separate compact icon button. Closing or hiding the panel also requests cancellation. The plugin sends zero speed, checks stop feedback, and restores the original tracking state. It waits two seconds before the second capture. If stopping fails, it attempts an abort, reports the error, and does not re-enable tracking without confirmation from the driver.

No movement response for six seconds, or exceeding the overall timeout, stops the operation. Connection, Dec, pier-side changes, and abnormal coordinate changes also stop it. Failed or cancelled rotation discards calibration. If rotation completes but only the second solve fails, recapturing does not repeat the rotation.

## Rotation range and validation

The [SharpCap Polar Alignment guide](https://docs.sharpcap.co.uk/4.1/9_PolarAlignment.htm) is the reference for approximately 90° rotation and a 60–80° alternative when considering obstructions and flexure. Smaller rotations are more sensitive to the same orientation error; equivalent precision at 60° and 90° is not assumed. Use the default 90° when clearance permits.

Mock-device tests and synthetic WPF previews cover the workflow. Physical mount movement, stopping, and accuracy remain unverified. The plugin provides no collision avoidance or mechanical-limit detection and cannot fully detect incorrect driver stop feedback. Debug recording saves rotation events and reference images in the session.

The Auto RA tooltip names the failing prerequisite, including parked or moving mounts, non-equatorial mode, missing RA/tracking control, unsupported speed, and invalid coordinates. When Auto RA is selected, Start uses N.I.N.A.'s Unpark operation if needed and verifies that the same mount is unparked and ready before the first exposure. Unpark failure or cancellation prevents capture. Manual mode never requests Unpark. Captured image previews appear before solving; axis guidance is displayed only after a successful solution.

The primary button uses short labels (Start, Continue, Resume; Cancel or Stop while working). Preparation/Unpark, reference capture, solving, and RA rotation progress are sent through `IApplicationStatusMediator` to N.I.N.A.'s application status bar at the bottom of the main window. Unpark uses the host mediator so N.I.N.A.'s own refusal conditions still apply. No GoTo or automatic return/parking is added.

The plugin control panel does not repeat operation status in its heading or below the alignment card. The host status entry uses one stable source and clears when the operation ends or the panel is disposed. Manual RA-rotation instructions remain in the host status bar while waiting for Continue.

## Preview latency

Single exposures use N.I.N.A. CaptureImage without waiting for its full image preparation/statistics pipeline. After raw decoding, a reduced grayscale quick-look image is published and the WPF render queue is given a turn before full-resolution ASTAP work runs on a worker thread. A separate display stretch does not modify raw pixels used for solving or saved evidence. When the solve completes, the full-resolution display and computed overlay are published; optional evidence saving does not delay the reference overlay. Timing markers in the N.I.N.A. log distinguish download, first preview, solve, and full-resolution display.

## Overlapping first solve and RA rotation

Automatic calibration freezes the first image, capture midpoint, and solver search coordinates before movement begins. Analysis uses those saved pixels while the mount rotates. A failure in either analysis or rotation cancels the sibling task; both tasks and mount stop verification finish before the operation returns or another capture can start. A failed or cancelled combined operation discards calibration. Manual capture remains sequential.

The first image stays visible during rotation. The Streaming option starts native video only after the two calibration references, during live adjustment; it does not provide a continuously refreshed video preview during calibration or rotation. Native streaming currently supports ZWO ASI and ToupTek drivers; unsupported drivers fall back to single-frame measurement.

Preview delivery and the freshness timer explicitly use the host application UI Dispatcher. N.I.N.A. composes plugins on a background loader thread; its unpumped Dispatcher must not receive image updates. The standalone workflow check also constructs the VM on a background thread to cover this host loading behavior. Logs record when each preview is applied to the UI, separately from raw download and solver timings.
