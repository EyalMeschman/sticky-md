using System.IO;
using StickyMD.App.Interop;
using StickyMD.App.Windows;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Geometry;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Services;

/// <summary>
/// Owns every live note window and the index that outlives them.
/// </summary>
/// <remarks>
/// THREE STATES, NOT TWO.
///   isOpen = true   this note belongs on my desktop and returns next startup
///   hidden          temporarily not drawn; isOpen UNCHANGED
///   instantiated    an INoteWindow exists in memory
///
/// ONLY <see cref="CloseNote"/> MAY CLEAR isOpen. Application exit, Windows
/// logoff, and internal disposal must never do so. WPF closes every window
/// during application shutdown, so if the close-glyph logic lived on
/// Window.Closing then choosing Exit would clear isOpen on every note and the
/// next boot would restore none of them -- exactly the failure this model
/// exists to prevent, and a direct breach of success criterion 3.
/// <see cref="ShutdownWithoutClosingNotes"/> is the shutdown path, and it
/// saves buffers and geometry while leaving isOpen alone.
///
/// This class NEVER references a WPF Window type. It talks to INoteWindow, so
/// all of the above is exercised headlessly by WindowManagerTests -- which is
/// the point, because none of it can be tested through a real window.
/// </remarks>
public sealed class WindowManager : IDisposable
{
    private readonly NoteRepository _repository;
    private readonly NoteIndexStore _indexStore;
    private readonly SettingsStore _settingsStore;
    private readonly INoteWindowFactory _factory;
    private readonly IMonitorProvider _monitors;
    private readonly ISystemTheme _theme;
    private readonly IFileDeletionService _deleter;
    private readonly IWriteLedger _ledger;
    private readonly RecoveryStore _recovery;
    private readonly string _diagnosticsFile;

    private readonly Dictionary<string, INoteWindow> _windows =
        NotePath.NewMap<INoteWindow>();

    private NoteIndex _index;
    private AppSettings _settings;
    private bool _disposed;

    public WindowManager(
        NoteRepository repository,
        NoteIndexStore indexStore,
        SettingsStore settingsStore,
        INoteWindowFactory factory,
        IMonitorProvider monitors,
        ISystemTheme theme,
        IFileDeletionService deleter,
        IWriteLedger ledger,
        RecoveryStore recovery,
        string diagnosticsFile)
    {
        _repository = repository;
        _indexStore = indexStore;
        _settingsStore = settingsStore;
        _factory = factory;
        _monitors = monitors;
        _theme = theme;
        _deleter = deleter;
        _ledger = ledger;
        _recovery = recovery;
        _diagnosticsFile = diagnosticsFile;

        _settings = _settingsStore.Load();
        _index = _indexStore.Load(_settings);
    }

    public IReadOnlyCollection<string> OpenPaths => _windows.Keys;

    /// <summary>
    /// Recreates a window for every note marked <c>isOpen</c>.
    /// </summary>
    /// <remarks>
    /// Notes merely PRESENT in the root get no window. Pointing StickyMD at an
    /// Obsidian vault must not carpet the desktop; a .md becomes available to
    /// StickyMD without becoming a sticky note.
    /// </remarks>
    public void RestoreOpenNotes()
    {
        foreach (var (path, state) in _index.Notes.ToList())
        {
            if (!state.IsOpen) continue;

            if (!File.Exists(path))
            {
                // The note was deleted or moved while StickyMD was not
                // running. Leave the entry alone -- it holds geometry that
                // costs nothing and would be missed if the file returns.
                DiagnosticsLog.Write(
                    _diagnosticsFile, $"{path}: marked open but the file is gone.");
                continue;
            }

            // ShowActivated=false, per the spec's --startup rule: a screenful
            // of notes must not fight the logon sequence for focus.
            Instantiate(path, state, activate: false);
        }
    }

