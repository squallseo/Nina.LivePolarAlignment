using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Model.Equipment.MyCamera.Simulator;
using Nina.LivePolarAlignment.Capture;

if (args.Length is < 2 or > 3) {
    Console.Error.WriteLine("Usage: Simulator <dataset-folder> <astap.exe> [new-profile-file]");
    return 2;
}

var dataset = Path.GetFullPath(args[0]);
var astap = Path.GetFullPath(args[1]);
var profileOutput = args.Length == 3 ? Path.GetFullPath(args[2]) : null;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataset, "manifest.json")));
var spec = manifest.RootElement;
var runtime = Path.Combine(dataset, "runtime");
Directory.CreateDirectory(runtime);
Environment.SetEnvironmentVariable("LOCALAPPDATA", runtime);
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var profile = new Profile("QHY600 1000mm Polar Simulation") { Id = Guid.NewGuid() };
profile.AstrometrySettings.Latitude = spec.GetProperty("latitude").GetDouble();
profile.AstrometrySettings.Longitude = spec.GetProperty("longitude").GetDouble();
profile.AstrometrySettings.Elevation = 0;
profile.CameraSettings.PixelSize = spec.GetProperty("pixelSizeUm").GetDouble();
profile.CameraSettings.BitDepth = 16;
profile.CameraSettings.Id = "4C0BBF74-0D95-41F6-AAD8-D6D58668CF2C";
profile.TelescopeSettings.Id = "No_Device";
profile.TelescopeSettings.FocalLength = 1000;
profile.TelescopeSettings.FocalRatio = 5;
profile.TelescopeSettings.Name = "QHY600 full frame / 1000mm f/5 simulation";
profile.PlateSolveSettings.ASTAPLocation = astap;
profile.PlateSolveSettings.PlateSolverType = PlateSolverEnum.ASTAP;
profile.PlateSolveSettings.BlindSolverType = BlindSolverEnum.ASTAP;
profile.PlateSolveSettings.DownSampleFactor = 0;
profile.PlateSolveSettings.Binning = 1;
profile.PlateSolveSettings.MaxObjects = 500;
profile.PlateSolveSettings.ExposureTime = 1;
profile.ImageFileSettings.FITSUseLegacyWriter = true;
profile.ApplicationSettings.Culture = "en-US";
var simulatorId = Guid.Parse("65CA9FD7-8984-4609-B206-86F3F9E8C19D");
profile.PluginSettings.SetValue(simulatorId, "Type", "DIRECTORY");
profile.PluginSettings.SetValue(simulatorId, "DirectorySettings", Path.Combine(dataset, "camera"));
profile.PluginSettings.SetValue(simulatorId, "IsBayered", false);

var profiles = Stub.Create<IProfileService>((method, _) => method.Name == "get_ActiveProfile" ? profile : null);
var detection = Stub.Create<IStarDetection>((method, _) => method.Name == "CreateAnalysis" ? new StarDetectionAnalysis() : null);
var detectionSelector = Stub.Create<IPluggableBehaviorSelector<IStarDetection>>((_, _) => detection);
var annotationSelector = Stub.Create<IPluggableBehaviorSelector<IStarAnnotator>>((_, _) => null);
var imageFactory = new ImageDataFactory(profiles, detectionSelector, annotationSelector);
var exposureFactory = new ExposureDataFactory(imageFactory, profiles, detectionSelector, annotationSelector);
var telescope = Stub.Create<ITelescopeMediator>((_, _) => null);
var camera = new SimulatorCamera(profiles, telescope, exposureFactory, imageFactory);
var cameraMediator = Stub.Create<ICameraMediator>((method, _) => method.Name switch {
    "GetInfo" => new NINA.Equipment.Equipment.MyCamera.CameraInfo { Connected = true, ExposureMin = .01, ExposureMax = 3600 },
    "IsFreeToCapture" => true, _ => null
});
var pointing = Stub.Create<ITelescopeMediator>((method, _) => method.Name switch {
    "GetInfo" => new NINA.Equipment.Equipment.MyTelescope.TelescopeInfo { Connected = true },
    "GetCurrentPosition" => new Coordinates(0, 89.5, Epoch.J2000, Coordinates.RAType.Degrees), _ => null
});
var imaging = Stub.Create<IImagingMediator>((method, parameters) => method.Name == "CaptureImage"
    ? camera.DownloadExposure((CancellationToken)parameters![1]!)
    : throw new InvalidOperationException("Replay must not wait for full NINA image preparation: " + method.Name));
