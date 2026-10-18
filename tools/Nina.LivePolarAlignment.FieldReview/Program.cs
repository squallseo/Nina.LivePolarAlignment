using System.IO;
using System.Text.Json;
using Nina.LivePolarAlignment.Capture;
using Nina.LivePolarAlignment.Core;

if (args.Length is < 1 or > 2) {
    Console.Error.WriteLine("Usage: Nina.LivePolarAlignment.FieldReview <session-folder> [output-folder]"); return 2;
}
try {
    var session = Path.GetFullPath(args[0]);
    var output = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.Combine(session, "review");
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    var report = FieldSessionReview.Review(session, output);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(FieldJournal.Json) { WriteIndented = true }));
    Console.WriteLine($"Review files: {output}");
    return report.Warnings.Any(w => w.Contains("replay failed")) ? 1 : 0;
} catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
