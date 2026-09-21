using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using StickyMD.App.Interop;
using StickyMD.Core.Diagnostics;

namespace StickyMD.App.Services;

/// <summary>
/// The system tray icon and its menu, per spec §7.
/// </summary>
/// <remarks>
/// WHY THIS IS THE KEYSTONE OF PLAN C, and not decoration. Before it existed
/// the app had no visible presence at all -- <c>ShowInTaskbar="False"</c> on
/// every note and <c>ShutdownMode=OnExplicitShutdown</c> meant that closing the
/// last note stranded the process with no reachable Exit and no way to make a
/// new note, so the only way out was Task Manager, which skips <c>OnExit</c>
/// and loses that session's geometry. That is also why the temporary
/// <c>Ctrl+Shift+Alt+Q</c>/<c>N</c> keys existed on <c>NoteWindow</c>, and why
/// they are gone in the same change that added this.
///
/// H.NotifyIcon.Wpf, per spec §7 and §10, rather than
/// <c>System.Windows.Forms.NotifyIcon</c>. WinForms ships in the same
/// <c>Microsoft.WindowsDesktop.App</c> runtime the app already uses, so it
/// would have cost no download -- but it costs
/// <c>&lt;UseWindowsForms&gt;true&lt;/UseWindowsForms&gt;</c> in two projects
/// plus a <c>&lt;Using Remove&gt;</c> in each to stop the implicit-usings clash
/// on <c>MessageBox</c> and <c>Application</c> from failing a build that treats
/// warnings as errors, and it would put a WinForms-rendered menu next to the
/// WPF <c>⋯</c> menu on the same notes. The decision was re-made here on
/// purpose rather than inherited; the spec's reasoning held.
///
/// THE MENU IS REBUILT EVERY TIME IT OPENS. Recent Notes, its check marks and
/// the Launch at Startup tick are all live state -- the startup tick is read
/// back out of the registry each time, per spec §7, because cleanup tools
/// strip those entries behind the app's back.
/// </remarks>
public sealed class TrayIconService : IDisposable
{
    private readonly WindowManager _manager;
    private readonly StartupManager _startup;
    private readonly Action _openSettings;
    private readonly Func<IReadOnlyList<HotkeyFailure>> _hotkeyFailures;
    private readonly string _diagnosticsFile;
    private readonly TaskbarIcon _icon;

    private bool _disposed;