var capture = new NinaPolarAlignmentCaptureService(profiles, cameraMediator, imaging, pointing);
int previews = 0;
capture.PreviewAvailable += preview => {
    if (!preview.IsFrozen || preview.PixelWidth > 1600 || preview.PixelHeight > 1200) throw new InvalidOperationException("Invalid early preview");
    previews++; Console.WriteLine($"Production preview before ASTAP: {preview.PixelWidth}x{preview.PixelHeight}");
};
var session = new PolarAlignmentSession(profile.AstrometrySettings.Latitude, profile.AstrometrySettings.Longitude);
var results = new List<object>();
try {
    if (!await camera.Connect(CancellationToken.None)) throw new InvalidOperationException("Simulator camera connection failed");
    var index = 0;
    foreach (var expected in spec.GetProperty("frames").EnumerateArray()) {
        int previousPreviews = previews;
        var frame = await capture.CaptureAndSolveAsync(1, CancellationToken.None);
        if (previews != previousPreviews + 1 || frame.Preview?.PixelWidth != frame.Width || frame.Preview?.PixelHeight != frame.Height)
            throw new InvalidOperationException("Production pipeline must publish an early preview and return a full-resolution display");
        var expectedTime = expected.GetProperty("capturedAtUtc").GetDateTimeOffset();
        if (Math.Abs((frame.CapturedAtUtc - expectedTime).TotalSeconds) > .01)
            throw new InvalidOperationException("XISF exposure midpoint was not preserved or directory order changed");
        frame = frame with { Dut1Seconds = 0 };
        if (index++ < 2) {
            session.AddCalibration(frame);
            Console.WriteLine($"Reference {index}: solved; rotation={session.RotationDegrees:0.000} deg");
        } else {
            var error = session.Measure(frame);
            var expectedTotal = expected.GetProperty("expectedTotalArcminutes").GetDouble();
            results.Add(new { name = expected.GetProperty("name").GetString(), error.TotalArcminutes,
                error.AltitudeArcminutes, error.AzimuthArcminutes, expectedTotalArcminutes = expectedTotal });
            Console.WriteLine($"{expected.GetProperty("name").GetString()}: actual={error.TotalArcminutes:0.000} arcmin, expected={expectedTotal:0.000}");
            if (Math.Abs(error.TotalArcminutes - expectedTotal) > .15)
                throw new InvalidOperationException("Recovered axis differs from the independent renderer by more than 9 arcseconds");
        }
        GC.Collect();
    }
} finally { camera.Disconnect(); }

File.WriteAllText(Path.Combine(dataset, "replay-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
if (profileOutput != null) {
    // The interactive feed contains one replaceable FITS frame; it never advances unexpectedly.
    profile.PluginSettings.SetValue(simulatorId, "DirectorySettings", Path.Combine(dataset, "feed"));
    profile.Id = Guid.Parse(Path.GetFileNameWithoutExtension(profileOutput));
    Directory.CreateDirectory(Path.GetDirectoryName(profileOutput)!);
    using var stream = new FileStream(profileOutput, FileMode.CreateNew);
    new DataContractSerializer(typeof(Profile)).WriteObject(stream, profile);
    Console.WriteLine("New isolated profile: " + profileOutput);
}
Console.WriteLine("Verified real SimulatorCamera -> production capture/early-preview pipeline -> N.I.N.A. ASTAP solver -> PolarAlignmentSession.");
return 0;

public class Stub : DispatchProxy {
    private Func<MethodInfo, object?[]?, object?> handler = null!;
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class {
        var proxy = Create<T, Stub>();
        ((Stub)(object)proxy).handler = handler;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) {
        var result = handler(method!, args);
        return result ?? (method!.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType) : null);
    }
}
