# Session integration tests

These Windows x64 tests exercise the actual NINA astrometry native libraries and the
plugin session boundary. They synthesize ICRS/J2000 plate solutions from independent
known horizon orientations with SOFA's inverse transform. They check northern and
southern sites, both image parities, two-frame RA calibration, known mount adjustments,
exposure timestamps separated by hours, projected guidance, and session state guards.

Install NINA, then run:

```powershell
dotnet test tests/Nina.LivePolarAlignment.Integration.Tests -c Release
```

For a nonstandard installation, pass `-p:NinaInstallDirectory="C:\path\to\NINA"`.
The project copies the installed x64 SOFA, NOVAS and SQLite native libraries to its
test output. No third-party binaries are committed. Missing native libraries cause
the numerical tests to fail; they are not silently skipped. DUT1 uses NINA's value
when available and the documented zero-second approximation otherwise, matching the
production adapter. A fixture redirects `LOCALAPPDATA` inside the test process to a
temporary directory, so no user database is created, migrated, or modified.

These tests do not connect a camera, run a plate solver, or validate a physical mount.
