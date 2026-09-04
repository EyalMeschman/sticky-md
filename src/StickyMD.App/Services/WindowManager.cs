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
/// This class talks to INoteWindow, not to a WPF Window type, so all of the
/// above is exercised headlessly by WindowManagerTests -- which is the point,
/// because none of it can be tested through a real window. One temporary
/// exception: <see cref="Instantiate"/> checks <c>window is NoteWindow</c> to
/// wire the Plan-B-only New Note hotkey, since that event is not on
/// INoteWindow. It goes away with Plan C's tray, and WindowManagerTests drives
/// fakes, so that one branch is untested here.
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

    /// <summary>
    /// The light/dark mode every open note is currently rendered with.
    /// </summary>
    /// <remarks>
    /// The gate for <see cref="OnSystemThemeChanged"/>: the OS raises that for
    /// far more than a light/dark flip, and re-theming a note re-navigates its
    /// WebView shell. Seeded here so the first genuine flip after startup is
    /// not swallowed.
    /// </remarks>
    private ThemeMode _lastResolvedMode;

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
        _lastResolvedMode = _theme.Resolve(_settings.Theme);
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

        // No Persist here. Instantiate persists the CLAMPED state, and this
        // used to re-persist the unclamped one straight afterwards -- so
        // notes.json kept the off-screen geometry that had just been
        // corrected. The window was right and only the index drifted, which is
        // the kind of bug that surfaces one restart later.
        Instantiate(canonical, state, activate);
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

        // Spec §362: a newly created empty note opens directly in edit mode
        // with focus. Guarded on the window actually being in the map --
        // OpenNote can refuse a path (an unusable one, or the file vanishing
        // between creation and here), and this must not throw when it does.
        // Only CreateAndOpenNote does this; OpenNote on an EXISTING note
        // stays in preview.
        if (_windows.TryGetValue(path, out var window)) window.EnterEditMode();

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

        // TEMPORARY (Plan B only). Plan C's tray owns New Note. A method
        // group, not a lambda -- two separately-written lambda expressions
        // compile to two different backing methods, so `-= <the other one>`
        // in Detach would silently fail to remove this subscription.
        if (window is NoteWindow note)
            note.NewNoteRequested += OnNewNoteRequested;

        _windows[canonical] = window;

        window.ShowNote(activate);

        // ALWAYS the clamped state, and always from here -- this is the single
        // point where "what the window was given" and "what the index records"
        // are the same value. Persisting unconditionally rather than only on a
        // change costs one write of identical content.
        Persist(canonical, clamped);

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

        // TEMPORARY (Plan B only), symmetric with the subscribe in
        // Instantiate. Same method group, or this would not actually detach.
        if (window is NoteWindow note)
            note.NewNoteRequested -= OnNewNoteRequested;
    }

    /// <summary>
    /// TEMPORARY (Plan B only). A named method so <see cref="Detach"/> can
    /// unsubscribe the exact delegate <see cref="Instantiate"/> subscribed.
    /// </summary>
    private void OnNewNoteRequested() => CreateAndOpenNote();

    /// <summary>
    /// The close glyph. Takes the note off the screen and leaves
    /// <c>isOpen</c> ALONE, so the next launch brings it back.
    /// </summary>
    public void CloseNote(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;

        PixelRect? bounds = null;

        if (_windows.Remove(canonical, out var window))
        {
            // Detach BEFORE Dispose: see Detach's remarks. Belt-and-braces
            // alongside NoteWindow's own Closing unsubscribe, not a
            // replacement for it.
            Detach(window);

            // Harvest geometry HERE, exactly as ShutdownWithoutClosingNotes
            // does, and before Dispose -- a closed window's handle is gone, so
            // GetWindowRect has nothing to answer with afterwards.
            //
            // This is not belt-and-braces. Window.Closing was meant to be the
            // harvest for this path and cannot be: Detach above has already
            // removed StateChanged, and NoteWindow.Dispose unsubscribes its own
            // Closing handler before calling Close(). So without this read,
            // moving a note and then closing it with the close glyph persisted
            // the position it had when it was last saved, and the note came
            // back in the wrong place.
            bounds = window.Bounds;

            // Save before disposing: the buffer may hold unsaved text, and
            // this is a user action, not a crash.
            window.SaveNow();
            window.Dispose();
        }

        if (!_index.Notes.TryGetValue(canonical, out var state)) return;

        if (bounds is { Width: > 0, Height: > 0 } rect)
        {
            state = state with
            {
                X = rect.X, Y = rect.Y, W = rect.Width, H = rect.Height,
            };
        }

        // isOpen is deliberately NOT cleared. The close glyph means "off my
        // screen", not "off my desktop set" -- a note the user opened returns
        // on the next launch, and the only way out is a real deletion through
        // the more menu. Nothing in the app clears isOpen any more: exit and
        // logoff never did, and this path stopped after the first person to
        // run the app closed three notes and found them gone.
        //
        // A note merely PRESENT in the notes root still gets no window (see
        // RestoreOpenNotes) -- that rule is what keeps an Obsidian vault from
        // carpeting the desktop, and it is the reason dropping the third state
        // costs so little.
        Persist(canonical, state);
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
        SaveIndex(canonical);

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

    /// <summary>
    /// <see cref="ISystemTheme.Changed"/>. Re-themes every open note when the
    /// OS light/dark setting flips -- the only handler for
    /// <c>ThemePreference.System</c>, which is the default.
    /// </summary>
    public void OnSystemThemeChanged()
    {
        var resolved = _theme.Resolve(_settings.Theme);

        // SystemTheme raises Changed for UserPreferenceCategory.General,
        // VisualStyle AND Color -- categories Windows raises for accent-colour
        // changes, wallpaper and theme touches, and a broad slice of
        // WM_SETTINGCHANGE traffic, not only for light/dark. Every one of them
        // used to drive ApplyState across every open window, which
        // re-navigated each note's WebView shell and re-posted a render: a
        // user changing their accent colour watched every note on the desktop
        // flash, plus a redundant SetWindowPos each. NoteWindow.ApplyState
        // guards this too, from the other side.
        if (resolved == _lastResolvedMode) return;

        _lastResolvedMode = resolved;

        foreach (var (path, window) in _windows.ToList())
        {
            if (!_index.Notes.TryGetValue(path, out var state)) continue;

            var bounds = window.Bounds;

            // Geometry from Bounds (LIVE), NEVER from _index -- the same trap
            // OnDisplaySettingsChanged already avoids. Taking X/Y/W/H from the
            // index would snap every note back to its last PERSISTED position
            // on a theme flip, discarding a move that was never saved.
            var updated = bounds.Width > 0 && bounds.Height > 0
                ? state with { X = bounds.X, Y = bounds.Y, W = bounds.Width, H = bounds.Height }
                : state;

            window.ApplyState(updated, NotePalette.Get(updated.Color, resolved));

            // No Persist: nothing about the note's own recorded state changed
            // -- only which theme it renders with, which Resolve recomputes
            // fresh every time anyway.
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
            SaveIndex(newCanonical);
        }

        if (!_windows.Remove(oldCanonical, out var window)) return;

        // A rename can land ON a path that already has a window open: a move
        // or copy with overwrite, a git checkout, Obsidian. Explorer's own
        // rename refuses, but plenty of writers do not. Assigning over the
        // key without this dropped the displaced window from the map while it
        // stayed visible -- still holding its own buffer and still autosaving
        // to this path. Two windows owning one file, and whichever saved last
        // won.
        //
        // NOT Dispose. Dispose flushes the displaced buffer over the file that
        // was just renamed into place, which is exactly the loss this
        // prevents. NotifyFileDeleted instead: the buffer survives and the
        // user can SEE it, behind the "file is gone" bar. That bar's Recreate
        // would then write the displaced buffer to this path and overwrite the
        // renamed file -- deliberate and accepted. An explicit click on a bar
        // that says what happened is strictly better than today's silent race.
        // Do not "fix" it back.
        if (_windows.Remove(newCanonical, out var displaced))
        {
            Detach(displaced);
            displaced.NotifyFileDeleted();

            DiagnosticsLog.Write(
                _diagnosticsFile,
                $"'{oldCanonical}' was renamed onto '{newCanonical}', which was already open. "
                    + "The displaced note keeps its text behind the \"file is gone\" bar; "
                    + "nothing has been written to either path yet.");
        }

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
        SaveIndex(canonical);
    }

    /// <summary>
    /// The ONLY place <c>_indexStore.Save</c> is called from.
    /// </summary>
    /// <remarks>
    /// JsonFile.Write throws on any I/O failure, so an unguarded save turned a
    /// full or locked %LOCALAPPDATA% into an unhandled exception on the UI
    /// thread -- raised by something as small as a colour click, a pin toggle,
    /// or a window's Closing. DiagnosticsLog and RecoveryStore.Save are both
    /// guarded against exactly this; the index write was the one that was not.
    /// Losing a note's geometry is acceptable. Crashing the app over it, and
    /// with it every other note's unsaved buffer, is not. The in-memory index
    /// keeps the change either way, so the next save that succeeds writes it
    /// after all.
    /// </remarks>
    private void SaveIndex(string context)
    {
        try
        {
            _indexStore.Save(_index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write(
                _diagnosticsFile,
                $"{context}: notes.json could not be written -- {ex.Message}. "
                    + "Window positions from this session may be lost; the .md files are untouched.");
        }
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
