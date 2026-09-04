using System.Text.Json;
using StickyMD.Core.Notes;

namespace StickyMD.Core.Persistence;

/// <summary>
/// Loads and saves notes.json. Load never throws: an unusable file is moved
/// aside and an empty index returned, and an unusable ENTRY costs that one
/// note's geometry rather than the whole file's.
/// </summary>
/// <remarks>
/// Entries are deserialised one at a time on purpose. A single unknown enum
/// string anywhere in the file used to throw JsonException from the top-level
/// Deserialize, which sent the entire index to notes.json.corrupt-N and lost
/// every note's position -- a hand edit to one note taking out the desktop.
///
/// Keys are re-keyed to NotePath canonical form, because a hand-written or
/// older file may hold a non-canonical path, and the window manager looks
/// entries up by what NoteRepository and NoteWatcher produce.
/// </remarks>
public sealed class NoteIndexStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public string? LastCorruptBackupPath { get; private set; }

    /// <summary>
    /// Corrections made by the most recent <see cref="Load"/>. Empty after a
    /// clean load. The bootstrapper writes these to the diagnostics log; Plan C
    /// also surfaces them in a tray balloon.
    /// </summary>
    public IReadOnlyList<ValidationIssue> LastLoadIssues { get; private set; } = [];

    /// <param name="defaults">
    /// Supplies the fallback colour, size, and opacity for entries that carry
    /// unusable ones. Pass the loaded AppSettings, or <c>new()</c>.
    /// </param>
    /// <param name="nowUtc">
    /// Used to pull future lastOpenedUtc values back. Injected so the behaviour
    /// is testable without waiting for a clock.
    /// </param>
    public NoteIndex Load(AppSettings? defaults = null, DateTime? nowUtc = null)
    {
        LastCorruptBackupPath = null;
        var issues = new List<ValidationIssue>();
        LastLoadIssues = issues;

        var effectiveDefaults = defaults ?? new AppSettings();
        var now = nowUtc ?? DateTime.UtcNow;

        if (!File.Exists(FilePath)) return new NoteIndex();

        var raw = JsonFile.TryRead<RawIndex>(FilePath);

        if (raw is null)
        {
            LastCorruptBackupPath = JsonFile.BackupCorrupt(FilePath, "corrupt");
            return new NoteIndex();
        }

        if (raw.Version != NoteIndex.CurrentVersion)
        {
            // Not corrupt -- just a schema this build does not understand. Never
            // half-parse it, but do not label the user's file as damaged either.
            var direction = raw.Version > NoteIndex.CurrentVersion ? "newer" : "older";
            LastCorruptBackupPath =
                JsonFile.BackupCorrupt(FilePath, $"v{raw.Version}-{direction}");
            return new NoteIndex();
        }

        var index = new NoteIndex { Version = raw.Version };

        foreach (var entry in raw.Notes ?? [])
        {
            if (!NotePath.TryCanonical(entry.Key, out var key))
            {
                issues.Add(new ValidationIssue(
                    entry.Key ?? "(empty key)", "key",
                    "is not a usable path; the entry was dropped."));
                continue;
            }

            NoteState? state;

            try
            {
                state = entry.Value.Deserialize<NoteState>(JsonFile.Options);
            }
            catch (JsonException ex)
            {
                // Per-entry: an unknown enum string or a wrong-typed field here
                // loses ONE note's geometry, not the file.
                issues.Add(new ValidationIssue(key, "(entry)",
                    $"could not be read ({ex.Message.Split('.')[0]}); the entry was dropped."));
                continue;
            }

            if (state is null)
            {
                issues.Add(new ValidationIssue(key, "(entry)",
                    "was null; the entry was dropped."));
                continue;
            }

            var validated = StateValidator.ValidateNote(
                state, effectiveDefaults, key, now, issues);

            var existingKey = index.Notes.Keys
                .FirstOrDefault(k => NotePath.Comparer.Equals(k, key));

            if (existingKey is not null)
            {
                var existing = index.Notes[existingKey];

                // Two keys differing only in case. Document order guarantees
                // nothing, so it cannot decide this -- not even as a
                // tie-break: the newest lastOpenedUtc wins, and when THAT is
                // also equal, the ordinally smaller key string wins. Both
                // rules are properties of the two entries themselves, never
                // of where either happened to sit in the file.
                var newWins = validated.LastOpenedUtc != existing.LastOpenedUtc
                    ? validated.LastOpenedUtc > existing.LastOpenedUtc
                    : string.CompareOrdinal(key, existingKey) < 0;

                var winnerKey = newWins ? key : existingKey;
                var loserKey = newWins ? existingKey : key;

                issues.Add(new ValidationIssue(key, "key",
                    $"appeared twice under different casing ('{existingKey}' and '{key}'); kept '{winnerKey}', discarded '{loserKey}'."));

                if (!newWins) continue;

                // The winning spelling must actually be the one stored: the
                // dictionary's case-insensitive comparer would otherwise keep
                // whichever casing was inserted first regardless of which
                // value won.
                if (!string.Equals(existingKey, key, StringComparison.Ordinal))
                    index.Notes.Remove(existingKey);
            }

            index.Notes[key] = validated;
        }

        return index;
    }

    public void Save(NoteIndex index) => JsonFile.Write(FilePath, index);

    /// <summary>
    /// The on-disk shape, with note entries left as raw JSON so each can be
    /// deserialised -- and fail -- on its own.
    /// </summary>
    private sealed class RawIndex
    {
        public int Version { get; set; } = NoteIndex.CurrentVersion;

        public Dictionary<string, JsonElement>? Notes { get; set; }
    }
}
