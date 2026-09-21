using System.IO;
using System.Windows;
using StickyMD.App.Interop;
using StickyMD.App.Services;
using StickyMD.App.Windows;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;

namespace StickyMD.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private WindowManager? _manager;
    private NoteWatcher? _watcher;
    private SystemTheme? _theme;
    private SettingsStore? _settingsStore;
    private NoteIndexStore? _indexStore;
    private WriteLedger? _ledger;

    // ---- Shell services ---------------------------------------------------
    private TrayIconService? _tray;
    private HotkeyManager? _hotkeys;
    private StartupManager? _startup;

    /// <summary>
    /// The one Settings window, or null when it is closed.
    /// </summary>
    /// <remarks>
    /// Held because Settings is shown NON-MODAL: without this, the tray's
    /// Settings item and the hotkey warning item would each open another copy,
    /// and two of them saving in turn would fight over settings.json.
    /// </remarks>
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// Why the notes root is unusable, when it is.
    /// </summary>
    /// <remarks>
    /// Spec §8: "Notes root missing → created on startup; if creation fails,
    /// Settings opens with a banner and the app stays alive in tray."
    /// </remarks>
    private string? _notesRootFailure;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // FIRST, before anything that can throw. This is not defensive
        // decoration: OnExit does not run for an unhandled exception, so
        // without these handlers one escaped exception anywhere on the
        // dispatcher takes EVERY open note's unsaved buffer with it -- no
        // flush, no recovery snapshot, not even a diagnostics line, just the
        // Windows crash dialog. The whole branch is written as though this
        // existed; several async void handlers (NoteWindow.OnSourceInitialized,
        // NoteWindow.OnTaskToggleRequested) reach it directly.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Single instance BEFORE anything that reads or writes app state, and
        // before the runtime check: two processes each hold their own copy of
        // notes.json and write the whole snapshot on every save, so the loser
        // must not get as far as loading one.
        if (!SingleInstance.TryAcquire(SingleInstance.DefaultName, out _instance))
        {
            if (!SingleInstance.TrySend(SingleInstance.DefaultName, e.Args))
            {
                // Said out loud rather than swallowed: this launch is exiting
                // regardless, so a failed handoff is a note the user asked for
                // and will never see open.
                DiagnosticsLog.Write(
                    AppPaths.DiagnosticsFile,
                    "Another StickyMD holds this session, but the handoff pipe did not answer. "
                        + $"This launch exited without opening: {string.Join(", ", e.Args)}");
            }

            Shutdown();
            return;
        }

        // Raised on a pipe thread, so Dispatch is not optional -- the same
        // shape as every NoteWatcher handler below.
        _instance!.Received += args => Dispatch(() => ApplyLaunchArgs(args));

        // Check the runtime FIRST. Without it a note window opens and simply
        // never paints, which looks like a bug in the window rather than a
        // missing dependency.
        if (WebViewEnvironment.DetectRuntimeVersion() is null)
        {
            ShowRuntimeMissingDialog();
            return;
        }

        _settingsStore = new SettingsStore(AppPaths.SettingsFile);
        var settings = _settingsStore.Load();

        _indexStore = new NoteIndexStore(AppPaths.NoteIndexFile);

        // Load the index with the validated settings, so an entry with an
        // unusable colour or size falls back to the user's defaults rather
        // than to the type's. Loaded ONCE, here, and handed to the manager:
        // a second Load would reset both stores' LastCorruptBackupPath before
        // the tray got to balloon it.
        var index = _indexStore.Load(settings);

        ReportStartupState(_settingsStore, _indexStore);

        var repository = new NoteRepository(settings.NotesRoot, new SystemClock());

        try
        {
            repository.EnsureRootExists();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not a shutdown. The tray and Settings are built below this line,
            // so the app stays up, says why, and opens the one window that can
            // point it somewhere else. Nothing is created, nothing is opened,
            // and no note is lost -- there are none yet.
            _notesRootFailure =
                $"StickyMD could not create its notes folder:\n\n{settings.NotesRoot}\n\n"
                    + $"{ex.Message}\n\nChoose a different folder below.";

            DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"The notes root '{settings.NotesRoot}' could not be created -- {ex.Message}");
        }

        // One shared WriteLedger for every note is what makes self-write
        // suppression work; a per-window ledger makes every save look external
        // and the note reloads in a loop. Built BEFORE the factory, which takes
        // it and passes it through to every NoteWindow it creates.
        _ledger = new WriteLedger();
        _theme = new SystemTheme();

        _manager = new WindowManager(
            repository,
            index,
            _indexStore,
            settings,
            new NoteWindowFactory(_ledger),
            new MonitorEnumerator(),
            _theme,
            new RecycleBinService(),
            _ledger,
            new RecoveryStore(AppPaths.RecoveryDir),
            AppPaths.DiagnosticsFile);

        StartWatcher(repository, _ledger);

        // SystemEvents raise on their own thread; every handler marshals to
        // the dispatcher before touching a window.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // ThemePreference.System is the default, so without this an OS
        // light/dark flip does nothing until the next restart. SystemTheme.Changed
        // fires off the dispatcher, same as the watcher and SystemEvents handlers.
        _theme.Changed += OnSystemThemeChanged;

        SessionEnding += OnSessionEnding;

        StartShellServices(settings);

        _manager.RestoreOpenNotes();

        // Hide, not skip: the windows exist and isOpen stays set, so the tray
        // left-click brings back exactly the notes a normal launch would have
        // shown. Only a sign-in launch, because that is the one nobody asked
        // for at that moment; a manual launch means "I want my notes".
        if (settings.StartHidden
            && e.Args.Contains(StartupManager.StartupArgument, StringComparer.OrdinalIgnoreCase))
        {
            _manager.HideAll();
        }

        // Through the SAME parser the pipe uses. "Open with StickyMD" must not
        // behave one way with the app already up and another way with it down.
        // Skipped when there is nothing to apply: a bare first launch has
        // nothing to activate, it has just restored.
        if (e.Args.Length > 0) ApplyLaunchArgs(e.Args);

        if (_notesRootFailure is not null)
        {
            // Spec §8. The app is alive in the tray with no notes and no
            // folder to put one in; Settings is the only thing worth showing.
            OpenSettings();
            return;
        }

        // First run, or every note closed: give the user something. A new note
        // opens directly in edit mode with focus, per the spec. The tray means
        // running with zero notes is now a legitimate state rather than a dead
        // end, but a FIRST launch that puts nothing at all on the screen is
        // indistinguishable from one that failed.
        if (_manager.OpenPaths.Count == 0) _tray?.NewNote();
    }

    /// <summary>
    /// The tray, the hotkeys and the startup entry, in the order the tray needs
    /// them: it reads both of the others.
    /// </summary>
    private void StartShellServices(AppSettings settings)
    {
        _startup = new StartupManager(
            new RunKeyRegistry(),

            // ProcessPath, never Assembly.Location: for a single-file publish
            // that returns the extraction directory's .dll, and the Run key
            // would point at something Windows cannot launch. Null only for a
            // process with no executable image, which a WinExe is not.
            Environment.ProcessPath!,
            AppPaths.DiagnosticsFile);

        _hotkeys = new HotkeyManager(AppPaths.DiagnosticsFile);

        _tray = new TrayIconService(
            _manager!,
            _startup,
            OpenSettings,

            // A callback rather than a snapshot: the tray menu is rebuilt on
            // every open, and a Settings save re-registers both hotkeys, so
            // the list it flags has to be the CURRENT one.
            () => _hotkeys!.Failures,
            AppPaths.DiagnosticsFile);

        ApplyHotkeys(settings);
        ReportStartupStateToTray();
    }

    /// <summary>
    /// Registers both global hotkeys and balloons anything Windows refused.
    /// </summary>
    /// <remarks>
    /// Spec §7: "Registration failure names the conflicting combination in a
    /// tray balloon and flags it in Settings; the app keeps running." The
    /// balloon is here, the flag is <c>TrayIconService</c>'s warning item and
    /// <c>SettingsWindow.FlagFailedHotkeys</c>.
    ///
    /// New Note runs straight off the hotkey message rather than being
    /// dispatched, and that is deliberate: <c>NoteRepository.CreateNewRecorded</c>
    /// is documented NOT thread-safe and requires its callers to serialise, and
    /// <c>WM_HOTKEY</c> is dispatched on the UI thread, which satisfies that.
    /// </remarks>
    private void ApplyHotkeys(AppSettings settings)
    {
        if (_hotkeys is null || _manager is null) return;

        if (!settings.HotkeysEnabled)
        {
            // Apply with nothing: UnregisterAll releases both combinations
            // and clears Failures, so the tray warning item goes with them.
            _hotkeys.Apply([]);
            return;
        }

        _hotkeys.Apply(
        [
            // Through the tray, not straight to the manager: New Note can
            // legitimately fail -- an unplugged drive, a dropped share -- and
            // the tray is where the balloon that says so lives. Reaching the
            // manager directly would leave HotkeyManager's own guard to
            // swallow it into diagnostics.log, and a hotkey that quietly does
            // nothing is the failure "never die silently" forbids.
            (settings.NewNoteHotkey, () => _tray?.NewNote()),
            (settings.ShowHideHotkey, _manager.ToggleShowHideAll),
        ]);

        if (_hotkeys.Failures.Count == 0) return;

        _tray?.ShowBalloon(
            "StickyMD hotkeys",
            string.Join(
                Environment.NewLine,
                _hotkeys.Failures.Select(f => $"{f.Combination} {f.Reason}")),
            warning: true);
    }

    /// <summary>
    /// The quiet corrections already in
    /// <c>diagnostics.log</c>, said out loud once.
    /// </summary>
    /// <remarks>
    /// Only the two CORRUPT-file cases get a balloon. The per-field
    /// <c>LastLoadIssues</c> stay in the log: a clamped opacity is a correction
    /// the user will not miss, and popping a balloon for each one would train
    /// them to dismiss the balloon that matters.
    /// </remarks>
    private void ReportStartupStateToTray()
    {
        if (_tray is null) return;

        // No balloon for the notes-root failure: OnStartup opens Settings on a
        // banner that says the same thing at more length and cannot be missed,
        // and a second telling is a Windows toast that lands on the
        // notification area and covers the tray the user now has to reach.

        if (_settingsStore?.LastCorruptBackupPath is not null)
        {
            _tray.ShowBalloon(
                "StickyMD settings were reset",
                "settings.json could not be read and was kept alongside as .corrupt. "
                    + "Defaults are in use.",
                warning: true);
        }

        if (_indexStore?.LastCorruptBackupPath is not null)
        {
            // Worth being explicit that nothing was lost but geometry --
            // "notes.json.corrupt-1 appeared" otherwise reads as data loss.
            _tray.ShowBalloon(
                "StickyMD note positions were reset",
                "notes.json could not be read. Your .md files are untouched; "
                    + "only window positions and colours were lost.",
                warning: true);
        }
    }

    /// <summary>
    /// The tray's Settings item, the hotkey warning item, and spec §8's
    /// notes-root failure.
    /// </summary>
    private void OpenSettings()
    {
        if (_manager is null || _startup is null) return;

        if (_settingsWindow is not null)
        {
            // Already open. Activating beats opening a second one: two
            // non-modal Settings windows would each hold their own copy of the
            // settings and save over each other.
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _manager.Settings,
            _theme!.Resolve(_manager.Settings.Theme),
            _startup,
            _hotkeys?.Failures ?? [],
            _notesRootFailure,
            ApplySettings);

        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>
    /// Saves a settings change and makes it true of the running app. Returns
    /// null on success, or the message the Settings banner should show.
    /// </summary>
    /// <remarks>
    /// ORDER MATTERS. The new notes root is proved usable BEFORE settings.json
    /// is written, and settings.json is written before the running app adopts
    /// the change -- so a refused folder or a failed write leaves both the file
    /// and the app exactly as they were, with the reason on screen. The
    /// alternative is an app running on settings its own file does not hold.
    /// </remarks>
    private string? ApplySettings(AppSettings settings)
    {
        if (_manager is null || _settingsStore is null) return "StickyMD is still starting up.";

        var previousRoot = _manager.Settings.NotesRoot;
        var rootChanged = !NotePath.AreSame(previousRoot, settings.NotesRoot);

        try
        {
            new NoteRepository(settings.NotesRoot, new SystemClock()).EnsureRootExists();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"That folder could not be created or opened:\n\n{ex.Message}";
        }

        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"settings.json could not be written:\n\n{ex.Message}";
        }

        // Cleared only once the folder is proved good, so the banner does not
        // outlive the problem -- and stays if it was never fixed.
        _notesRootFailure = null;

        _manager.ApplySettings(settings);

        // The watcher is App's, not the manager's, so the manager cannot
        // repoint it. Without this the app would go on watching the old folder:
        // every external edit in the new notes root missed, and every edit in
        // the abandoned one reported against notes that are no longer there.
        if (rootChanged) StartWatcher(_manager.Repository, _ledger!);

        ApplyHotkeys(settings);

        return null;
    }

    private void StartWatcher(NoteRepository repository, IWriteLedger ledger)
    {
        // Disposed first: a settings change can call this a second time, and
        // two live watchers on two roots would each raise their own events into
        // the same handlers.
        _watcher?.Dispose();

        _watcher = new NoteWatcher(repository.NotesRoot, ledger);

        // Every one of these fires on a watcher thread. Marshalling is not
        // optional -- touching a Window off the dispatcher throws
        // InvalidOperationException, and from a timer callback the throw is
        // unhandled.
        _watcher.ExternalChanged += path => Dispatch(() => _manager?.OnExternalChanged(path));
        _watcher.Deleted += path => Dispatch(() => _manager?.OnDeleted(path));
        _watcher.Renamed += (from, to) => Dispatch(() => _manager?.OnRenamed(from, to));
        _watcher.Recovered += cause => Dispatch(() => _manager?.OnWatcherRecovered(cause));
    }

    /// <summary>
    /// Spec §7's launch commands: nothing activates, <c>--new</c> creates,
    /// anything else is a note to open. Used for this process's own command
    /// line and for one handed down the pipe by a launch that lost.
    /// </summary>
    private void ApplyLaunchArgs(IReadOnlyList<string> args)
    {
        if (_manager is null) return;

        if (args.Count == 0)
        {
            // Activate. With no taskbar button on any note, showing them IS
            // what "I am already here" looks like -- a silent no-op would be
            // indistinguishable from a launch that failed. ShowAll rather than
            // ToggleShowHideAll on purpose: a second launch means "come here",
            // never "go away".
            _manager.ShowAll();
            return;
        }

        foreach (var arg in args)
        {
            // Anything else beginning with "-" falls through ignored: that is
            // --startup. Passing those to OpenNote would refuse each one into
            // diagnostics.log as a missing file, and --startup needs no
            // handling anyway -- RestoreOpenNotes is unconditionally
            // ShowActivated=false, which is all it ever asked for. Unusable
            // paths ARE OpenNote's to refuse and log.
            if (arg.Equals("--new", StringComparison.OrdinalIgnoreCase)) _tray?.NewNote();
            else if (!arg.StartsWith('-')) _manager.OpenNote(arg);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatch(() => _manager?.OnDisplaySettingsChanged());

    private void OnSystemThemeChanged()
        => Dispatch(() => _manager?.OnSystemThemeChanged());

    private void Dispatch(Action action)
        => Dispatcher.BeginInvoke(action);

    /// <summary>
    /// The last resort for the UI thread. It exists to get dirty text onto
    /// disk before the process dies -- nothing else.
    /// </summary>
    private void OnDispatcherUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // Handled FIRST. Everything below is best-effort, and an exception
        // escaping this handler is the crash dialog with none of it done.
        e.Handled = true;

        DiagnosticsLog.Write(
            AppPaths.DiagnosticsFile,
            $"Unhandled exception on the UI thread: {e.Exception}");

        try
        {
            // THE REASON THIS HANDLER EXISTS. ShutdownWithoutClosingNotes
            // flushes every dirty buffer and lets SaveCoordinator write a
            // recovery snapshot for each one it cannot save -- and it leaves
            // isOpen alone, so a crash does not empty the desktop on the next
            // start. It is idempotent (it clears _windows), so OnExit's later
            // call is a safe no-op.
            _manager?.ShutdownWithoutClosingNotes();
        }
        catch (Exception flush)
        {
            // The flush is best-effort; exiting is not optional. A throw here
            // would put us back where we started.
            DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"The emergency flush itself failed: {flush}");
        }

        // Exit rather than continue. After an unhandled exception the app's
        // state is unknown, and carrying on risks writing that unknown state
        // over the user's files -- but exit THROUGH the flush above, not
        // through a crash.
        MessageBox.Show(
            "StickyMD hit an unexpected error and has to close.\n\n"
                + "Open notes were saved first. What happened is recorded in:\n\n"
                + AppPaths.DiagnosticsFile,
            "StickyMD", MessageBoxButton.OK, MessageBoxImage.Error);

        Shutdown();
    }

    /// <summary>
    /// The non-dispatcher threads. Log only, because nothing more is possible.
    /// </summary>
    /// <remarks>
    /// By the time this fires the CLR is already committed to terminating the
    /// process: it cannot be cancelled, and there is no guarantee the UI
    /// thread is alive to flush on. One line in diagnostics.log is the
    /// difference between a bug report and a mystery.
    /// </remarks>
    private static void OnDomainUnhandledException(
        object sender, UnhandledExceptionEventArgs e)
        => DiagnosticsLog.Write(
            AppPaths.DiagnosticsFile,
            $"Unhandled exception on a background thread (terminating: "
                + $"{e.IsTerminating}): {e.ExceptionObject}");

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        // Windows logoff and shutdown follow the SHUTDOWN path. isOpen must
        // survive, or logging off would empty the desktop on the next logon.
        _manager?.ShutdownWithoutClosingNotes();
    }

    private static void ReportStartupState(
        SettingsStore settingsStore, NoteIndexStore indexStore)
    {
        // "Never die silently" covers quiet recovery too. The log is the full
        // record; ReportStartupStateToTray balloons the two cases a user has to
        // know about.
        DiagnosticsLog.WriteAll(
            AppPaths.DiagnosticsFile,
            "settings.json corrections:",
            settingsStore.LastLoadIssues.Select(i => i.ToString()));

        DiagnosticsLog.WriteAll(
            AppPaths.DiagnosticsFile,
            "notes.json corrections:",
            indexStore.LastLoadIssues.Select(i => i.ToString()));

        if (settingsStore.LastCorruptBackupPath is { } settingsBackup)
        {
            DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"settings.json was unusable and was kept as {settingsBackup}; defaults are in use.");
        }

        if (indexStore.LastCorruptBackupPath is { } indexBackup)
        {
            // Notes are untouched -- only geometry is lost. Worth saying,
            // because "notes.json.corrupt-1 appeared" otherwise reads as data
            // loss.
            DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"notes.json was unusable and was kept as {indexBackup}. "
                    + "The .md files are untouched; only window positions were lost.");
        }
    }

    private void ShowRuntimeMissingDialog()
    {
        const string url = "https://developer.microsoft.com/microsoft-edge/webview2/";

        var answer = MessageBox.Show(
            "StickyMD needs the Microsoft Edge WebView2 runtime, which is not installed.\n\n"
                + "Open the download page?",
            "StickyMD", MessageBoxButton.OKCancel, MessageBoxImage.Error);

        if (answer == MessageBoxResult.OK)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception) { /* nothing more to offer */ }
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // ShutdownWithoutClosingNotes, NEVER CloseNote. Exiting must not clear
        // isOpen -- that is the failure the three-state model exists to
        // prevent.
        _manager?.ShutdownWithoutClosingNotes();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_theme is not null) _theme.Changed -= OnSystemThemeChanged;
        SessionEnding -= OnSessionEnding;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;

        // The tray icon FIRST: it is the only thing here with a presence
        // outside the process, and a dead icon lingers in the notification area
        // until something makes the shell re-poll.
        _tray?.Dispose();
        _hotkeys?.Dispose();

        _watcher?.Dispose();
        _theme?.Dispose();
        _manager?.Dispose();
        _instance?.Dispose();

        base.OnExit(e);
    }
}