    public TrayIconService(
        WindowManager manager,
        StartupManager startup,
        Action openSettings,
        Func<IReadOnlyList<HotkeyFailure>> hotkeyFailures,
        string diagnosticsFile)
    {
        _manager = manager;
        _startup = startup;
        _openSettings = openSettings;
        _hotkeyFailures = hotkeyFailures;
        _diagnosticsFile = diagnosticsFile;

        _icon = new TaskbarIcon
        {
            Icon = LoadTrayIcon(diagnosticsFile),
            ToolTipText = "StickyMD",

            // The UIA name a script or a screen reader finds the icon by. Left
            // as the product name deliberately: it is the only handle the
            // verification harness has on this icon.
            CustomName = "StickyMD",

            // Spec §7: "Left-click toggles Show All / Hide All." NoLeftClickDelay
            // skips the double-click wait, which otherwise makes a single click
            // feel like it did not register.
            NoLeftClickDelay = true,
        };

        _icon.TrayLeftMouseUp += (_, _) => _manager.ToggleShowHideAll();

        // Rebuilt in the PREVIEW, which H.NotifyIcon raises before it reads
        // ContextMenu and opens it -- so a menu assigned here is the one that
        // shows. Rebuilding after the popup was already open would show one
        // right-click's worth of stale check marks.
        _icon.PreviewTrayContextMenuOpen += (_, _) => _icon.ContextMenu = BuildMenu();

        // Required because this icon is created in code rather than declared in
        // XAML: without it the TaskbarIcon never reaches the loaded state that
        // adds it to the notification area. Efficiency mode is turned OFF --
        // its default is on, and it applies EcoQoS process throttling to an app
        // that hosts a WebView2 per note.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>
    /// Spec §8's tray balloon. Used for a corrupt <c>notes.json</c> or
    /// <c>settings.json</c>, a hotkey conflict, and a notes root that could not
    /// be created.
    /// </summary>
    public void ShowBalloon(string title, string message, bool warning = false)
    {
        try
        {
            _icon.ShowNotification(
                title,
                message,
                warning ? NotificationIcon.Warning : NotificationIcon.Info);
        }
        catch (Exception ex)
        {
            // A balloon is how the app says something went wrong. It must not
            // itself become the thing that goes wrong -- Windows can refuse a
            // notification (Focus Assist, a notification-area shell that has
            // just restarted), and the message is already in diagnostics.log.
            DiagnosticsLog.Write(
                _diagnosticsFile, $"A tray balloon could not be shown ({title}): {ex.Message}");
        }
    }

    /// <summary>
    /// The spec's menu, verbatim and in order: New Note, Recent Notes ▸,
    /// Open Note…, Show All, Hide All, ─, Settings, Launch at Startup ☑, ─,
    /// Exit.
    /// </summary>
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(Item("New Note", NewNote));
        menu.Items.Add(RecentNotesItem());
        menu.Items.Add(Item("Open Note…", OpenNoteDialog));
        menu.Items.Add(Item("Show All", _manager.ShowAll));
        menu.Items.Add(Item("Hide All", _manager.HideAll));

        menu.Items.Add(new Separator());

        menu.Items.Add(Item("Settings", _openSettings));

        var startup = new MenuItem
        {
            Header = "Launch at Startup",
            IsCheckable = true,

            // Read from the registry on every open, per spec §7. settings.json
            // deliberately has no launchAtStartup flag to cache it in.
            IsChecked = _startup.IsEnabled,
        };

        startup.Click += (_, _) => ToggleStartup(startup);
        menu.Items.Add(startup);

        var failures = _hotkeyFailures();
        if (failures.Count > 0)
        {
            // Spec §7: a registration failure is "flagged in Settings". This is
            // the second half of that -- the balloon at startup is gone by the
            // time anyone goes looking, and a menu that quietly lists working
            // hotkeys next to broken ones tells the user nothing.
            menu.Items.Add(new Separator());
            menu.Items.Add(HotkeyWarningItem(failures));
        }

        menu.Items.Add(new Separator());

        // Shutdown, not Environment.Exit: OnExit is what flushes every dirty
        // buffer, harvests geometry and leaves isOpen alone.
        menu.Items.Add(Item("Exit", () => Application.Current.Shutdown()));

        return menu;
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <remarks>
    /// A submenu with no children cannot be opened, so an empty Recent list
    /// would be an item that looks live and does nothing. Disabled with a
    /// placeholder child instead, which says what is going on.
    /// </remarks>
    private MenuItem RecentNotesItem()
    {
        var recent = new MenuItem { Header = "Recent Notes" };
        var notes = _manager.RecentNotes();

        if (notes.Count == 0)
        {
            recent.Items.Add(new MenuItem { Header = "No notes yet", IsEnabled = false });
            return recent;
        }

        foreach (var note in notes)
        {
            var path = note.Path;

            // OpenNote is idempotent per path and focuses an existing window,
            // so clicking a ticked entry brings that note forward rather than
            // doing nothing.
            var item = Item(note.Title, () => _manager.OpenNote(path));

            // The full path as the tooltip, because the title is a heading and
            // two notes can legitimately share one.
            item.ToolTip = path;

            // Ticked means "has a window right now", NOT the index's isOpen --
            // that is true for every note the user has ever opened, so ticking
            // it would tick the whole list.
            //
            // IsCheckable as well as IsChecked, even though the tick is a
            // REPORT rather than a control. WPF's MenuItem automation peer only
            // exposes TogglePattern for a checkable item, so without this the
            // check mark is painted on screen and invisible to UI Automation --
            // which means invisible to a screen reader, and unverifiable by
            // scripts/verify-smoke-ui.ps1. The side effect is that a click
            // toggles the mark as well as opening the note, and that is
            // unobservable: the click closes the menu, and the next open
            // rebuilds every item from live state.
            item.IsCheckable = true;
            item.IsChecked = note.IsOpen;

            recent.Items.Add(item);
        }

        return recent;
    }

    private MenuItem HotkeyWarningItem(IReadOnlyList<HotkeyFailure> failures)
    {
        var item = new MenuItem
        {
            Header = failures.Count == 1
                ? $"⚠ {failures[0].Combination} is unavailable"
                : $"⚠ {failures.Count} hotkeys are unavailable",
            ToolTip = string.Join(
                Environment.NewLine, failures.Select(f => $"{f.Combination} {f.Reason}")),
        };

        // Opens Settings, which is the only place the combination can be
        // changed. A warning the user cannot act on is just noise.
        item.Click += (_, _) => _openSettings();
        return item;
    }

    private void ToggleStartup(MenuItem item)
    {
        // IsCheckable flips IsChecked before Click runs, so this is the state
        // the user just asked for.
        var wanted = item.IsChecked;

        if (_startup.TrySet(wanted)) return;

        // Put the tick back where the registry actually is, then say so. A
        // checkbox left showing a state the machine does not hold is the
        // failure spec §7 is guarding against when it makes the registry the
        // single source of truth.
        item.IsChecked = _startup.IsEnabled;

        ShowBalloon(
            "StickyMD",
            "Launch at Startup could not be changed. Windows refused the registry write; "
                + "the details are in diagnostics.log.",
            warning: true);
    }

    /// <summary>
    /// New Note, from the menu or from the hotkey, saying so when the notes
    /// root will not take one.
    /// </summary>
    /// <remarks>
    /// The single place New Note is asked for, so the failure is reported once
    /// rather than at each entry point. Deliberately does NOT open Settings: a
    /// notes root on a network share that is momentarily down would then pop a
    /// window every time the user pressed the hotkey. The balloon names the
    /// folder and Settings is one item away on this same menu.
    /// </remarks>
    public void NewNote()
    {
        if (_manager.CreateAndOpenNote() is not null) return;

        ShowBalloon(
            "StickyMD could not create a note",
            $"The notes folder is not usable right now:{Environment.NewLine}"
                + $"{_manager.Settings.NotesRoot}{Environment.NewLine}{Environment.NewLine}"
                + "Nothing was lost. Choose another folder in Settings, or reconnect this one.",
            warning: true);
    }

    /// <summary>
    /// Spec §7's <c>Open Note…</c>.
    /// </summary>
    /// <remarks>
    /// NOT decoration, and the reason it is in the menu at all: a note whose
    /// <c>notes.json</c> entry is lost -- a corrupt index, a hand-edit, a note
    /// that lives outside the notes root -- is otherwise UNREACHABLE. Nothing
    /// else in the app opens a note by path.
    /// </remarks>
    private void OpenNoteDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open note",
            Filter = "Markdown notes (*.md)|*.md|All files (*.*)|*.*",
            DefaultExt = ".md",

            // Spec §5: "Open Note… defaults to the configured notes root."
            InitialDirectory = Directory.Exists(_manager.Settings.NotesRoot)
                ? _manager.Settings.NotesRoot
                : string.Empty,
        };

