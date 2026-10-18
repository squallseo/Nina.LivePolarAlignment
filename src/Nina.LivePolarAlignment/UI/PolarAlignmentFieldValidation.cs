using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Enum;
using NINA.Image.FileFormat;
using Nina.LivePolarAlignment.Capture;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private FieldJournal? fieldJournal;
    private string? fieldDirectory, fieldFailure;
    private bool recordFieldSession, fieldSaving, fieldDisposed;
    private SolvedPolarFrame? lastFieldFrame;
    private long lastFieldFrameId;
    private long stabilitySequence;
    private StabilityWindow? stability;
    private StabilityResult? stabilityResult;
    public bool RecordFieldSession {
        get => recordFieldSession;
        set {
            if (!CanConfigureField || recordFieldSession == value) return;
            recordFieldSession = value; fieldFailure = null;
            if (!captureService.IsSynthetic) profiles.ActiveProfile.PluginSettings.SetValue(Plugin.SettingsId, nameof(Plugin.EnableDebugRecording), value);
            if (!value) EndFieldSession("recording_disabled");
            RaisePropertyChanged(); NotifyField();
        }
    }
    public bool CanConfigureField => !liveBusy && !fieldSaving && calibrationSamples == 0;
    public string FieldNote { get; set; } = string.Empty;
    public string? FieldSessionDirectory => fieldDirectory;
    public bool HasFieldFailure => fieldFailure != null;
    public string FieldRecordingStatus => fieldFailure != null ? UiText.Format("FieldRecordingFailure", fieldFailure)
        : fieldJournal != null ? UiText.Format("FieldRecordingCount", fieldJournal.FrameCount)
        : UiText.Get(recordFieldSession ? "FieldRecordingReady" : "FieldRecordingOff");
    public bool HasStabilityResult => stabilityResult != null;
    public string StabilityStatus => stabilityResult is { } result
        ? UiText.Format("FieldScatter", result.Count, result.SigmaAltitudeArcseconds, result.SigmaAzimuthArcseconds)
        : stability is { } window ? UiText.Format("FieldCollecting", window.Count, window.RequiredSamples)
        : UiText.Get("FieldStabilityHint");
    public ICommand MarkFieldSampleCommand { get; private set; } = null!;
    public ICommand CheckStabilityCommand { get; private set; } = null!;
    public ICommand OpenFieldFolderCommand { get; private set; } = null!;
    private bool CanMarkField => fieldJournal != null && fieldFailure == null && !fieldSaving && HasLiveMeasurement && !IsMeasurementStale && lastFieldFrame != null;

    private void InitializeField() {
        recordFieldSession = captureService.IsSynthetic || Plugin.ReadDebugRecording(profiles);
        MarkFieldSampleCommand = new AsyncRelayCommand(MarkFieldSampleAsync, () => CanMarkField);
        CheckStabilityCommand = new RelayCommand(StartStability, () => HasLiveMeasurement && !IsMeasurementStale && liveBusy && stability == null);
        OpenFieldFolderCommand = new RelayCommand(() => {
            if (fieldDirectory != null) {
                try { Process.Start(new ProcessStartInfo(fieldDirectory) { UseShellExecute = true }); }
                catch (Exception error) { fieldFailure = error.Message; NotifyField(); }
            }
        }, () => fieldDirectory != null && Directory.Exists(fieldDirectory));
    }
    private void EnsureFieldSession() {
        if (fieldJournal == null && !captureService.IsSynthetic) recordFieldSession = Plugin.ReadDebugRecording(profiles);
        if (!recordFieldSession || fieldJournal != null || fieldFailure != null) return;
        try {
            var profile = profiles.ActiveProfile;
            var root = captureService.IsSynthetic
                ? Path.Combine(Path.GetTempPath(), "NinaPolarFieldSynthetic")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "LivePolarAlignment", "FieldSessions");
            fieldJournal = new FieldJournal(root, new {
                synthetic = captureService.IsSynthetic, purpose = verificationInProgress ? "verification" : "alignment",
                pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
                ninaCoreVersion = typeof(NINA.Core.Utility.CoreUtil).Assembly.GetName().Version?.ToString(),
                profile = captureService.IsSynthetic ? "synthetic" : profile.Name,
                latitudeDegrees = profile.AstrometrySettings.Latitude, longitudeDegrees = profile.AstrometrySettings.Longitude,
                cameraId = profile.CameraSettings?.Id, focalLengthMm = profile.TelescopeSettings?.FocalLength,
                pixelSizeMicrons = profile.CameraSettings?.PixelSize, solveBinning = profile.PlateSolveSettings?.Binning,
                solver = profile.PlateSolveSettings?.PlateSolverType.ToString(), solveGain = profile.PlateSolveSettings?.Gain,
                refractionCorrection = false, distortionModel = "solver linear center / angle / pixel scale only",
                streamingTimestamp = "receive time minus half requested exposure; hardware latency unknown",
                targetArcseconds = 60, note = FieldNote
            });
            fieldDirectory = fieldJournal.DirectoryPath;
        } catch (Exception error) { fieldFailure = error.Message; }
        NotifyField();
    }
    private void RecordFieldEvent(string type, object data) {
        if (fieldJournal == null || fieldFailure != null) return;
        try { fieldJournal.WriteEvent(type, data); }
        catch (Exception error) { fieldFailure = error.Message; NotifyField(); }
    }
    private void RecordFieldFrame(SolvedPolarFrame frame, string kind, bool accepted, double cycleMs = 0, Exception? failure = null) {
        if (fieldJournal == null || fieldFailure != null) return;
        try {
            var error = accepted && kind == "measurement" ? latestError : null;
            var pointGuidance = accepted ? guidance : null;
            var record = new FieldFrameRecord(0, kind, accepted,
                new(frame.RightAscensionDegrees, frame.DeclinationDegrees, frame.PositionAngleDegrees, frame.PixelScaleArcseconds,
                    frame.Width, frame.Height, frame.Flipped, frame.CapturedAtUtc, session?.LastDut1Seconds),
                kind == "reference" ? "still_reference" : UseStreaming && !verificationInProgress ? "native_streaming" : "single_frames",
                ExposureSeconds, cycleMs, (DateTimeOffset.UtcNow - frame.CapturedAtUtc).TotalSeconds,
                session?.RotationDegrees, session?.UsedApproximateEarthRotation ?? false,
                error?.AltitudeArcminutes * 60, error?.AzimuthArcminutes * 60, error?.TotalArcminutes * 60,
                pointGuidance?.MountAxis?.X, pointGuidance?.MountAxis?.Y,
                pointGuidance?.CelestialPole?.X, pointGuidance?.CelestialPole?.Y, failure?.Message);
            var id = fieldJournal.WriteFrame(record);
            if (accepted) { lastFieldFrame = frame; lastFieldFrameId = id; }
        } catch (Exception error) { fieldFailure = error.Message; }
        NotifyField();
    }
    private void StartStability() {
        stability = new StabilityWindow(); stabilityResult = null;
        RecordFieldEvent("stability_start", new { samples = 20, note = FieldNote, assumption = "Operator holds mount still; scatter is not absolute accuracy." });
        NotifyField();
    }
    private void UpdateStability(SolvedPolarFrame frame) {
        if (stability is not { } window || latestError is not { } offset) return;
        if (IsMeasurementStale) { AbortStability("stale frame"); return; }
        var id = fieldJournal != null && fieldFailure == null ? lastFieldFrameId : Math.Max(stabilitySequence + 1, lastFieldFrameId + 1);
        stabilitySequence = id;
        try { window.Add(id, frame.CapturedAtUtc, offset.AltitudeArcminutes * 60, offset.AzimuthArcminutes * 60); }
        catch (ArgumentException) { AbortStability("duplicate or out-of-order exposure"); return; }
        if (window.Complete) {
            stabilityResult = window.Result(); stability = null;
            RecordFieldEvent("stability_result", stabilityResult);
        }
        NotifyField();
    }
    private async Task SaveFieldImagesAsync(FieldJournal journal, SolvedPolarFrame frame, long frameId, string kind, string note) {
        var marker = journal.WriteEvent("sample_start", new { frameId, kind, note });
        var stem = $"sample-{marker:D6}";
        string? fits = null, png = null;
        try {
            if (frame.RawImage != null) {
                fits = await frame.RawImage.SaveToDisk(new FileSaveInfo {
                    FilePath = journal.DirectoryPath, FilePattern = stem, FileType = FileTypeEnum.FITS,
                    FITSUseLegacyWriter = true
                }, CancellationToken.None, true);
                fits = Path.GetFileName(fits);
            }
            if (frame.Preview is { IsFrozen: true } preview) {
                png = stem + "-preview.png";
                await Task.Run(() => {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
                    using var output = File.Create(Path.Combine(journal.DirectoryPath, png)); encoder.Save(output);
                });
            }
            journal.WriteEvent("sample_saved", new { frameId, kind, note, fits, previewPng = png,
                previewIsProcessed = true, rawAvailable = fits != null });
        } catch (Exception error) {
            journal.WriteEvent("sample_failed", new { frameId, kind, note, fits, previewPng = png, error = error.Message });
            throw;
        }
    }
    private async Task SaveReferenceEvidenceAsync(SolvedPolarFrame frame) {
        if (fieldJournal == null || fieldFailure != null) return;
        try { await SaveFieldImagesAsync(fieldJournal, frame, lastFieldFrameId, "reference", FieldNote); }
        catch (Exception error) { fieldFailure = error.Message; NotifyField(); }
    }
    private async Task MarkFieldSampleAsync() {
        if (!CanMarkField) return;
        var journal = fieldJournal!; var frame = lastFieldFrame!; var id = lastFieldFrameId; var note = FieldNote;
        fieldSaving = true; NotifyField();
        try { await SaveFieldImagesAsync(journal, frame, id, "mark", note); }
        catch (Exception error) { fieldFailure = error.Message; }
        finally { fieldSaving = false; NotifyField(); if (fieldDisposed && !liveBusy) EndFieldSession("disposed"); }
    }
    private void AbortStability(string reason) {
        if (stability == null) return;
        RecordFieldEvent("stability_aborted", new { reason, samples = stability.Count }); stability = null;
        NotifyField();
    }
    private void EndFieldSession(string reason) {
        AbortStability(reason);
        RecordFieldEvent("session_end", new { reason });
        try { fieldJournal?.Dispose(); } catch (Exception error) { fieldFailure = error.Message; }
        fieldJournal = null; lastFieldFrame = null;
    }
    private void NotifyField() {
        NotifyVerification();
        foreach (var property in new[] { nameof(HasStabilityResult), nameof(CanConfigureField), nameof(FieldRecordingStatus), nameof(StabilityStatus), nameof(HasFieldFailure), nameof(FieldSessionDirectory) }) RaisePropertyChanged(property);
        (MarkFieldSampleCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (CheckStabilityCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (OpenFieldFolderCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (ResetLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
    }
}
