# Field validation

Enable **Enable debug recording** under `Plugins → Installed → Live Polar Alignment → Options` to retain validation data. Recording defaults off and is stored per N.I.N.A. profile. It starts with the first reference, so reset before starting again if already calibrated. The `Field validation` panel provides Mark, Stability, and Folder. Physical accuracy remains unverified.

## Field procedure

1. Check PC time, site coordinates, focal length, pixel size, and solver. Record camera, optics, mount, tracking state, exposure, and weather in the notes. Begin with still captures for comparison.
2. Capture the first reference. Keep altitude, azimuth, and camera mounting fixed, rotate only RA by 60–90°, and capture the second reference. [Automatic RA rotation](auto-ra-rotation.md) performs both captures and the selected 60–90° movement. No mount sync is performed.
3. Start adjustment, then select `Stability` before touching the bolts. Keep RA, Dec, bolts, and camera stationary while collecting 20 new measurements. Alt/Az sigma is sample standard deviation in arcseconds: a measure of repeatability. Small values can coexist with a shared polar-error bias.
4. `Mark` saves the displayed measurement's original FITS when available, processed PNG, and note. It does not take a new exposure. Add a note such as `before adjustment` and save.
5. Adjust the bolts slightly and wait for the next measurement. Repeat Stability and Mark after adjustment. Record clouds, wind, vibration, or solve problems with a note and Mark. Stale measurements or stopping cancel an in-progress stability check.
6. With bolts fixed, Stop, Reset, and capture a new reference pair in a separate session. Compare the results, preferably using a different RA interval.
7. Compare with a separate method, such as a dedicated polar-alignment device or independent drift alignment. Record the method, time, angle units, and residual error. Compare the same physical state; document any bolt changes made using another tool. Meeting a 1-arcminute UI target does not establish 30-arcsecond accuracy.
8. On supported cameras, use a separate streaming session at the same exposure to compare update cadence, variation, and results. Streaming timestamps are estimates based on receive time.
9. Use `Folder` to open the session. Retain the entire folder and comparison results for later review. There is no automatic upload.

Recording errors do not stop alignment, but records may be incomplete. Check storage and folder access, then reset to start a new session. Change the recording setting before capturing the first reference.

## Stored data

Location: `%LOCALAPPDATA%\NINA\LivePolarAlignment\FieldSessions\<UTC-timestamp-ID>`.

- `session.json`: version, equipment, solver, site settings, initial note, and model limitations.
- `events.jsonl`: solved reference and measurement coordinates/WCS, actual DUT1 used, capture timestamps and mode, update cadence, errors, rejected geometry, start/stop/error events, Mark, and stability results. Each line is flushed immediately.
- `sample-*.fits`: original successful references and marked measurement frames. Unavailable when the provider supplies no RAW image.
- `sample-*-preview.png`: processed previews. Use FITS for original-image analysis.

Continuous raw frames and failed plate solutions are not all saved; use Mark at relevant points. Records contain site information, equipment identifiers, and notes, so review their contents before sharing. Synthetic previews use a separate temporary folder and are marked synthetic.

## Offline recalculation

Publish the review tool on Windows with the .NET 8 SDK and an installed N.I.N.A. distribution. Its project copies the required SOFA/NOVAS and JPLEPH files. A nonstandard N.I.N.A. location can be supplied through `-p:NinaInstallDirectory="C:\path\to\NINA"`.

```powershell
dotnet publish tools/Nina.LivePolarAlignment.FieldReview/Nina.LivePolarAlignment.FieldReview.csproj -c Release -o artifacts/field-review
.\artifacts\field-review\Nina.LivePolarAlignment.FieldReview.exe 'C:\path\to\session-folder'
```

The published tool requires the .NET 8 Windows Desktop Runtime. It writes `frames.csv` and `review.json` in the session's `review` folder, recalculating alignment geometry from saved WCS and DUT1 and comparing it with recorded errors. It does not plate solve FITS again or establish absolute accuracy. If an interrupted write truncates the last JSON line, earlier records are recovered with a warning.

The current model uses two reference images and linear WCS, without atmospheric refraction or optical distortion correction. Replay agreement and stability sigma do not establish physical polar-alignment accuracy. See [calculation and references](polar-alignment.md).