        if (dialog.ShowDialog() == true) _manager.OpenNote(dialog.FileName);
    }

    /// <summary>
    /// The exe's own icon, at the size the notification area actually asks for.
    /// </summary>
    /// <remarks>
    /// <c>SM_CXSMICON</c>, not a hardcoded 16: it is 16 at 100% and 24 at 150%,
    /// and a .ico with several frames only looks right if the correct frame is
    /// picked. <c>System.Drawing.Icon(Stream, int, int)</c> chooses the
    /// nearest-matching frame rather than rescaling the largest one.
    ///
    /// Same file as <c>ApplicationIcon</c>, built from NotePalette's
    /// Yellow/Light row, so the tray icon and a default note are the same
    /// yellow.
    /// </remarks>
    private static System.Drawing.Icon? LoadTrayIcon(string diagnosticsFile)
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/stickymd.ico");
            using var stream = Application.GetResourceStream(uri)!.Stream;

            return new System.Drawing.Icon(
                stream,
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON));
        }
        catch (Exception ex)
        {
            // A tray icon with no image is still a working tray icon: it has a
            // tooltip, a menu, and the left-click toggle. Refusing to start
            // over a missing image would take the app's only Exit with it.
            DiagnosticsLog.Write(
                diagnosticsFile, $"The tray icon image could not be loaded: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Removes the icon from the notification area. Without it Windows
        // leaves a dead icon behind until something makes the shell re-poll.
        _icon.Dispose();
    }
}
