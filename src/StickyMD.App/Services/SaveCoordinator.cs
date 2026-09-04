using System.IO;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;

namespace StickyMD.App.Services;

public enum SaveStatus { Saved, NothingToDo, Failed }

/// <param name="DiskHash">The saved content's hash, or null when nothing was written.</param>
/// <param name="Attempts">Total write attempts, including the first.</param>
public sealed record SaveOutcome(
    SaveStatus Status, string? DiskHash, string? Message, int Attempts);

/// <summary>
/// Owns one note's unsaved buffer and everything that happens when it is
/// written: the retry schedule, the write ledger, and the recovery snapshot.
/// </summary>
/// <remarks>
/// THE BUFFER NEVER LEAVES MEMORY ON FAILURE. IsDirty stays true and the text
/// stays here, so the amber bar's Retry has something to write and closing the
/// note still has something to snapshot. "Never lose text" is this class's
/// whole job.
///
/// SNAPSHOTS ARE WRITTEN AFTER THE RETRIES ARE EXHAUSTED, not before. A
/// OneDrive lock that clears on attempt two would otherwise leave a stale
/// snapshot that a later startup offers to restore -- older text presented as
/// a recovery.
///
/// AN ENCODING FAILURE IS NOT RETRIED. NoteFile encodes strictly, so a lone
/// surrogate throws before any file is touched and will throw identically
/// three more times. The snapshot is still written: that text cannot reach the
/// file at all, which makes it exactly the case recovery exists for.
///
/// EVERY AWAIT IN THIS CLASS USES ConfigureAwait(false). This class touches no
/// UI, so it has no reason to capture the caller's SynchronizationContext --
/// and NoteWindow.SaveNow() (called from Dispose, during shutdown) depends on
/// that: it blocks synchronously on FlushAsync via GetAwaiter().GetResult(),
/// which would deadlock if any continuation here tried to resume on the UI
/// thread while that same thread is blocked waiting for it.
/// </remarks>
public sealed class SaveCoordinator
{
    private readonly string _notePath;
    private readonly INoteFileGateway _gateway;
    private readonly IWriteLedger _ledger;
    private readonly RecoveryStore _recovery;
    private readonly string _diagnosticsFile;
    private readonly IReadOnlyList<int> _retryBackoffMs;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string _buffer = string.Empty;
    private bool _dirty;

    /// <param name="retryBackoffMs">
    /// Spec §8: 100, 300, 900. Injected so tests exercise the schedule without
    /// waiting 1.3 seconds for it.
    /// </param>
    public SaveCoordinator(
        string notePath,
        INoteFileGateway gateway,
        IWriteLedger ledger,
        RecoveryStore recovery,
        string diagnosticsFile,
        IReadOnlyList<int>? retryBackoffMs = null)
    {
        _notePath = notePath;
        _gateway = gateway;
        _ledger = ledger;
        _recovery = recovery;
        _diagnosticsFile = diagnosticsFile;
        _retryBackoffMs = retryBackoffMs ?? [100, 300, 900];
    }

    /// <summary>
    /// The on-disk format to reproduce. Set from a read; defaults to canonical
    /// for a note StickyMD created.
    /// </summary>
    public NoteFormat Format { get; set; } = NoteFormat.Canonical;

    /// <summary>The last hash successfully written or read.</summary>
    public string? DiskHash { get; private set; }

    public bool IsDirty => _dirty;

    public string Buffer => _buffer;

    public event Action<SaveOutcome>? Saved;

    /// <summary>Adopts content read from disk. Does not mark the buffer dirty.</summary>
    public void AdoptFromDisk(NoteContent content)
    {
        _buffer = content.Text;
        Format = content.Format;
        DiskHash = content.ContentHash;
        _dirty = false;
    }

    public void MarkDirty(string text)
    {
        _buffer = text;
        _dirty = true;
    }

    public async Task<SaveOutcome> FlushAsync()
    {
        // Serialised, because the 500ms debounce timer and an explicit flush
        // on blur or close can land together -- and two concurrent
        // AtomicWrites to one path race over the same temp file.
        //
        // ConfigureAwait(false): this class touches no UI and must not
        // capture a synchronization context -- see the class remarks on why
        // NoteWindow.SaveNow's blocking wait depends on that.
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!_dirty)
                return Report(new SaveOutcome(SaveStatus.NothingToDo, DiskHash, null, 0));

