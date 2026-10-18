# Standalone UI preview

This runs the production WPF view and workflow with an explicitly synthetic capture
service. It never connects a camera or mount. The NINA profile/database context is
isolated in a process-specific temporary directory.

Requires Windows, .NET 8 Desktop Runtime and an installed NINA distribution for
the native SOFA/NOVAS libraries. No NINA or SharpCap binaries are included here.
Use `-p:NinaInstallDirectory="C:\path\to\NINA"` for a different installation path.

```powershell
dotnet run --project tools/Nina.LivePolarAlignment.Preview -c Release
```

The sample opens maximized in the adjustment stage, with the image on the left and
controls on the right. The divider resizes the panels; narrow windows stack them.
Its top buttons switch between the first
reference, RA rotation and adjustment stages. The production buttons also work;
stop/reset marks old guidance stale. Sample exposure/processing times are artificial.

To verify workflow states and render narrow, 1280횞800 and 1920횞1080 layouts:

```powershell
dotnet run --project tools/Nina.LivePolarAlignment.Preview -c Release -- --render-to analysis/ui-polished
```

This checks all three stages, the image area and control positions, live publication and cancellation/stale behavior,
and writes PNGs plus `preview-verification.txt`. It does not test actual camera
capture, external plate solving or accuracy of a physical mount.

The preview loads NINA's actual Persian color schema and WPF control dictionaries. The plugin inherits host theme brushes and fonts. Focus follows the celestial pole and measured RA axis without changing the astrometric projection; switch it off to fit the complete frame. The synthetic camera uses 6 arcseconds/pixel. Common labels use NINA's locale resources. Plugin instructions follow the active profile language, with Korean translations and English fallback. The render checks cover English, Korean, and an untranslated locale.
