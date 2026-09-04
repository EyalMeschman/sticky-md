using System.Runtime.InteropServices;
using StickyMD.Core.Notes;

namespace StickyMD.Core.Persistence;

/// <summary>
/// An unsaved buffer plus enough metadata to identify where it came from.
/// Self-describing because the snapshot's filename is a one-way hash.
/// </summary>
public sealed record RecoveryEnvelope(
    string OriginalPath,
    string Content,
    DateTime CreatedUtc,
    string LastKnownDiskHash);

/// <summary>
/// Holds text that could not be written to its note. One snapshot per note,
/// cleared as soon as a real save succeeds.
/// </summary>
public sealed class RecoveryStore(string directory)
{
    public string Directory { get; } = directory;

    public static string FileNameFor(string notePath)
    {
        // Lowercased on purpose, and it does NOT violate NotePath's
        // preserve-case rule: this is a HASH KEY and is never shown to anyone.
        // Note identity is case-insensitive, and a hash cannot be made
        // case-insensitive after the fact -- so the folding has to happen
        // before hashing, or "Standup.md" and "standup.md" would get two
        // snapshots for one note.
        var normalized = NotePath.Canonical(notePath).ToLowerInvariant();

        // Hash the raw UTF-16 code units rather than encoding to UTF-8 first. Any
        // encoder must decide what to do with an unpaired surrogate: a lenient one
        // substitutes U+FFFD, so two distinct paths would hash identically and one
        // note's snapshot would overwrite another's; a strict one throws, which
        // would make this last-resort write fail outright. Marshalling the chars
        // directly can do neither.
        var bytes = MemoryMarshal.AsBytes(normalized.AsSpan()).ToArray();

        return NoteFile.Sha256(bytes) + ".json";
    }

    public void Save(RecoveryEnvelope envelope)
        => JsonFile.Write(PathFor(envelope.OriginalPath), envelope);

    public RecoveryEnvelope? TryLoad(string notePath)
        => JsonFile.TryRead<RecoveryEnvelope>(PathFor(notePath));

    /// <summary>
    /// Best-effort deletion. Returns false if the snapshot could not be removed --
    /// the caller must treat that as "a stale snapshot may still be offered later"
    /// and should compare <see cref="RecoveryEnvelope.LastKnownDiskHash"/> against
    /// the note's current on-disk hash before surfacing it to the user, or it may
    /// offer older text over a newer successful save.
    /// </summary>
    public bool Clear(string notePath)
    {
        var path = PathFor(notePath);

        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public IReadOnlyList<RecoveryEnvelope> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var envelopes = new List<RecoveryEnvelope>();

        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                var envelope = JsonFile.TryRead<RecoveryEnvelope>(file);

                // A well-formed but wrong-shape file deserialises to an all-default
                // envelope. Without OriginalPath the snapshot is unattributable -- the
                // filename is a one-way hash -- so it is worse than useless.
                if (envelope is null
                    || string.IsNullOrEmpty(envelope.OriginalPath)
                    || envelope.Content is null)
                {
                    continue;
                }

                envelopes.Add(envelope);
            }
        }
        catch (IOException)
        {
            // The directory vanished mid-enumeration. Return what we gathered --
            // losing some snapshots beats throwing out of the recovery path.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return envelopes;
    }

    private string PathFor(string notePath)
        => Path.Combine(Directory, FileNameFor(notePath));
}
