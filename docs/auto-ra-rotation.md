# Automatic RA rotation

`Auto RA rotation` performs the first reference capture, RA rotation, verified stop and settling, then the second reference capture and continuous live measurement. It defaults on. Unsupported equipment switches the option off and falls back to manual rotation. In manual mode, Start captures only the first reference and displays an explicit RA-rotation prompt. Rotate RA, then press Capture rotated field; continuous live measurement starts automatically after that reference. The user adjusts altitude and azimuth bolts during live measurement.

## Settings and prerequisites

- Connect the camera in N.I.N.A. Equipment to enable Start. Manual polar alignment requires only the camera; automatic RA rotation additionally requires a connected, supported mount. Missing camera and mount connections use N.I.N.A.'s red circled-X indicator and a compact Not connected label beside Start and Auto RA respectively. Connected indicators are hidden; details appear on hover. Rotation settings collapse when Auto RA is off. Configure the solver, site, focal length, and pixel size, then point near the visible celestial pole.
- A connected, unparked equatorial mount must support RA `MoveAxis`, tracking on/off control, and a speed within 0.1–1°/s. Stop other sequences, guiding, and manual movement.
- Rotation defaults to 90°. Available angles are 60°, 70°, 80°, and 90°; remeasurement uses the same setting.
- `Reverse RA direction` defaults on and sends the same negative RA command as N.I.N.A.'s manual W button. Off uses the positive command of its E button. N.I.N.A.'s `PrimaryReversed` setting also applies. W/E icons indicate command direction, not image clockwise/counterclockwise motion or live encoder feedback.
- Check clearance for the selected direction, full rotation, and stopping margin, including the telescope, tripod, and cables. Remeasurement adds another rotation from the current position, so check that path again.

## Movement and stopping

Only RA is moved. The plugin does not move Dec, issue GoTo or sync, unpark automatically, or return to the starting position. It temporarily disables tracking and selects a supported speed no higher than 1°/s. RA coordinates are unwrapped across the 24-hour boundary to accumulate rotation. Axis estimation uses the rotation measured from the two images.

The primary button shows a play icon before starting. During reference capture or RA rotation it becomes a stop icon with Cancel; during live measurement it shows Stop. The same button requests cancellation, shows Stopping while cleanup runs, and returns to its next start/resume action only after cleanup. Reset is a separate compact icon button. Closing or hiding the panel also requests cancellation. The plugin sends zero speed, checks stop feedback, and restores the original tracking state. It waits two seconds before the second capture. If stopping fails, it attempts an abort, reports the error, and does not re-enable tracking without confirmation from the driver.

No movement response for six seconds, or exceeding the overall timeout, stops the operation. Connection, Dec, pier-side changes, and abnormal coordinate changes also stop it. Failed or cancelled rotation discards calibration. If rotation completes but only the second solve fails, recapturing does not repeat the rotation.

## Rotation range and validation

The [SharpCap Polar Alignment guide](https://docs.sharpcap.co.uk/4.1/9_PolarAlignment.htm) is the reference for approximately 90° rotation and a 60–80° alternative when considering obstructions and flexure. Smaller rotations are more sensitive to the same orientation error; equivalent precision at 60° and 90° is not assumed. Use the default 90° when clearance permits.

Mock-device tests and synthetic WPF previews cover the workflow. Physical mount movement, stopping, and accuracy remain unverified. The plugin provides no collision avoidance or mechanical-limit detection and cannot fully detect incorrect driver stop feedback. Debug recording saves rotation events and reference images in the session.
