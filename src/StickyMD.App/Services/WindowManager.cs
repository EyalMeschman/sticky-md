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
/// One row of the tray's Recent Notes submenu.
/// </summary>
/// <param name="IsOpen">
/// A window exists for it right now, which is what the menu check mark means.
/// NOT the index's <c>isOpen</c> -- that is true for every note the user has
/// ever opened, so ticking it would tick the whole list.
/// </param>
public sealed record RecentNote(string Path, string Title, bool IsOpen);

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
/// because none of it can be tested through a real window. Nothing here
/// checks for the concrete <c>NoteWindow</c> type, so every branch is
/// reachable through fakes.
/// </remarks>
public sealed class WindowManager(
    NoteRepository repository,
    NoteIndex index,
    NoteIndexStore indexStore,
    AppSettings settings,
    INoteWindowFactory factory,
    IMonitorProvider monitors,
    ISystemTheme theme,
    IFileDeletionService deleter,
    IWriteLedger ledger,
    RecoveryStore recovery,
    string diagnosticsFile) : IDisposable
{
    /// <summary>Spec §7: "Recent Notes (10 by lastOpenedUtc)".</summary>
    private const int RecentNotesShown = 10;

    private readonly Dictionary<string, INoteWindow> _windows =
        NotePath.NewMap<INoteWindow>();

    private AppSettings _settings = settings;
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
    private ThemeMode _lastResolvedMode = theme.Resolve(settings.Theme);

    public IReadOnlyCollection<string> OpenPaths => _windows.Keys;

    /// <summary>
    /// The repository the manager is currently working against. Replaced by
    /// <see cref="ApplySettings"/> when the notes root changes.
    /// </summary>
    /// <remarks>
    /// Exposed because <c>App</c> owns the <c>NoteWatcher</c>, and a watcher
    /// pointed at the previous root would go on reporting external edits for
    /// a folder that is no longer the notes root while missing every edit in
    /// the one that is.
    /// </remarks>
    public NoteRepository Repository { get; private set; } = repository;

    /// <summary>The settings every open note is currently rendered with.</summary>
    public AppSettings Settings => _settings;

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
        foreach (var (path, state) in index.Notes.ToList())
        {
            if (!state.IsOpen) continue;

            if (!File.Exists(path))
            {
                // The note was deleted or moved while StickyMD was not
                // running. Leave the entry alone -- it holds geometry that
                // costs nothing and would be missed if the file returns.
                DiagnosticsLog.Write(
                    diagnosticsFile, $"{path}: marked open but the file is gone.");
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
            DiagnosticsLog.Write(diagnosticsFile, $"Refused to open '{path}': unusable path.");
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
                diagnosticsFile, $"Refused to open '{canonical}': no such file.");
            return;
        }

        var state = index.Notes.TryGetValue(canonical, out var saved)
            ? saved with { IsOpen = true, LastOpenedUtc = DateTime.UtcNow }
            : DefaultStateFor();

        // No Persist here. Instantiate persists the CLAMPED state; persisting
        // the unclamped one as well would leave notes.json holding off-screen
        // geometry the window itself had already corrected.
        Instantiate(canonical, state, activate);
    }

    /// <summary>
    /// New Note. Returns the path, or null when the notes root would not take
    /// one — the caller says so, this only records it.
    /// </summary>
    /// <remarks>
    /// THE GUARD IS NOT DEFENSIVE DECORATION. `CreateNewRecorded` starts with
    /// `EnsureRootExists`, which throws for a notes root that is gone: an
    /// unplugged drive, a dropped network share, a folder deleted from under
    /// the app, or the one settings.json names on a machine where it never
    /// existed. Spec §8 keeps the app alive in the tray in that state, so the
    /// throw is one click away on the tray menu, one keypress away on the
    /// hotkey, and reachable from `--new` -- and from a Click handler it
    /// reaches `DispatcherUnhandledException`, which closes the whole app,
    /// every other note's unsaved buffer included.
    ///
    /// Guarded HERE rather than at the three call sites, because here is where
    /// they all route through.
    /// </remarks>
    public string? CreateAndOpenNote()
    {
        string path;
        NoteFile.WriteOutcome outcome;

        try
        {
            // NoteRepository.CreateNewRecorded is documented NOT thread-safe
            // and requires callers to serialise. This runs on the UI thread,
            // which satisfies that.
            (path, outcome) = Repository.CreateNewRecorded();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write(
                diagnosticsFile,
                $"A new note could not be created in '{Repository.NotesRoot}' -- {ex.Message}");

            return null;
        }

        // Record the creation write, or the watcher reports StickyMD's own new
        // note as an external change the moment it appears.
        ledger.Record(path, outcome);

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
        var screens = monitors.GetMonitors();

        var work = screens.FirstOrDefault(m => m.IsPrimary)?.WorkArea
            ?? screens.FirstOrDefault()?.WorkArea
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

            // Pinned by default: a sticky note you have to hunt for behind the
            // window you are taking notes about is not doing its job. The ⋯
            // menu unpins per note, and the index remembers that.
            AlwaysOnTop: true,
            IsOpen: true,
            LastOpenedUtc: DateTime.UtcNow,
            FontSizePx: _settings.DefaultFontSizePx);
    }

    private void Instantiate(string canonical, NoteState state, bool activate)
    {
        var clamped = ClampToMonitors(state);
        var palette = NotePalette.Get(clamped.Color, theme.Resolve(_settings.Theme));

        var window = factory.Create(canonical, clamped, palette, _settings.AllowRemoteImages);

        window.CloseRequested += CloseNote;
        window.DeleteRequested += DeleteNote;
        window.RenameRequested += OnRenameRequested;
        window.OpenNoteRequested += OnOpenNoteRequested;
        window.StateChanged += OnStateChanged;

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
        var envelope = recovery.TryLoad(canonical);
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
                recovery.Clear(canonical);
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

        if (!index.Notes.TryGetValue(canonical, out var state)) return;

        if (bounds is { Width: > 0, Height: > 0 } rect)
        {
            state = state.WithBounds(rect);
        }

        // isOpen is deliberately NOT cleared. The close glyph means "off my
        // screen", not "off my desktop set" -- a note the user opened returns
        // on the next launch, and the only way out is a real deletion through
        // the more menu. Nothing in the app clears isOpen: closing three notes
        // and finding them gone on the next launch is exactly the loss this
        // prevents.
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
    /// The tray's left click, per spec §7: "Left-click toggles Show All /
    /// Hide All".
    /// </summary>
    /// <remarks>
    /// The decision is read off the screen rather than from a remembered
    /// flag. A flag would go out of step the moment a note was closed with
    /// <c>✕</c> or a new one opened, and the symptom is a left click that
    /// needs pressing twice.
    ///
    /// With no windows at all this does nothing, deliberately: there is
    /// nothing to show, and creating a note would make left-click mean two
    /// different things depending on state. New Note is one item away on the
    /// menu.
    /// </remarks>
    public void ToggleShowHideAll()
    {
        if (_windows.Values.Any(w => w.IsVisible)) HideAll();
        else ShowAll();
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
                && index.Notes.TryGetValue(path, out var state))
            {
                Persist(path, state.WithBounds(bounds));
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

        var result = deleter.SendToRecycleBin(canonical);

        if (result.Outcome is DeletionOutcome.Failed or DeletionOutcome.Cancelled)
        {
            // Never destroy a file, and never pretend to. Closing the window
            // here would leave the file behind with nothing on screen saying
            // so.
            DiagnosticsLog.Write(
                diagnosticsFile,
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
        index.Notes.Remove(canonical);
        SaveIndex(canonical);

        recovery.Clear(canonical);
    }

    private void OnRenameRequested(string currentPath, string newFileName)
    {
        if (!NotePath.TryCanonical(currentPath, out var canonical)) return;

        string renamed;

        try
        {
            renamed = Repository.Rename(canonical, newFileName);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // The name is taken, or Windows will not accept it. Both files are
            // untouched, which is the property that matters.
            DiagnosticsLog.Write(
                diagnosticsFile, $"{canonical}: rename refused -- {ex.Message}");
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
                diagnosticsFile,
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
            diagnosticsFile,
            $"The file watcher was recreated after {cause.GetType().Name}: {cause.Message}. "
                + $"Re-reading {_windows.Count} open note(s).");

        foreach (var path in _windows.Keys.ToList()) OnExternalChanged(path);
    }

    /// <summary>
    /// <c>WM_DISPLAYCHANGE</c>, via <c>SystemEvents.DisplaySettingsChanged</c>.
    /// </summary>
    public void OnDisplaySettingsChanged()
    {
        var screens = monitors.GetMonitors();

        foreach (var (path, window) in _windows.ToList())
        {
            var bounds = window.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;

            var clamped = WindowPlacement.Clamp(bounds, screens);

            // Only touch a note that actually moved. Reapplying a correct rect
            // would nudge every window on every display change.
            if (clamped == bounds) continue;

            if (!index.Notes.TryGetValue(path, out var state)) continue;

            var updated = state.WithBounds(clamped);

            window.ApplyState(
                updated,
                NotePalette.Get(updated.Color, theme.Resolve(_settings.Theme)),
                _settings.AllowRemoteImages);

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
        var resolved = theme.Resolve(_settings.Theme);

        // SystemTheme raises Changed for UserPreferenceCategory.General,
        // VisualStyle AND Color -- categories Windows raises for accent-colour
        // changes, wallpaper and theme touches, and a broad slice of
        // WM_SETTINGCHANGE traffic, not only for light/dark. Without this gate
        // every one of them would drive ApplyState across every open window
        // and re-navigate each note's WebView shell: a user changing their
        // accent colour would watch every note on the desktop flash.
        // NoteWindow.ApplyState guards this too, from the other side.
        if (resolved == _lastResolvedMode) return;

        ReapplyAll(resolved);
    }

    /// <summary>
    /// Pushes the current settings and <paramref name="resolved"/> theme mode
    /// into every open window.
    /// </summary>
    /// <remarks>
    /// Geometry comes from each window's LIVE Bounds, never from the index --
    /// taking X/Y/W/H from the index would snap every note back to its last
    /// PERSISTED position, discarding a move that was never saved. Nothing is
    /// persisted here either: nothing about a note's own recorded state has
    /// changed, only how it is rendered.
    /// </remarks>
    private void ReapplyAll(ThemeMode resolved)
    {
        _lastResolvedMode = resolved;

        foreach (var (path, window) in _windows.ToList())
        {
            if (!index.Notes.TryGetValue(path, out var state)) continue;

            var bounds = window.Bounds;
            var updated = bounds.Width > 0 && bounds.Height > 0 ? state.WithBounds(bounds) : state;

            window.ApplyState(
                updated, NotePalette.Get(updated.Color, resolved), _settings.AllowRemoteImages);
        }
    }

    // ---- Settings and the tray ----------------------------------------------

    /// <summary>
    /// The tray's Recent Notes list: most recently OPENED first, with the ones
    /// that currently have a window flagged so the menu can tick them.
    /// </summary>
    /// <remarks>
    /// Recent means recently OPENED, per spec §5, which is why it is ordered
    /// by <c>lastOpenedUtc</c> off the index rather than by anything on disk. A
    /// .md merely present in the notes root has no index entry and so does not
    /// appear here -- pointing StickyMD at an Obsidian vault must not fill this
    /// menu with a thousand files any more than it may carpet the desktop.
    ///
    /// Entries whose file has gone are dropped rather than listed and then
    /// refused on click. The filter runs BEFORE the take, or a deleted note
    /// would silently cost the list one of its ten slots.
    ///
    /// Ten is not configurable, because spec §7 fixes it and nothing asks for
    /// another number.
    /// </remarks>
    public IReadOnlyList<RecentNote> RecentNotes()
        => index.Notes
            .OrderByDescending(e => e.Value.LastOpenedUtc)
            .Where(e => File.Exists(e.Key))
            .Take(RecentNotesShown)
            .Select(e => new RecentNote(e.Key, TitleOf(e.Key), _windows.ContainsKey(e.Key)))
            .ToList();

    /// <remarks>
    /// Reads the file, because <c>NoteTitleResolver</c> needs its content and
    /// the index deliberately stores no title -- a cached one would go stale
    /// the moment the note was edited in VS Code. Ten small reads on a
    /// right-click; the notes this app is for are kilobytes.
    /// </remarks>
    private string TitleOf(string path)
    {
        try
        {
            return NoteTitleResolver.Resolve(NoteFile.Read(path).Text, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.DecoderFallbackException)
        {
            // A locked or unreadable note still belongs on the menu -- opening
            // it is how the user finds out what is wrong with it.
            return Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>
    /// Adopts a saved settings change and pushes it into every open note.
    /// </summary>
    /// <remarks>
    /// Does NOT save settings.json -- <c>App</c> does that first, so a write
    /// that fails does not leave the running app applying settings the file
    /// does not hold.
    ///
    /// Three things travel through here. The theme, because
    /// <c>ThemePreference</c> may have moved off System. <c>allowRemoteImages</c>,
    /// because spec §6's CSP is fixed per shell and every open note has to
    /// re-navigate. And the notes root, which replaces the repository so New
    /// Note lands in the folder the user just chose rather than in the old one
    /// -- silently creating notes somewhere settings.json no longer names was
    /// the alternative, and it is invisible until someone goes looking for the
    /// note they just made.
    ///
    /// Already-open notes are NOT moved or closed when the root changes. They
    /// are real files at absolute paths and they stay exactly where they are;
    /// what changes is where new ones go and which folder is watched.
    /// </remarks>
    public void ApplySettings(AppSettings settings)
    {
        var rootChanged = !NotePath.AreSame(_settings.NotesRoot, settings.NotesRoot);

        _settings = settings;

        if (rootChanged) Repository = new NoteRepository(settings.NotesRoot, new SystemClock());

        ReapplyAll(theme.Resolve(settings.Theme));
    }

    // ---- Index plumbing -----------------------------------------------------

    private void OnStateChanged(string path, NoteState state)
    {
        if (!NotePath.TryCanonical(path, out var canonical)) return;
        Persist(canonical, state);
    }

    private void Rekey(string oldCanonical, string newCanonical)
    {
        if (index.Notes.Remove(oldCanonical, out var state))
        {
            index.Notes[newCanonical] = state;
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

            // BEFORE the bar, and not optional. Detaching removes this window
            // from the map but leaves it running, and its autosave timer may
            // ALREADY be armed from a keystroke a moment ago. Without this the
            // tick lands after the rename and writes the displaced text over
            // the file just renamed into place -- no click, no warning, and
            // the renamed-in content is gone.
            displaced.StopAutomaticSaves();

            displaced.NotifyFileDeleted();

            DiagnosticsLog.Write(
                diagnosticsFile,
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
        => state.WithBounds(WindowPlacement.Clamp(state.Bounds, monitors.GetMonitors()));

    private void Persist(string canonical, NoteState state)
    {
        index.Notes[canonical] = state;
        SaveIndex(canonical);
    }

    /// <summary>
    /// The ONLY place <c>indexStore.Save</c> is called from.
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
            indexStore.Save(index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write(
                diagnosticsFile,
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
