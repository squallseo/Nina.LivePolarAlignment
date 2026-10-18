using System.Text.Json;
using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Core.Tests;

public sealed class FieldJournalTests {
    private static string Root() => Path.Combine(Path.GetTempPath(), "NinaPolarJournalTests", Guid.NewGuid().ToString("N"));
    [Fact]
    public void CompletedLinesAreReadableBeforeClosingAndPartialTailIsRecoverable() {
        using var journal = new FieldJournal(Root(), new { synthetic = true });
        journal.WriteEvent("note", new { text = "comma, quote\" and newline\n" });
        var entries = FieldJournal.Read(journal.DirectoryPath, out var incomplete);
        Assert.False(incomplete); Assert.Single(entries);
        Assert.Equal("comma, quote\" and newline\n", entries[0].Data.GetProperty("text").GetString());
        journal.Dispose();
        File.AppendAllText(Path.Combine(journal.DirectoryPath, "events.jsonl"), "{unfinished");
        Assert.Single(FieldJournal.Read(journal.DirectoryPath, out incomplete)); Assert.True(incomplete);
    }
    [Fact]
    public void CorruptionInMiddleIsNotSilentlyIgnored() {
        using var journal = new FieldJournal(Root(), new { synthetic = true });
        journal.WriteEvent("note", new { }); journal.Dispose();
        File.AppendAllText(Path.Combine(journal.DirectoryPath, "events.jsonl"), "{invalid\n{}\n");
        Assert.Throws<JsonException>(() => FieldJournal.Read(journal.DirectoryPath, out _));
    }
    [Fact]
    public void StabilityUsesSampleStandardDeviationAndReportsItsFrameRange() {
        var window = new StabilityWindow(3); var time = DateTimeOffset.UtcNow;
        window.Add(10, time, 1, 10); window.Add(12, time.AddSeconds(1), 2, 12);
        Assert.Throws<InvalidOperationException>(() => window.Result());
        window.Add(15, time.AddSeconds(2), 3, 14);
        var result = window.Result();
        Assert.Equal(1, result.SigmaAltitudeArcseconds, 10); Assert.Equal(2, result.SigmaAzimuthArcseconds, 10);
        Assert.Equal(2, result.MeanAltitudeArcseconds); Assert.Equal(12, result.MeanAzimuthArcseconds);
        Assert.Equal(2, result.RangeAltitudeArcseconds); Assert.Equal(4, result.RangeAzimuthArcseconds);
        Assert.Equal(10, result.FirstFrameId); Assert.Equal(15, result.LastFrameId);
    }
    [Fact]
    public void StabilityRejectsDuplicateExposuresAndNonfiniteOffsets() {
        var window = new StabilityWindow(); var time = DateTimeOffset.UtcNow;
        window.Add(1, time, 0, 0);
        Assert.Throws<ArgumentException>(() => window.Add(2, time, 0, 0));
        Assert.Throws<ArgumentException>(() => window.Add(1, time.AddSeconds(1), 0, 0));
        Assert.Throws<ArgumentException>(() => window.Add(2, time.AddSeconds(1), double.NaN, 0));
    }
}
