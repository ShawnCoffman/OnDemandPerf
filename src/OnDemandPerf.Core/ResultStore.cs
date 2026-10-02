using System.Text.Json;

namespace OnDemandPerf.Core;

public sealed class ResultStore(string directory, bool compact = false)
{
    public const int MaxHistory = 50;
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnDemandPerf");
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static JsonSerializerOptions CompactOptions { get; } = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Log(string message)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "OnDemandPerf.log");
        if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
            File.Move(path, path + ".1", true);
        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }

    /// <summary>Writes the report pair to history and Latest, prunes history, and returns the history JSON path.</summary>
    public string Save(AnalysisResult result)
    {
        var history = Path.Combine(directory, "Results");
        Directory.CreateDirectory(history);
        var name = $"{result.Timestamp:yyyy-MM-dd_HHmmss_fff}_{result.RunId}";
        var json = JsonSerializer.Serialize(result, compact ? CompactOptions : JsonOptions);
        var report = TextReportWriter.Format(result);
        var historyJson = Path.Combine(history, name + ".json");
        AtomicWrite(historyJson, json);
        AtomicWrite(Path.Combine(history, name + ".txt"), report);
        AtomicWrite(Path.Combine(directory, "Latest.json"), json);
        AtomicWrite(Path.Combine(directory, "Latest.txt"), report);
        foreach (var extension in new[] { "*.json", "*.txt" })
            foreach (var old in new DirectoryInfo(history).GetFiles(extension).OrderByDescending(f => f.Name).Skip(MaxHistory))
                old.Delete();
        return historyJson;
    }

    /// <summary>Loads a saved report into the same typed model the worker wrote.</summary>
    public static AnalysisResult Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AnalysisResult>(stream, JsonOptions)
            ?? throw new InvalidDataException("Report file is empty.");
    }

    private static void AtomicWrite(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
