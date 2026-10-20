# Catalog-based QHY600 simulator

This tool renders real catalog star positions into synthetic raw images and checks the actual N.I.N.A. directory camera, image decoder, ASTAP solver, and plugin calibration session. It does not connect physical equipment.

## Geometry and data

- QHY600 active area: 9576 × 6388 pixels at 3.76 micrometers.
- Telescope: 1000 mm focal length, f/5; aperture 200 mm.
- Field: approximately 2.063° × 1.376° at 0.7756 arcseconds/pixel. Focal ratio affects the illustrative brightness/PSF assumptions, not this field calculation.
- Gaia DR3 sources brighter than G=15 within 3.5° of the north pole, supplemented by missing bright Hipparcos stars, including Polaris. Proper motion is applied approximately.
- Independent Python/ERFA horizon-to-ICRS conversion, synthetic Gaussian PSFs, background/read-noise approximation, and saturation. This is not calibrated QHY600 photometry or a seeing model.
- Two reference orientations separated by 90° about an axis initially 10 arcminutes high and 12 arcminutes offset in azimuth, followed by correction scenes.

`manifest.json` records geometry, observing site, times, source count, and expected errors. Generated images, catalogs, solvers, and databases belong under ignored `artifacts/`, not in Git.

## Generate and verify

Create a Python environment with `numpy`, `astropy`, `requests`, and `Pillow`. Install ASTAP and its D05 or denser star database from the [official download page](https://www.hnsky.org/astap.htm).

```powershell
python tools/Nina.LivePolarAlignment.Simulator/generate.py --output artifacts/camera-simulator/qhy600-1000mm --cache artifacts/simulation --latitude 37.5 --longitude 127
dotnet run --project tools/Nina.LivePolarAlignment.Simulator -c Release -- artifacts/camera-simulator/qhy600-1000mm 'C:\path\to\astap.exe'
```

Use the same observing site in N.I.N.A. as in the manifest. The replay uses N.I.N.A.'s real `SimulatorCamera` in DIRECTORY mode, XISF decoding, `ImageSolver`/ASTAP, and the plugin's production `PolarAlignmentSession`. It checks the solved error against the independent renderer, not against injected WCS solutions. It writes `replay-results.json`. A third argument can create a fresh `.profile` file with simulator, optics, site, and solver settings; existing files are never overwritten.

## Interactive playback in the installed product

The `camera/` directory contains timestamped XISF for exact offline replay. Do not use that static recording for a live freshness check: the plugin correctly marks old measurements stale.

For interactive use, the controller publishes one replaceable FITS frame in `feed/`. N.I.N.A. 3.2 assigns the current capture timestamp to FITS input. The frame remains selected until you press another controller button, so captures, previews, and retries cannot unexpectedly consume the next calibration scene.

```powershell
powershell.exe -STA -NoProfile -File tools/Nina.LivePolarAlignment.Simulator/Start-Controller.ps1 -Dataset artifacts/camera-simulator/qhy600-1000mm
```

1. Select the separate simulation profile. Choose the built-in **N.I.N.A. Simulator Camera**, open its setup, select **DIRECTORY**, and load the dataset's **feed** folder. Connect the camera. No mount is required.
2. Use ASTAP, 1000 mm focal length, 3.76 micrometer pixels, and the manifest's site. Disable automatic RA rotation and streaming in the plugin for this manual scene replay.
3. Keep the controller on **Reference 1**, then capture the first reference in the plugin.
4. Select **Rotate RA 90 degrees**, then capture the second reference.
5. Start live adjustment. Select successive bolt-correction scenes and watch the reported error decrease. The final scene stays selected until changed; it does not wrap to the first reference.
6. Stop before resetting or changing the selected reference scene. Return the controller to Reference 1 before starting another calibration.

This FITS feed uses live timestamps for a scene rendered at recorded times. It demonstrates the real interaction and solving path; small time-dependent differences from the offline expected errors are normal. Use the timestamped XISF replay for numerical regression. Stability, automated mount rotation, and independent drift verification are separate tests.

## Built-in Sky Survey mode

N.I.N.A.'s **SKYSURVEY** camera mode is useful with a connected telescope simulator: it requests a real survey image at the telescope pointing and offers RA/Dec error and Alt/Az shift controls. The source implementation produces a square 2500 × 2500 image and overwrites the profile's pixel size and focal length from the requested survey field. Its FoV control therefore does not reproduce the QHY600 rectangular sensor at native resolution. Use a separate profile when experimenting with it. Its fixed pointing shifts also do not by themselves establish a known physical RA-axis calibration error.

## Sources

- [QHYCCD specifications](https://www.qhyccd.cn/product-specification/).
- [ESA Gaia DR3 documentation](https://gea.esac.esa.int/archive/documentation/GDR3/) and the ESA archive TAP service.
- [Hipparcos and Tycho catalogs, ESA 1997](https://cdsarc.cds.unistra.fr/viz-bin/cat/I/239), queried through CDS VizieR.
- [N.I.N.A. Version-3.2 simulator source](https://github.com/isbeorn/nina/blob/Version-3.2/NINA.WPF.Base/Model/Equipment/MyCamera/Simulator/SimulatorCamera.cs).