            var text = _buffer;
            var attempts = 0;
            Exception? last = null;

            for (var i = 0; i <= _retryBackoffMs.Count; i++)
            {
                attempts++;

                try
                {
                    var outcome = _gateway.Write(_notePath, text, Format);

                    // Record BEFORE anything else. This is what lets
                    // NoteWatcher tell our own write from an external edit; a
                    // save that is not recorded comes straight back as an
                    // external change and the note reloads itself.
                    _ledger.Record(_notePath, outcome);

                    DiskHash = outcome.ContentHash;

                    // Only clear dirty if the buffer has not moved on. The
                    // user may have typed during the await.
                    if (string.Equals(_buffer, text, StringComparison.Ordinal))
                        _dirty = false;

                    ClearSnapshot();

                    return Report(new SaveOutcome(
                        SaveStatus.Saved, outcome.ContentHash, null, attempts));
                }
                catch (Exception ex) when (IsRetryable(ex))
                {
                    last = ex;

                    if (i < _retryBackoffMs.Count)
                        await Task.Delay(_retryBackoffMs[i]).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsPermanent(ex))
                {
                    // Retrying is three wasted delays before the same answer.
                    last = ex;
                    break;
                }
                catch (Exception ex)
                {
                    // Anything outside the two named sets above (an
                    // ObjectDisposedException, a SecurityException, a bug
                    // surfacing as a NullReferenceException, ...) is treated
                    // as permanent ON PURPOSE. IsRetryable/IsPermanent are a
                    // closed set; letting an unrecognised exception propagate
                    // out of FlushAsync would skip Fail() entirely -- no
                    // snapshot written -- and land as an unhandled exception
                    // on the UI thread, since every caller (_autosave.Tick,
                    // Editor.LostFocus) is an async void handler with no
                    // try/catch. A snapshot plus a reported Failed beats an
                    // unhandled exception that kills the app.
                    last = ex;
                    break;
                }
            }

            return Report(Fail(text, attempts, last));
        }
        finally
        {
            _gate.Release();
        }
    }

    private SaveOutcome Fail(string text, int attempts, Exception? cause)
    {
        // The buffer stays. _dirty stays true. Retry, Save As, and the
        // on-close snapshot all depend on it still being here.
        WriteSnapshot(text);

        var message = cause?.Message ?? "the file could not be written";

        DiagnosticsLog.Write(
            _diagnosticsFile,
            $"{_notePath}: save failed after {attempts} attempt(s) -- {message}");

        return new SaveOutcome(SaveStatus.Failed, DiskHash, message, attempts);
    }

    private void WriteSnapshot(string text)
    {
        try
        {
            _recovery.Save(new RecoveryEnvelope(
                _notePath, text, DateTime.UtcNow, DiskHash ?? string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The buffer is still in memory and still in the editor, so this
            // is a degraded outcome rather than a lost one -- but it means a
            // crash from here would lose the text, which is worth recording.
            DiagnosticsLog.Write(
                _diagnosticsFile,
                $"{_notePath}: could not write a recovery snapshot -- {ex.Message}");
        }
    }

    private void ClearSnapshot()
    {
        if (_recovery.Clear(_notePath)) return;

        // Recorded, not ignored. A snapshot that outlives its successful save
        // would be offered on a later startup and would present OLDER text as
        // a recovery. The startup path compares LastKnownDiskHash for exactly
        // this reason.
        DiagnosticsLog.Write(
            _diagnosticsFile,
            $"{_notePath}: a recovery snapshot could not be removed after a successful save.");
    }

    private SaveOutcome Report(SaveOutcome outcome)
    {
        Saved?.Invoke(outcome);
        return outcome;
    }

    /// <summary>
    /// Transient: a lock, an antivirus scan, a sync client holding the file.
    /// Worth waiting for.
    /// </summary>
    private static bool IsRetryable(Exception ex)
        => ex is IOException or UnauthorizedAccessException;

    /// <summary>
    /// Deterministic: the same buffer will fail the same way every time.
    /// NoteFile encodes strictly, so a lone surrogate throws before any file
    /// is touched.
    /// </summary>
    private static bool IsPermanent(Exception ex)
        => ex is System.Text.EncoderFallbackException
            or ArgumentException
            or NotSupportedException;
}