    public void OpenNote(string path, bool activate = true)
    {
        if (!NotePath.TryCanonical(path, out var canonical))
        {
            DiagnosticsLog.Write(_diagnosticsFile, $"Refused to open '{path}': unusable path.");
            return;
        }

        // Never two windows on one file. This lookup is the reason every
        // boundary hands out one canonical form -- before that, a path from
        // the repository and one from the watcher were different keys here.
        if (_windows.TryGetValue(canonical, out var existing))
        {
            existing.FocusNote();
            return;
        }

        if (!File.Exists(canonical))
        {
            DiagnosticsLog.Write(
                _diagnosticsFile, $"Refused to open '{canonical}': no such file.");
            return;
        }

        var state = _index.Notes.TryGetValue(canonical, out var saved)
            ? saved with { IsOpen = true, LastOpenedUtc = DateTime.UtcNow }
            : DefaultStateFor();

        Instantiate(canonical, state, activate);
        Persist(canonical, state);
    }

    public string CreateAndOpenNote()
    {
        // NoteRepository.CreateNewRecorded is documented NOT thread-safe and
        // requires callers to serialise. This runs on the UI thread, which
        // satisfies that.
        var (path, outcome) = _repository.CreateNewRecorded();

        // Record the creation write, or the watcher reports StickyMD's own new
        // note as an external change the moment it appears.
        _ledger.Record(path, outcome);

        OpenNote(path);

        return path;
    }

    private NoteState DefaultStateFor()
    {
        var monitors = _monitors.GetMonitors();

        var work = monitors.FirstOrDefault(m => m.IsPrimary)?.WorkArea
            ?? monitors.FirstOrDefault()?.WorkArea
            ?? new PixelRect(0, 0, 1920, 1080);

        // Offset each new note so a burst of them does not stack invisibly on
        // one spot.
        var offset = _windows.Count * 28 % 200;

        return new NoteState(
            X: work.X + 80 + offset,
            Y: work.Y + 80 + offset,
            W: _settings.DefaultWidth,
            H: _settings.DefaultHeight,
            Monitor: null,
            Color: _settings.DefaultColor,
            Opacity: _settings.DefaultOpacity,
            AlwaysOnTop: false,
            IsOpen: true,
            LastOpenedUtc: DateTime.UtcNow);
    }

    private void Instantiate(string canonical, NoteState state, bool activate)
    {
        var clamped = ClampToMonitors(state);
        var theme = NotePalette.Get(clamped.Color, _theme.Resolve(_settings.Theme));

        var window = _factory.Create(canonical, clamped, theme);

        window.CloseRequested += CloseNote;
        window.DeleteRequested += DeleteNote;
        window.RenameRequested += OnRenameRequested;
        window.OpenNoteRequested += OnOpenNoteRequested;
        window.StateChanged += OnStateChanged;

        _windows[canonical] = window;

        window.ShowNote(activate);

        if (clamped != state) Persist(canonical, clamped);

        OfferRecovery(canonical, window);
    }

