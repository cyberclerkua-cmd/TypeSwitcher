using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeSwitcher.Core;

public sealed record CorrectionEntry(
    [property: JsonPropertyName("ts")] string Timestamp,
    [property: JsonPropertyName("ev")] string Event,
    [property: JsonPropertyName("orig")] string Original,
    [property: JsonPropertyName("res")] string? Result,
    [property: JsonPropertyName("kind")] string? Kind);

/// <summary>Bounded JSONL history compatible with the original corrections.jsonl, atomically replaced under one lock.</summary>
public static class CorrectionHistory
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<CorrectionEntry> Read(string path, int limit)
    {
        limit = Math.Clamp(limit, 10, 10000);
        var result = new Queue<CorrectionEntry>();
        if (!File.Exists(path)) return [];
        lock (Gate)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 32768) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<CorrectionEntry>(line, Json);
                    if (entry == null || string.IsNullOrEmpty(entry.Original) || entry.Event is not ("correct" or "reject")) continue;
                    result.Enqueue(entry);
                    if (result.Count > limit) result.Dequeue();
                }
                catch (JsonException) { }
            }
        }
        return result.ToArray();
    }

    public static void Append(string path, CorrectionEntry entry, int limit)
    {
        lock (Gate)
        {
            var entries = Read(path, limit).Append(entry).TakeLast(Math.Clamp(limit, 10, 10000)).ToArray();
            Write(path, entries);
        }
    }

    public static void Clear(string path)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // A zero-length truncate is one filesystem operation and does not require replacing
            // a file that another process may currently be reading.
            using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            file.SetLength(0); file.Flush(true);
        }
    }

    private static void Write(string path, IEnumerable<CorrectionEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                foreach (var entry in entries) writer.WriteLine(JsonSerializer.Serialize(entry, Json));
                writer.Flush(); file.Flush(true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, true); break; }
                catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
                { Thread.Sleep(25); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
