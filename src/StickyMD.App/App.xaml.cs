using System.Windows;
using StickyMD.App.Services;
using StickyMD.App.Windows;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App;

public partial class App : Application
{
    // TEMPORARY (Plan B only). Task 14 replaces all of this with the real
    // bootstrap: settings, index, watcher, WindowManager, recovery.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (WebViewEnvironment.DetectRuntimeVersion() is null)
        {
            MessageBox.Show(
                "The WebView2 runtime is not installed.",
                "StickyMD", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var repository = new NoteRepository(
            AppSettings.DefaultNotesRoot, new SystemClock());
        repository.EnsureRootExists();

        var existing = repository.EnumerateRoot();
        var path = existing.Count > 0 ? existing[0] : repository.CreateNewRecorded().Path;

        var state = new NoteState(
            X: 200, Y: 200, W: 320, H: 420,
            Monitor: null,
            Color: NoteColor.Yellow,
            Opacity: 0.95,
            AlwaysOnTop: true,
            IsOpen: true,
            LastOpenedUtc: DateTime.UtcNow);

        // TEMPORARY (Plan B only). One ledger instance shared by every note is
        // what makes self-write suppression work at all -- Task 13's
        // NoteWindowFactory takes it in its own constructor and passes it
        // through instead of each window creating its own.
        var ledger = new WriteLedger();

        var window = new NoteWindow(
            path, state, NotePalette.Get(state.Color, StickyMD.Core.Theming.ThemeMode.Light), ledger);

        window.CloseRequested += _ => Shutdown();

        window.ShowNote(activate: true);
    }
}
