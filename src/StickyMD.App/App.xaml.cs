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
    private WindowManager? _manager;
    private NoteWatcher? _watcher;
    private SystemTheme? _theme;

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

        // Check the runtime FIRST. Without it a note window opens and simply
        // never paints, which looks like a bug in the window rather than a
        // missing dependency.
        if (WebViewEnvironment.DetectRuntimeVersion() is null)
        {
            ShowRuntimeMissingDialog();
            return;
        }

        var settingsStore = new SettingsStore(AppPaths.SettingsFile);
        var settings = settingsStore.Load();

        var indexStore = new NoteIndexStore(AppPaths.NoteIndexFile);

        // Load the index with the validated settings, so an entry with an
        // unusable colour or size falls back to the user's defaults rather
        // than to the type's.
        _ = indexStore.Load(settings);

        ReportStartupState(settingsStore, indexStore);

        var repository = new NoteRepository(settings.NotesRoot, new SystemClock());

        try
        {
            repository.EnsureRootExists();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Exiting is correct here, not a fallback: with no notes root
            // there is nothing to show and no Settings window until Plan C to
            // point the user elsewhere. "Never die silently" is satisfied by
            // saying why BEFORE exiting and recording it -- not by staying up
            // with nothing to display. Plan C replaces this Shutdown() with
            // Settings opened on a banner.
            DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"The notes root '{settings.NotesRoot}' could not be created -- {ex.Message}");

            MessageBox.Show(
                $"StickyMD could not create its notes folder:\n\n{settings.NotesRoot}\n\n{ex.Message}",
                "StickyMD", MessageBoxButton.OK, MessageBoxImage.Warning);

            Shutdown();
            return;
        }

        // One shared WriteLedger for every note is what makes self-write
        // suppression work; a per-window ledger makes every save look external
        // and the note reloads in a loop. Built BEFORE the factory, which takes
        // it and passes it through to every NoteWindow it creates.
        var ledger = new WriteLedger();
        _theme = new SystemTheme();

        _manager = new WindowManager(
            repository,
            indexStore,
            settingsStore,
            new NoteWindowFactory(ledger),
            new MonitorEnumerator(),
            _theme,
            new RecycleBinService(),
            ledger,
            new RecoveryStore(AppPaths.RecoveryDir),
            AppPaths.DiagnosticsFile);

        StartWatcher(repository, ledger);

        // SystemEvents raise on their own thread; every handler marshals to
        // the dispatcher before touching a window.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // ThemePreference.System is the default, so without this an OS
        // light/dark flip does nothing until the next restart. SystemTheme.Changed
        // fires off the dispatcher, same as the watcher and SystemEvents handlers.
        _theme.Changed += OnSystemThemeChanged;

        SessionEnding += OnSessionEnding;

        _manager.RestoreOpenNotes();

        // First run, or every note closed: give the user something. A new note
        // opens directly in edit mode with focus, per the spec.
        if (_manager.OpenPaths.Count == 0) _manager.CreateAndOpenNote();
    }

    private void StartWatcher(NoteRepository repository, IWriteLedger ledger)
    {
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
        // "Never die silently" covers quiet recovery too. Plan C turns these
        // into tray balloons; Plan B's obligation is that they exist on disk
        // rather than in nobody's hands.
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

        _watcher?.Dispose();
        _theme?.Dispose();
        _manager?.Dispose();

        base.OnExit(e);
    }
}
