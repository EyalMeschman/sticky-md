using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StickyMD.Core.Notes;

namespace StickyMD.Core.Persistence;

/// <summary>Atomic, corruption-tolerant JSON persistence for app state.</summary>
public static class JsonFile
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Returns null when the file is absent, empty, or unparseable.</summary>
    public static T? TryRead<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(value, Options);
        NoteFile.AtomicWriteBytes(path, new UTF8Encoding(false, true).GetBytes(json));
    }

    /// <summary>
    /// Moves an unusable file aside so the app can start clean. Returns the backup
    /// path, or <c>null</c> if the move failed -- callers must not claim a backup
    /// exists when it does not, since that path is surfaced to the user.
    /// Nothing the user authored is ever destroyed here.
    /// </summary>
    public static string? BackupCorrupt(string path, string label)
    {
        for (var n = 1; ; n++)
        {
            var candidate = $"{path}.{label}-{n}";
            if (File.Exists(candidate)) continue;

            try { File.Move(path, candidate); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            return candidate;
        }
    }
}
