namespace StickyMD.Core.Notes;

/// <summary>
/// Debounced external-change notifications for the .md files in one directory.
/// Suppresses StickyMD's own writes via <see cref="IWriteLedger"/>.
/// </summary>
public sealed class NoteWatcher : IDisposable
{
    private const int TickMs = 25;

    private readonly string _directory;
    private readonly IWriteLedger _ledger;
    private readonly int _debounceMs;
    private readonly Dictionary<string, long> _pending = NotePath.NewMap<long>();
    private readonly object _gate = new();
    private readonly Timer _flushTimer;

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public event Action<string>? ExternalChanged;
    public event Action<string>? Deleted;
    public event Action<string, string>? Renamed;

    /// <summary>
    /// Fired after the underlying watcher was lost and recreated. Consumers
    /// should re-read whatever they currently have open -- events may have been
    /// dropped while the watcher was down.
    /// </summary>
    public event Action<Exception>? Recovered;

    public NoteWatcher(string directory, IWriteLedger ledger, int debounceMs = 150)
    {
        _directory = directory;
        _ledger = ledger;
        _debounceMs = debounceMs;

        _flushTimer = new Timer(_ => Flush(), null, TickMs, TickMs);
        TryStartWatcher();
    }

    private void TryStartWatcher()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;

            var watcher = new FileSystemWatcher(_directory, "*.md")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.FileName
                    | NotifyFilters.Size,
            };

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
        }
        catch (ArgumentException)
        {
            // Invalid path. Nothing to watch; the app surfaces this elsewhere.
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Cheap early-out; see the note in OnRenamed about what this does and
        // does not guarantee.
        if (_disposed) return;

        try { Enqueue(e.FullPath); }
        catch (ArgumentException) { /* Unusable path; skip this one event. */ }
        catch (NotSupportedException) { /* Ditto -- e.g. a stray colon. */ }
        catch (IOException) { /* Covers PathTooLongException. */ }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Cheap early-out, not a guarantee. Unlike OnChanged -- whose enqueued
        // work is filtered later by Flush under the lock -- this fires Renamed
        // directly, so a stale read here can let exactly ONE Renamed through
        // after Dispose has returned. That window is accepted: closing it would
        // mean invoking a subscriber callback while holding _gate, which risks
        // deadlocking against FileSystemWatcher.Dispose waiting on in-flight
        // callbacks. Subscribers must tolerate a late Renamed.
        if (_disposed) return;

        try
        {
            // An atomic save is not a rename. File.Replace surfaces as TWO rename
            // events -- "a.md -> a.md~RF….TMP" (the OS backup step) and
            // "a.md.stickymd-tmp -> a.md" -- and reporting either as Renamed would
            // have consumers re-key their index to a path that does not exist.
            // Only the second names a real note, so route it through Enqueue: that
            // is the path that consults the write ledger, which is what makes
            // self-write suppression work for the app's own saves at all.
            var oldIsNote = IsNotePath(e.OldFullPath);
            var newIsNote = IsNotePath(e.FullPath);

            // Both the OS backup step ("a.md -> a.md~RF….TMP", mid-File.Replace)
            // and a genuine rename out of the watched set ("a.md -> b.txt") arrive
            // with newIsNote == false. Telling them apart by NAME is fragile, so
            // let the debounce do it: Dispatch re-checks File.Exists ~150ms later,
            // by which point the backup step has completed and a.md is BACK (so the
            // ledger suppresses it), while a real rename has left a.md GONE (so
            // Deleted fires). One enqueue, both cases correct.
            if (!newIsNote)
            {
                Enqueue(e.OldFullPath);
                return;
            }

            if (!oldIsNote) { Enqueue(e.FullPath); return; }   // tmp -> a.md : our own save

            if (!NotePath.TryCanonical(e.OldFullPath, out var oldCanonical)) return;
            if (!NotePath.TryCanonical(e.FullPath, out var newCanonical)) return;

            Renamed?.Invoke(oldCanonical, newCanonical);
        }
        catch (ArgumentException) { /* Unusable path; skip this one event. */ }
        catch (NotSupportedException) { /* Ditto -- e.g. a stray colon. */ }
        catch (IOException) { /* Covers PathTooLongException. */ }
    }

    /// <summary>
    /// True for a path naming a real note. The FileSystemWatcher filter is "*.md",
    /// which also matches our own "a.md.stickymd-tmp" and the OS's "a.md~RF….TMP"
    /// backup -- neither of which ends in ".md", so this single test excludes both.
    /// </summary>
    private static bool IsNotePath(string path)
        => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    private void DetachAndDispose(FileSystemWatcher? watcher)
    {
        if (watcher is null) return;

        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnChanged;
        watcher.Created -= OnChanged;
        watcher.Deleted -= OnChanged;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnError;
        watcher.Dispose();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // FileSystemWatcher drops events when its internal buffer overflows.
        // A watcher must never be allowed to silently stop watching.
        FileSystemWatcher? old;

        lock (_gate)
        {
            if (_disposed) return;
            old = _watcher;
            _watcher = null;
        }

        DetachAndDispose(old);

        lock (_gate)
        {
            // Dispose may have completed while we were tearing the old watcher
            // down. Re-check under the lock, or we would resurrect a live
            // watcher after Dispose returned and leak its handle.
            if (_disposed) return;

            // A concurrent OnError may have already restarted us. Without this,
            // both calls would create a watcher and the loser would be orphaned
            // still-enabled -- a leaked handle and duplicate events.
            if (_watcher is not null) return;

            TryStartWatcher();
        }

        Recovered?.Invoke(e.GetException());
    }

    private void Enqueue(string fullPath)
    {
        // TryCanonical rather than Canonical: these paths come straight from
        // FileSystemWatcher, and one unusable name must cost one event, never
        // the watcher.
        if (!NotePath.TryCanonical(fullPath, out var canonical)) return;

        lock (_gate) _pending[canonical] = Environment.TickCount64 + _debounceMs;
    }

    private void Flush()
    {
        List<string> due;

        lock (_gate)
        {
            if (_pending.Count == 0) return;

            var now = Environment.TickCount64;
            due = _pending.Where(p => p.Value <= now).Select(p => p.Key).ToList();
            foreach (var path in due) _pending.Remove(path);
        }

        foreach (var path in due) Dispatch(path);
    }

    private void Dispatch(string path)
    {
        if (_disposed) return;

        if (!File.Exists(path))
        {
            Deleted?.Invoke(path);
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (_ledger.IsOwnWrite(path, bytes.LongLength, NoteFile.Sha256(bytes)))
                return;

            ExternalChanged?.Invoke(path);
        }
        catch (IOException)
        {
            // Still being written. Re-arm so the next tick tries again.
            Enqueue(path);
        }
        catch (UnauthorizedAccessException)
        {
            Enqueue(path);
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
        }

        _flushTimer.Dispose();
        DetachAndDispose(watcher);
    }
}