    private void OfferRecovery(string canonical, INoteWindow window)
    {
        var envelope = _recovery.TryLoad(canonical);
        if (envelope is null) return;

        // RecoveryStore.Clear is best-effort, so a snapshot can outlive its
        // successful save. Compare LastKnownDiskHash against what is on disk
        // now: if they match, that text already reached the file and offering
        // it would present older content as a recovery.
        try
        {
            var current = NoteFile.Read(canonical);

            if (string.Equals(
                    envelope.LastKnownDiskHash,
                    current.ContentHash,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(envelope.Content, current.Text, StringComparison.Ordinal))
            {
                _recovery.Clear(canonical);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.DecoderFallbackException)
        {
            // Cannot compare. Offer it -- the user losing a click beats the
            // user losing text.
        }

        window.ShowRecovered(envelope);
    }

    private void OnOpenNoteRequested(string path) => OpenNote(path);

    /// <summary>
    /// Detaches every handler before a window leaves this manager's ownership.
    /// </summary>
    /// <remarks>
    /// Not a leak fix -- a late event. Disposing a NoteWindow can raise
    /// StateChanged (Dispose -> Close -> Closing -> StateChanged), which would
    /// reach OnStateChanged and write to the index while we are in the middle
    /// of closing or deleting that very note. It happens not to fire today
    /// only because NoteWindow.Dispose unsubscribes its own Closing handler
    /// first -- an invariant living in another file that nothing here can
    /// see. Detaching on this side makes the guarantee local and survives a
    /// refactor over there.
    /// </remarks>
    private void Detach(INoteWindow window)
    {
        window.CloseRequested -= CloseNote;
        window.DeleteRequested -= DeleteNote;
        window.RenameRequested -= OnRenameRequested;
        window.OpenNoteRequested -= OnOpenNoteRequested;
        window.StateChanged -= OnStateChanged;
    }

    /// <summary>
    /// The close glyph. THE ONLY path that clears <c>isOpen</c>.
    /// </summary>
    public void CloseNote(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;

        if (_windows.Remove(canonical, out var window))
        {
            // Detach BEFORE Dispose: see Detach's remarks. Belt-and-braces
            // alongside NoteWindow's own Closing unsubscribe, not a
            // replacement for it.
            Detach(window);

            // Save before disposing: the buffer may hold unsaved text, and
            // this is a user action, not a crash.
            window.SaveNow();
            window.Dispose();
        }

        if (_index.Notes.TryGetValue(canonical, out var state))
            Persist(canonical, state with { IsOpen = false });
    }

    /// <summary>Hide All. <c>isOpen</c> is untouched, by design.</summary>
    public void HideAll()
    {
        foreach (var window in _windows.Values) window.HideNote();
    }

    public void ShowAll()
    {
        foreach (var window in _windows.Values) window.ShowNote(activate: false);
    }

    /// <summary>
    /// The shutdown path: application exit, Windows logoff, and
    /// <c>SessionEnding</c>.
    /// </summary>
    /// <remarks>
    /// Saves buffers and persists geometry, and DOES NOT TOUCH isOpen. That
    /// distinction is the whole reason this method exists separately from
    /// <see cref="CloseNote"/>.
    /// </remarks>
    public void ShutdownWithoutClosingNotes()
    {
        foreach (var (path, window) in _windows.ToList())
        {
            // Spec §8.1: a snapshot is also written on app exit if the note is
            // still unsaved. SaveNow tries the file first; the coordinator
            // writes the snapshot if it cannot.
            window.SaveNow();

            var bounds = window.Bounds;

            if (bounds.Width > 0 && bounds.Height > 0
                && _index.Notes.TryGetValue(path, out var state))
            {
                Persist(path, state with
                {
                    X = bounds.X, Y = bounds.Y, W = bounds.Width, H = bounds.Height,
                });
            }

            // Detach BEFORE Dispose: see Detach's remarks. Without this, a
            // late StateChanged raised by Dispose's own Close could overwrite
            // the geometry just persisted above.
            Detach(window);
            window.Dispose();
        }

        _windows.Clear();
    }

    public void DeleteNote(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;

        var result = _deleter.SendToRecycleBin(canonical);

        if (result.Outcome is DeletionOutcome.Failed or DeletionOutcome.Cancelled)
        {
            // Never destroy a file, and never pretend to. Closing the window
            // here would leave the file behind with nothing on screen saying
            // so.
            DiagnosticsLog.Write(
                _diagnosticsFile,
                $"{canonical}: delete {result.Outcome} -- {result.Message ?? "no reason given"}");
            return;
        }

        if (_windows.Remove(canonical, out var window))
        {
            // Detach BEFORE Dispose: see Detach's remarks. Without this, a
            // late StateChanged raised by Dispose's own Close could re-add
            // the very entry the lines below are about to remove.
            Detach(window);
            window.Dispose();
        }

        // The entry goes too: geometry for a file in the Recycle Bin is
        // clutter, and restoring the file gives it a fresh default position.
        _index.Notes.Remove(canonical);
        _indexStore.Save(_index);

        _recovery.Clear(canonical);
    }

    private void OnRenameRequested(string currentPath, string newFileName)
    {
        if (!NotePath.TryCanonical(currentPath, out var canonical)) return;

        string renamed;

        try
        {
            renamed = _repository.Rename(canonical, newFileName);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // The name is taken, or Windows will not accept it. Both files are
            // untouched, which is the property that matters.
            DiagnosticsLog.Write(
                _diagnosticsFile, $"{canonical}: rename refused -- {ex.Message}");
            return;
        }

        Rekey(canonical, renamed);
    }

    // ---- Watcher handlers -------------------------------------------------

    public void OnExternalChanged(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;
        if (!_windows.TryGetValue(canonical, out var window)) return;

        try
        {
            var content = NoteFile.Read(canonical);

            // Pass the NoteContent straight through. The window does its own
            // ComputeToken comparison internally -- computing a token here and
            // handing it over instead would lose the raw ContentHash the
            // window needs for the recovery envelope's LastKnownDiskHash, and
            // an external editor that only changed line endings or added a
            // BOM would have that change silently reverted on the next save.
            window.ApplyExternalContent(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.DecoderFallbackException)
        {
            DiagnosticsLog.Write(
                _diagnosticsFile,
                $"{canonical}: an external change could not be read -- {ex.Message}");
        }
    }

    public void OnDeleted(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;
        if (!_windows.TryGetValue(canonical, out var window)) return;

        // The file is gone; the note is NOT closed and isOpen is untouched.
        // The user still gets Recreate, and closing it for them would discard
        // the buffer -- which may be the only remaining copy.
        window.NotifyFileDeleted();
    }

    public void OnRenamed(string oldPath, string newPath)
    {
        if (!NotePath.TryCanonical(oldPath, out var oldCanonical)) return;
        if (!NotePath.TryCanonical(newPath, out var newCanonical)) return;

        Rekey(oldCanonical, newCanonical);
    }

    /// <summary>
    /// <see cref="NoteWatcher.Recovered"/>. Re-reads every OPEN note.
    /// </summary>
    /// <remarks>
    /// NoteWatcher deliberately does not decide which notes to re-read -- it
    /// has no idea which are open, and this is where that knowledge lives.
    /// FileSystemWatcher drops events when its internal buffer overflows, so
    /// after a recovery any open note may be stale and there is no way to know
    /// which.
    /// </remarks>
    public void OnWatcherRecovered(Exception cause)
    {
        DiagnosticsLog.Write(
            _diagnosticsFile,
            $"The file watcher was recreated after {cause.GetType().Name}: {cause.Message}. "
                + $"Re-reading {_windows.Count} open note(s).");

        foreach (var path in _windows.Keys.ToList()) OnExternalChanged(path);
    }

    /// <summary>
    /// <c>WM_DISPLAYCHANGE</c>, via <c>SystemEvents.DisplaySettingsChanged</c>.
    /// </summary>
    public void OnDisplaySettingsChanged()
    {
        var monitors = _monitors.GetMonitors();

        foreach (var (path, window) in _windows.ToList())
        {
            var bounds = window.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;

            var clamped = WindowPlacement.Clamp(bounds, monitors);

            // Only touch a note that actually moved. Reapplying a correct rect
            // would nudge every window on every display change.
            if (clamped == bounds) continue;

            if (!_index.Notes.TryGetValue(path, out var state)) continue;

            var updated = state with
            {
                X = clamped.X, Y = clamped.Y, W = clamped.Width, H = clamped.Height,
            };

            window.ApplyState(
                updated,
                NotePalette.Get(updated.Color, _theme.Resolve(_settings.Theme)));

            Persist(path, updated);
        }
    }

    // ---- Index plumbing -----------------------------------------------------

    private void OnStateChanged(string path, NoteState state)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;
        Persist(canonical, state);
    }

    private void Rekey(string oldCanonical, string newCanonical)
    {
        if (_index.Notes.Remove(oldCanonical, out var state))
        {
            _index.Notes[newCanonical] = state;
            _indexStore.Save(_index);
        }

        if (!_windows.Remove(oldCanonical, out var window)) return;

        _windows[newCanonical] = window;

        // The window remaps note.local as part of this, or images in the moved
        // note silently stop loading.
        window.NotifyRenamed(newCanonical);
    }

    private NoteState ClampToMonitors(NoteState state)
    {
        var clamped = WindowPlacement.Clamp(
            new PixelRect(state.X, state.Y, state.W, state.H),
            _monitors.GetMonitors());

        return state with
        {
            X = clamped.X, Y = clamped.Y, W = clamped.Width, H = clamped.Height,
        };
    }

    private void Persist(string canonical, NoteState state)
    {
        _index.Notes[canonical] = state;
        _indexStore.Save(_index);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose is NOT a close. It goes down the shutdown path, so isOpen
        // survives whatever caused it.
        ShutdownWithoutClosingNotes();
    }
}
