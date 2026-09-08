using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Wpf;
using StickyMD.App.Interop;
using StickyMD.App.Services;
using StickyMD.Core.Editing;
using StickyMD.Core.Geometry;
using StickyMD.Core.Markdown;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Windows;

/// <summary>
/// The seam <c>WindowManager</c> talks to. A WPF <c>Window</c> cannot be
/// constructed on an xUnit thread, and the three-state model this drives is
/// the one piece of window logic that MUST be tested -- the spec is explicit
/// that getting it wrong restores zero notes after an exit.
/// </summary>
public interface INoteWindow : IDisposable
{
    string NotePath { get; }

    /// <summary>Physical screen pixels, read straight from the OS.</summary>
    PixelRect Bounds { get; }

    void ShowNote(bool activate);
    void HideNote();
    void FocusNote();

    /// <summary>
    /// Spec §362: a newly created empty note opens directly in edit mode with
    /// focus. Only <c>WindowManager.CreateAndOpenNote</c> calls this --
    /// opening an EXISTING note must stay in preview.
    /// </summary>
    void EnterEditMode();

    void ApplyState(NoteState state, NoteTheme theme);

    /// <summary>An external edit arrived and the buffer was clean.</summary>
    void ApplyExternalContent(NoteContent content);

    void NotifyFileDeleted();
    void NotifyRenamed(string canonicalPath);
    void SaveNow();

    /// <summary>
    /// Give up this path: stop every save that happens WITHOUT the user asking.
    /// </summary>
    /// <remarks>
    /// For a window displaced by a rename collision, which is detached from
    /// WindowManager's map but still alive and still holding its text. Its
    /// autosave timer and its LostFocus handler do not know that, and an
    /// already-armed tick writes the displaced buffer over the file that was
    /// just renamed into place -- with no user action at all. The buffer is
    /// deliberately NOT cleared: the text stays visible behind the
    /// "file is gone" bar, and that bar's Recreate still writes it, because an
    /// explicit click that says what it will do is a choice rather than a race.
    /// </remarks>
    void StopAutomaticSaves();

    /// <summary>A recovery snapshot survived to this startup.</summary>
    void ShowRecovered(RecoveryEnvelope envelope);

    /// <summary>
    /// The user clicked the close glyph. Takes the note off the screen;
    /// <c>isOpen</c> survives, so it returns on the next launch.
    /// </summary>
    event Action<string>? CloseRequested;

    event Action<string>? DeleteRequested;

    /// <summary>Canonical current path, requested new file name.</summary>
    event Action<string, string>? RenameRequested;

    /// <summary>A relative .md link was clicked. Canonical target path.</summary>
    event Action<string>? OpenNoteRequested;

    /// <summary>Colour, opacity, pin, or geometry changed.</summary>
    event Action<string, NoteState>? StateChanged;
}

public sealed partial class NoteWindow : Window, INoteWindow
{
    private readonly MarkdownRenderer _renderer = new();
    private readonly InlineBarHost _bars;
    private readonly SaveCoordinator _saves;
    private readonly System.Windows.Threading.DispatcherTimer _autosave = new()
    {
        // Spec: 500ms after the last keystroke.
        Interval = TimeSpan.FromMilliseconds(500),
    };

    /// <summary>
    /// Guards against the shell never posting "ready". Without this,
    /// <see cref="WebViewHost"/> queues a render in _pendingRender forever and
    /// the note shows an empty page with no timeout and no explanation --
    /// "never die silently" forbids that. Generous on purpose: 10s is far
    /// beyond any ordinary shell load, so it should only ever fire on a
    /// genuinely stuck shell.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _viewerStall = new()
    {
        Interval = TimeSpan.FromSeconds(10),
    };

    private WebViewHost _web;
    private NoteState _state;
    private NoteTheme _theme;
    private StickyMD.Core.Theming.ThemeMode _resolvedMode = StickyMD.Core.Theming.ThemeMode.Light;
    private string _buffer = string.Empty;
    private bool _webReady;
    private bool _disposed;
    private bool _editing;

    /// <summary>
    /// The file exists but could not be READ -- an exclusive lock from another
    /// editor, an AV scan, OneDrive, a share hiccup.
    /// </summary>
    /// <remarks>
    /// THIS FLAG IS WHAT STOPS THE FILE BEING TRUNCATED. The note's real
    /// content is still on disk while <c>_buffer</c> is empty, so an
    /// apparently blank note that accepts one keystroke would 500ms later
    /// autosave that one character over the whole file -- and
    /// <c>_saves.AdoptFromDisk</c> never ran, so a CRLF or BOM note would lose
    /// its format with it. The bar and <c>Editor.IsReadOnly</c> explain the
    /// state; only this gate makes writing impossible until a read succeeds.
    /// </remarks>
    private bool _loadFailed;

    /// <summary>
    /// This window was displaced by a rename and no longer owns
    /// <see cref="NotePath"/>. Set by <see cref="StopAutomaticSaves"/>, and
    /// cleared only by an explicit Recreate: the user re-taking the path.
    /// </summary>
    private bool _savingStopped;

    /// <summary>
    /// What the WebView shell was last BUILT with -- not what this window
    /// currently wants.
    /// </summary>
    /// <remarks>
    /// <see cref="ApplyState"/> compares against these before re-navigating.
    /// Windows raises <c>UserPreferenceChanged</c> for accent colour, wallpaper
    /// and a broad slice of <c>WM_SETTINGCHANGE</c> traffic, not only for
    /// light/dark, and every one of those used to reach here and rebuild the
    /// page: every note on the desktop flashes and re-renders on an accent
    /// colour change. Null until the first shell is built.
    /// </remarks>
    private NoteTheme? _shellTheme;
    private bool _shellAllowRemoteImages;

    // Default false preserves this task's behaviour exactly. Task 12 flips
    // this for the per-note "Load remote images" bar; declaring and reading
    // it from the start means that task changes behaviour rather than
    // introducing a field.
    private bool _allowRemoteImages = false;

    /// <summary>
    /// Spec §8. Markdig plus a DOM swap on a multi-megabyte document freezes
    /// the note for seconds; edit mode is a plain TextBox and stays usable.
    /// </summary>
    private const int PreviewSizeLimitBytes = 2 * 1024 * 1024;

    private bool ExceedsPreviewLimit
        => System.Text.Encoding.UTF8.GetByteCount(_buffer) > PreviewSizeLimitBytes;

    public NoteWindow(string canonicalPath, NoteState state, NoteTheme theme, IWriteLedger ledger)
    {
        InitializeComponent();

        NotePath = canonicalPath;
        _state = state;
        _theme = theme;

        _bars = new InlineBarHost(BarStack);
        _web = new WebViewHost(canonicalPath);

        WebViewSlot.Content = _web.Control;

        _saves = new SaveCoordinator(
            canonicalPath,
            new NoteFileGateway(),
            ledger,
            new RecoveryStore(AppPaths.RecoveryDir),
            AppPaths.DiagnosticsFile);

        // Saved can fire off the UI thread: FlushAsync awaits with
        // ConfigureAwait(false) throughout, so it has no reason to resume on
        // the dispatcher. Marshal here so OnSaved -- extended in Task 12 to
        // show bars -- can stay UI-thread-safe without every caller having to
        // know that.
        //
        // MUST BE BeginInvoke, NEVER Invoke. SaveNow() blocks the UI thread
        // synchronously on FlushAsync().GetAwaiter().GetResult(). If FlushAsync
        // genuinely suspends there (waiting on the save gate, or during a
        // retry's Task.Delay) its continuation resumes on a ThreadPool thread
        // -- ConfigureAwait(false) throughout the coordinator guarantees that
        // -- and Report() invokes Saved from there, synchronously, before the
        // awaited Task completes. A blocking Dispatcher.Invoke at that point
        // waits for the UI thread to pump, but the UI thread is the one
        // blocked in SaveNow and is not pumping: deadlock. BeginInvoke queues
        // the call and returns immediately, so the ThreadPool continuation can
        // finish and let SaveNow's blocking wait complete. During application
        // shutdown a queued BeginInvoke callback may never run -- the save has
        // already happened by then, and there is no UI left to show it on, so
        // a bar silently not appearing at exit is correct, not a bug.
        _saves.Saved += outcome =>
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnSaved(outcome)); return; }
            OnSaved(outcome);
        };

        // Through FlushAsync, never _saves.FlushAsync directly: FlushAsync is
        // where the "the file was never read" gate lives, and a debounce tick
        // that went straight to the coordinator would walk straight past it.
        _autosave.Tick += async (_, _) => await FlushAsync().ConfigureAwait(true);
        _viewerStall.Tick += (_, _) => { _viewerStall.Stop(); ShowViewerStalled(); };
        Editor.TextChanged += OnEditorTextChanged;
        Editor.LostFocus += async (_, _) => await FlushAsync().ConfigureAwait(true);
        Editor.PreviewKeyDown += OnEditorPreviewKeyDown;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        EditButton.Click += (_, _) => ToggleEditMode();

        WireHeader();
        ApplyTheme(theme);

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    public string NotePath { get; private set; }

    public PixelRect Bounds => _handle == IntPtr.Zero
        ? new PixelRect(_state.X, _state.Y, _state.W, _state.H)
        : WindowGeometry.GetBounds(_handle);

    public event Action<string>? CloseRequested;
    public event Action<string>? DeleteRequested;
    public event Action<string, string>? RenameRequested;
    public event Action<string>? OpenNoteRequested;

    // `new`: System.Windows.Window already declares a parameterless
    // StateChanged event (window minimize/maximize/restore). This one is
    // INoteWindow's, with a different signature, and deliberately shadows it
    // -- nothing in this class means to observe the base Window's version.
    public new event Action<string, NoteState>? StateChanged;

    // TEMPORARY (Plan B only). Removed in Plan C, which gives the tray menu
    // New Note. Not on INoteWindow -- WindowManagerTests drives the manager
    // through fakes and has no reason to know about this key.
    public event Action? NewNoteRequested;

    private IntPtr _handle = IntPtr.Zero;

    private void WireHeader()
    {
        // ============================================================
        // THE DRAG HANDLER IS ON THE HEADER ELEMENT. NEVER ON THE WINDOW.
        //
        // MouseLeftButtonDown BUBBLES. A handler on the Window calls
        // DragMove() for every left-click anywhere, INCLUDING over the note
        // content, and swallows the mouse-down before the WebView receives it.
        //
        // Observed symptoms, none of which name the cause:
        //   - task checkboxes silently stop toggling (onclick never fires)
        //   - scrollbar thumb drags move the window instead of scrolling
        //   - text selection inside a note becomes impossible
        //
        // There is no exception, no warning, and no crash. The note simply
        // stops responding to clicks. Spike 0 initially misattributed this to
        // the composition control.
        // ============================================================
        Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;

            try { DragMove(); }
            catch (InvalidOperationException)
            {
                // Raised when the button was already released. Harmless.
            }
        };

        Header.MouseEnter += (_, _) => FadeHeaderButtons(1.0);
        Header.MouseLeave += (_, _) => FadeHeaderButtons(RestingGlyphOpacity);

        CloseButton.Click += (_, _) => CloseRequested?.Invoke(NotePath);

        PinButton.Click += (_, _) =>
        {
            _state = _state with { AlwaysOnTop = !_state.AlwaysOnTop };
            Topmost = _state.AlwaysOnTop;
            StateChanged?.Invoke(NotePath, CurrentState());
        };

        MoreButton.Click += (_, _) => ShowMoreMenu(MoreButton);

        // The colour glyph opens COLOURS, not the whole menu. Both buttons
        // called ShowMoreMenu until the app was first run by hand, so the two
        // glyphs did exactly the same thing and the colour one was decoration.
        ColorButton.Click += (_, _) => ShowColourMenu(ColorButton);
    }

    /// <summary>
    /// What the header glyphs fade back to when the mouse leaves. NOT zero:
    /// invisible controls are undiscoverable, and the first person to run the
    /// app could not find them. Kept in step with HeaderButtons' Opacity in
    /// NoteWindow.xaml.
    /// </summary>
    private const double RestingGlyphOpacity = 0.45;

    private void FadeHeaderButtons(double to)
        => HeaderButtons.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)));

    /// <summary>
    /// The spec's menu, verbatim: Rename…, Color ▸, Opacity ▸, Always on Top ☑,
    /// separator, Delete. Every entry maps to a v1 feature.
    /// </summary>
    private void ShowMoreMenu(System.Windows.Controls.Button anchor)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var rename = new System.Windows.Controls.MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => PromptRename();
        menu.Items.Add(rename);

        var colours = new System.Windows.Controls.MenuItem { Header = "Color" };
        colours.Items.Add(SwatchRowItem(() => menu.IsOpen = false));
        menu.Items.Add(colours);

        var opacity = new System.Windows.Controls.MenuItem { Header = "Opacity" };
        opacity.Items.Add(OpacitySliderItem());
        menu.Items.Add(opacity);

        var pin = new System.Windows.Controls.MenuItem
        {
            Header = "Always on Top",
            IsCheckable = true,
            IsChecked = _state.AlwaysOnTop,
        };
        pin.Click += (_, _) =>
        {
            _state = _state with { AlwaysOnTop = !_state.AlwaysOnTop };
            Topmost = _state.AlwaysOnTop;
            StateChanged?.Invoke(NotePath, CurrentState());
        };
        menu.Items.Add(pin);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var delete = new System.Windows.Controls.MenuItem { Header = "Delete" };
        delete.Click += (_, _) => ConfirmDelete();
        menu.Items.Add(delete);

        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Colours only, hung off the colour glyph. Same swatch row the more
    /// menu's Color submenu uses, so the two can never drift apart.
    /// </summary>
    private void ShowColourMenu(System.Windows.Controls.Button anchor)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(SwatchRowItem(() => menu.IsOpen = false));

        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// The seven palette colours as a row of clickable swatches.
    /// </summary>
    /// <remarks>
    /// They were a list of colour NAMES until the app was first run by hand.
    /// A text list is the wrong control for choosing a colour -- you cannot
    /// see what you are picking -- and it read as a bug rather than a design.
    ///
    /// Each swatch paints the note's CONTENT background, not its chrome: that
    /// is the large surface the user will actually be looking at. Both come
    /// from NotePalette.Get for the resolved mode, so a swatch is showing the
    /// real colour rather than a hardcoded approximation of it, and the chrome
    /// and the rendered HTML still agree because nothing here invents a value.
    ///
    /// StaysOpenOnClick is set because the click is handled by a child Border,
    /// not by the MenuItem -- without it WPF closes the menu on mouse-down and
    /// the Border never sees the mouse-up.
    /// </remarks>
    private System.Windows.Controls.MenuItem SwatchRowItem(Action close)
    {
        var row = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(2),
        };

        foreach (var colour in NotePalette.All)
        {
            var theme = NotePalette.Get(colour, _resolvedMode);
            var selected = colour == _state.Color;

            var swatch = new System.Windows.Controls.Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Parse(theme.ContentBg)),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = new SolidColorBrush(
                    Parse(selected ? theme.Accent : theme.Border)),
                Cursor = Cursors.Hand,
                ToolTip = colour.ToString(),
            };

            var chosen = colour;

            swatch.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ApplyColour(chosen);
                close();
            };

            row.Children.Add(swatch);
        }

        return new System.Windows.Controls.MenuItem
        {
            Header = row,
            StaysOpenOnClick = true,
        };
    }

    /// <summary>
    /// Opacity as a slider rather than five fixed steps.
    /// </summary>
    /// <remarks>
    /// The floor is 30, ABOVE StateValidator.MinOpacity's 0.20, for the same
    /// reason that floor exists: below roughly 20% a note is invisible and
    /// cannot be found with the mouse to be fixed, so a slider that reached
    /// zero would let the user build a state they cannot get out of.
    ///
    /// Applied live on ValueChanged, because the whole point of a slider is
    /// seeing the result while you drag. Window.Opacity is cheap to set;
    /// StateChanged goes to the index each time, which is one small JSON write
    /// per drag notch and has not been worth debouncing.
    /// </remarks>
    private System.Windows.Controls.MenuItem OpacitySliderItem()
    {
        var readout = new System.Windows.Controls.TextBlock
        {
            Width = 34,
            VerticalAlignment = VerticalAlignment.Center,
            Text = $"{_state.Opacity * 100:0}%",
        };

        var slider = new System.Windows.Controls.Slider
        {
            Minimum = 30,
            Maximum = 100,
            Value = Math.Clamp(_state.Opacity * 100, 30, 100),
            Width = 140,
            TickFrequency = 5,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        slider.ValueChanged += (_, e) =>
        {
            readout.Text = $"{e.NewValue:0}%";
            ApplyOpacity(e.NewValue / 100.0);
        };

        var row = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(6, 2, 6, 2),
        };

        row.Children.Add(slider);
        row.Children.Add(readout);

        return new System.Windows.Controls.MenuItem
        {
            Header = row,
            StaysOpenOnClick = true,
        };
    }

    private void ApplyColour(NoteColor colour)
    {
        _state = _state with { Color = colour };

        var theme = NotePalette.Get(colour, _resolvedMode);
        ApplyTheme(theme);

        // The WebView needs a fresh shell for the new CSS variables and a new
        // opaque backdrop, or the note's chrome and its content disagree about
        // what this colour is. Guarded like ApplyState's identical call: the
        // shell may not have finished its first InitializeAsync yet.
        if (_webReady) _ = ReloadShellAsync();

        StateChanged?.Invoke(NotePath, CurrentState());
    }

    private void ApplyOpacity(double value)
    {
        _state = _state with { Opacity = value };

        // Window.Opacity, never SetLayeredWindowAttributes. WS_EX_LAYERED
        // cannot be added post-creation on this platform, and WPF has already
        // applied it at CreateWindowEx because AllowsTransparency is True.
        Opacity = value;

        StateChanged?.Invoke(NotePath, CurrentState());
    }

    /// <summary>
    /// <c>_state</c> with the window's LIVE rect stamped onto it.
    /// </summary>
    /// <remarks>
    /// EVERY StateChanged raise must go through this. WindowManager.Persist
    /// replaces the whole index entry, not just the field that changed, and
    /// _state's X/Y/W/H are only as fresh as the last event that happened to
    /// write them -- so a colour, opacity or pin change built straight from
    /// _state persists the geometry the note had when it opened and silently
    /// reverts every move the user has made since. Reading Bounds at the
    /// moment of the event is enough; no LocationChanged/SizeChanged
    /// subscription is needed, and that matches how the rest of the manager
    /// reads geometry.
    /// </remarks>
    private NoteState CurrentState()
    {
        if (_handle == IntPtr.Zero) return _state;

        var bounds = WindowGeometry.GetBounds(_handle);

        // A zero rect means GetWindowRect failed or the window is not realised
        // yet. Keeping the last known geometry beats persisting an empty one.
        if (bounds.Width <= 0 || bounds.Height <= 0) return _state;

        _state = _state with
        {
            X = bounds.X, Y = bounds.Y, W = bounds.Width, H = bounds.Height,
        };

        return _state;
    }

    private void PromptRename()
    {
        var dialog = new RenamePrompt(Path.GetFileNameWithoutExtension(NotePath))
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true) return;

        // The manager performs the rename: it owns the index key and the
        // open-notes map, and re-keying either from here would leave the other
        // stale.
        RenameRequested?.Invoke(NotePath, dialog.NewName);
    }

    private void ConfirmDelete()
    {
        // Owner is THIS window, not the parameterless overload. An owner-less
        // MessageBox gets hWnd = IntPtr.Zero and no owner relationship, so a
        // Topmost note (Always on Top is a first-class feature here) can
        // render on top of it -- a frozen, unclickable note with no dialog
        // visible anywhere.
        var answer = MessageBox.Show(
            this,
            $"Send \"{Path.GetFileName(NotePath)}\" to the Recycle Bin?",
            "Delete note",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        DeleteRequested?.Invoke(NotePath);
    }

    private void ApplyTheme(NoteTheme theme)
    {
        _theme = theme;
        _resolvedMode = theme.Mode;

        Root.Background = new SolidColorBrush(Parse(theme.ContentBg));
        Root.BorderBrush = new SolidColorBrush(Parse(theme.Border));
        Header.Background = new SolidColorBrush(Parse(theme.ChromeBg));
        TitleText.Foreground = new SolidColorBrush(Parse(theme.ChromeFg));

        // The header glyphs are styled in App.xaml and reach these two through
        // DynamicResource, because a Style in application scope cannot see a
        // per-note theme any other way. Without them the buttons fell back to
        // Button's default near-black and were invisible on every dark note --
        // the first thing a human noticed about this app.
        //
        // ChromeFg for the glyphs, and ChromeFg at low alpha for the hover
        // wash. Deriving the wash from the foreground rather than picking
        // black-or-white by mode is what makes it correct for Charcoal/Light,
        // whose chrome is dark even though the mode says light.
        var glyph = Parse(theme.ChromeFg);

        Resources["HeaderGlyphFg"] = new SolidColorBrush(glyph);
        Resources["HeaderGlyphHover"] = new SolidColorBrush(
            Color.FromArgb(0x2A, glyph.R, glyph.G, glyph.B));
        Editor.Background = new SolidColorBrush(Parse(theme.ContentBg));
        Editor.Foreground = new SolidColorBrush(Parse(theme.ContentFg));
        Editor.CaretBrush = new SolidColorBrush(Parse(theme.Accent));

        _bars.ApplyTheme(theme);
    }

    private static Color Parse(string hex)
        => (Color)ColorConverter.ConvertFromString(hex)!;

    private async void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;

        // Rounded corners come from DWM only. Per-pixel transparency is
        // unavailable because the WebView backdrop must be opaque, so a
        // clipped-geometry approach would round the chrome and leave the note
        // body square.
        DwmCorners.Round(_handle);

        // Geometry in PHYSICAL pixels, via SetWindowPos. Assigning Left/Top
        // would go through WPF's DIP conversion against the PRIMARY monitor's
        // DPI, which is the bug that puts a note saved at 150% in the wrong
        // place at 100%.
        WindowGeometry.SetBounds(
            _handle, new PixelRect(_state.X, _state.Y, _state.W, _state.H));

        Topmost = _state.AlwaysOnTop;
        Opacity = _state.Opacity;

        await InitialiseWebAsync().ConfigureAwait(true);
    }

    private async Task InitialiseWebAsync()
    {
        _web.TaskToggleRequested += OnTaskToggleRequested;
        _web.LinkClicked += OnLinkClicked;
        _web.EditRequested += OnEditRequested;
        _web.FellBackToPlainText += OnFellBackToPlainText;
        _web.ControlRecreated += OnControlRecreated;
        _web.Rendered += OnRenderDelivered;

        var directory = Path.GetDirectoryName(NotePath) ?? string.Empty;

        try
        {
            await _web.InitializeAsync(
                directory,
                _theme,
                allowRemoteImages: _allowRemoteImages,
                backdrop: System.Drawing.ColorTranslator.FromHtml(_theme.ContentBg))
                .ConfigureAwait(true);

            RecordShellInputs();
            _webReady = true;
        }
        catch (Exception ex)
        {
            // App.OnStartup handles the runtime being ABSENT. This is the
            // adjacent and more common case: the runtime is there but
            // WebViewEnvironment.GetAsync or EnsureCoreWebView2Async fails --
            // a locked or corrupt user-data folder, a policy block, an SDK
            // mismatch. OnSourceInitialized is async void, so before this
            // catch existed that took the whole process down at the FIRST
            // note, with no diagnostics line and no recovery snapshot for any
            // other note's dirty buffer.
            //
            // DELIBERATELY UNFILTERED. The failure set here spans COM
            // HRESULTs, IO, argument validation and SDK-internal types; a
            // filter list would be a list of the ones we happened to think
            // of, and the cost of missing one is process death. The recreate
            // path already has FellBackToPlainText; this gives the initial
            // path the same route.
            Core.Diagnostics.DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"{NotePath}: the note viewer could not start -- {ex}");

            // BEFORE the fallback: OnFellBackToPlainText copies _buffer into
            // the editor, and an empty buffer would show the user a blank pane
            // for a note whose text is sitting on disk.
            LoadFromDisk();

            OnFellBackToPlainText(
                "The note viewer could not start. Showing the note as plain text.");

            return;
        }

        LoadFromDisk();
        await RenderAsync().ConfigureAwait(true);

        // Armed LAST. If the shell never posts "ready", WebViewHost.RenderAsync
        // above only queued the render and OnRenderDelivered never fires --
        // this is the guard that turns that silent, empty page into a bar.
        _viewerStall.Start();
    }

    /// <summary>
    /// The point at which a render actually reached the page -- WebViewHost's
    /// RenderAsync branch that posts to a shell that has already said "ready",
    /// not the branch that only queues into _pendingRender. Chosen over
    /// stopping the timer right after the call in InitialiseWebAsync returns,
    /// because that call returns immediately whether or not the shell is
    /// ready -- which would disarm the guard before the exact failure it
    /// exists to catch could ever be observed.
    /// </summary>
    private void OnRenderDelivered() => _viewerStall.Stop();

    /// <summary>The shell never reported "ready" within the guard window.</summary>
    private void ShowViewerStalled()
    {
        Core.Diagnostics.DiagnosticsLog.Write(
            AppPaths.DiagnosticsFile,
            $"{NotePath}: the note viewer did not finish loading within {_viewerStall.Interval.TotalSeconds:0}s.");

        _bars.Show(new InlineBarRequest(
            "viewer-stalled",
            "The note viewer didn't finish loading.",
            PrimaryAction: "Reload",
            // Not a retry loop: ReloadShellAsync re-arms _viewerStall once as
            // part of re-navigating. If the reload itself stalls, the bar
            // comes back and the user can click again.
            OnPrimary: () => _ = ReloadShellAsync(),
            Dismissible: false));
    }

    /// <summary>
    /// A ProcessFailed recovery built a NEW control. WebViewSlot.Content is
    /// only set once in the constructor, so without this the note would keep
    /// showing the dead control after a crash and never visibly recover.
    /// </summary>
    /// <remarks>
    /// This runs SYNCHRONOUSLY inside WebViewHost.RecreateAsync, on the
    /// continuation after its <c>ConfigureAwait(true)</c>, and the re-parent
    /// below must complete before that method's <c>finally</c> disposes the
    /// previous control. Turning either of the host's recreate-path awaits
    /// into <c>ConfigureAwait(false)</c> breaks that ordering -- the
    /// dispatcher marshal here becomes asynchronous and the old control can be
    /// disposed while it is still the one in the visual tree. It shows up as a
    /// blank or torn note, never as an exception at this line. The content
    /// itself is repainted by the new shell's "ready", which posts the host's
    /// remembered render.
    /// </remarks>
    private void OnControlRecreated(WebView2CompositionControl control)
    {
        // Dispatcher-guarded on purpose. WebView2 raises its events on the thread
        // that owns the CoreWebView2 and the WPF wrapper marshals onto that
        // dispatcher, so this should already be the UI thread -- but assigning to
        // the visual tree off-thread throws, and the recovery path is the worst
        // place to discover a wrong assumption about a cross-SDK guarantee.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnControlRecreated(control));
            return;
        }

        WebViewSlot.Content = control;

        // Re-arm the stall guard for THIS navigation. The recreated shell is
        // the only thing that will ever repaint the note -- its "ready" posts
        // the host's remembered render -- so if it never reaches "ready",
        // _pendingRender is never posted and Rendered never fires. The timer
        // was stopped by the first successful render, possibly hours ago, so
        // without this the note is blank with no bar and no timeout: C2's
        // exact symptom, one failure deeper. Restart rather than Start, for
        // the same reason ReloadShellAsync does.
        _viewerStall.Stop();
        _viewerStall.Start();
    }

    private void LoadFromDisk()
    {
        // Cleared up front so a successful Retry re-enables saving. Only the
        // "exists but unreadable" catch below sets it again.
        _loadFailed = false;

        try
        {
            var content = NoteFile.Read(NotePath);
            _saves.AdoptFromDisk(content);
            _buffer = content.Text;
        }
        // FileNotFound and DirectoryNotFound keep the buffer empty and keep
        // SAVING ENABLED on purpose: there is no content to lose, and writing
        // genuinely recreates the note. That is not true of the IO catch below.
        catch (FileNotFoundException) { _buffer = string.Empty; }
        catch (DirectoryNotFoundException) { _buffer = string.Empty; }
        catch (System.Text.DecoderFallbackException)
        {
            // Not UTF-8 text -- an ANSI note, or a binary file with a .md
            // extension. Keep the buffer empty rather than half-read, and
            // never retry the decode leniently: see ShowUnreadable.
            _buffer = string.Empty;
            TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);
            ShowUnreadable();
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the buffer empty rather than half-read -- a partial buffer
            // that later autosaves would overwrite the file with less than it
            // had. ZERO characters is that same failure taken to its limit,
            // which is why the gate below is not optional: the file's real
            // content is on disk and only momentarily unreachable.
            _buffer = string.Empty;
            _loadFailed = true;

            Core.Diagnostics.DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile, $"{NotePath}: could not be read -- {ex.Message}");

            TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);
            ShowLoadFailed(ex.Message);
            return;
        }

        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);
    }

    /// <summary>
    /// The file exists but could not be read right now.
    /// </summary>
    /// <remarks>
    /// Read-only plus <see cref="_loadFailed"/>, not just a bar: an empty note
    /// that accepts typing autosaves one character over the whole file 500ms
    /// later. Retry re-runs the load and, on success, hands the note back --
    /// editable, rendered, and saving again.
    /// </remarks>
    private void ShowLoadFailed(string reason)
    {
        Editor.IsReadOnly = true;

        _bars.Show(new InlineBarRequest(
            "load-failed",
            $"This note couldn't be read — {reason}",
            PrimaryAction: "Retry",
            OnPrimary: () =>
            {
                LoadFromDisk();

                // Still unreadable: LoadFromDisk has already re-raised this
                // bar with the current reason, so there is nothing to undo.
                if (_loadFailed) return;

                Editor.IsReadOnly = false;
                _bars.Dismiss("load-failed");

                if (_editing) SetEditorText(_buffer);

                _ = RenderAsync();
            },
            Dismissible: false,
            // A failed Retry must leave the bar up: the file is still
            // unreadable and the editor is still read-only for a reason.
            PrimaryKeepsBar: true));
    }

    private async Task RenderAsync()
    {
        if (!_webReady) return;

        if (ExceedsPreviewLimit)
        {
            if (!_editing) EnterEditMode();

            _bars.Show(new InlineBarRequest(
                "size-limit",
                "This note is over 2 MB, so the preview is turned off.",
                Dismissible: false));

            return;
        }

        // Trimming a 3MB note back under the limit must take the bar with it.
        // Left up, it claims the preview is off while the preview is visibly
        // working -- and it is not dismissible, so the user cannot clear it.
        _bars.Dismiss("size-limit");

        var result = _renderer.Render(
            _buffer, new StickyMD.Core.Markdown.RenderOptions(_allowRemoteImages));

        if (result.BlockedRemoteImages > 0 && !_allowRemoteImages)
            ShowRemoteImagesAvailable(result.BlockedRemoteImages);
        else
            _bars.Dismiss("remote-images");

        await _web.RenderAsync(result).ConfigureAwait(true);
    }

    private async void OnTaskToggleRequested(int spanStart, int spanEnd, string? token)
    {
        var decision = CheckboxBridge.Decide(_buffer, spanStart, spanEnd, token);

        if (decision.Verdict != ToggleVerdict.Apply)
        {
            // Re-render so the checkbox returns to whatever the file actually
            // says. The page cancelled the default action, so it is currently
            // showing the pre-click state -- but a re-render is what makes
            // that true rather than coincidental.
            Core.Diagnostics.DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"{NotePath}: checkbox click refused -- {decision.Reason}");

            await RenderAsync().ConfigureAwait(true);
            return;
        }

        _buffer = decision.Markdown;
        _saves.MarkDirty(_buffer);
        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);

        // Saved IMMEDIATELY, not on the 500ms debounce. Ticking a box is a
        // deliberate commit, not typing -- and a note closed in the next
        // 500ms must not lose it.
        await FlushAsync().ConfigureAwait(true);

        // Re-render AFTER the save, so the new token matches what is on disk
        // and the next click on the same note is not dropped as stale.
        await RenderAsync().ConfigureAwait(true);
    }

    private void OnLinkClicked(string? href)
    {
        var directory = Path.GetDirectoryName(NotePath) ?? string.Empty;
        var decision = NavigationPolicy.DecideLinkClick(href, directory);

        switch (decision.Action)
        {
            case NavigationAction.OpenInBrowser:
                OpenInBrowser(decision.Target!);
                break;

            case NavigationAction.OpenNote:
                // The manager owns the open-notes map, so it decides whether
                // this is a new window or a focus of an existing one.
                OpenNoteRequested?.Invoke(decision.Target!);
                break;

            default:
                Core.Diagnostics.DiagnosticsLog.Write(
                    AppPaths.DiagnosticsFile, $"{NotePath}: {decision.Reason}");
                break;
        }
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            // UseShellExecute hands the URL to the OS default browser. Without
            // it, .NET tries to execute the string as a program and throws.
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or PlatformNotSupportedException)
        {
            _bars.Show(new InlineBarRequest(
                "link-failed", "That link could not be opened."));

            Core.Diagnostics.DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"{NotePath}: could not open '{url}' -- {ex.Message}");
        }
    }

    private void OnEditRequested()
    {
        if (!_editing) EnterEditMode();
    }

    private void OnFellBackToPlainText(string message)
    {
        // Readable beats rendered. The text is already in memory, so nothing
        // is lost -- the note becomes a plain-text pane.
        WebViewSlot.Visibility = Visibility.Collapsed;
        Editor.Text = _buffer;
        Editor.Visibility = Visibility.Visible;
        _editing = true;

        _bars.Show(new InlineBarRequest("plain-text", message, Dismissible: false));
    }

    private void OnSaved(SaveOutcome outcome)
    {
        if (outcome.Status == SaveStatus.Failed)
        {
            ShowSaveFailed(outcome.Message ?? "the file could not be written");
        }
        else if (outcome.Status == SaveStatus.Saved)
        {
            // Spec: cleared on the next success.
            _bars.Dismiss("save-failed");
            _bars.Dismiss("file-gone");
        }
    }

    /// <summary>Save exhausted its retries. Not dismissible: the file and the screen disagree.</summary>
    private void ShowSaveFailed(string message)
        => _bars.Show(new InlineBarRequest(
            "save-failed",
            $"Couldn't save — {message}",
            PrimaryAction: "Retry",
            OnPrimary: () => _ = FlushAsync(),
            SecondaryAction: "Save As…",
            OnSecondary: SaveAs,
            Dismissible: false,
            // Save As... copies elsewhere; the original file is still
            // unwritten and _saves.IsDirty is still true. Dismissing here
            // would tear the bar down over a problem that is unresolved.
            SecondaryKeepsBar: true));

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = Path.GetFileName(NotePath),
            InitialDirectory = Path.GetDirectoryName(NotePath),
            AddExtension = true,
            DefaultExt = ".md",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // Deliberately NOT through SaveCoordinator: this writes somewhere
            // else, so it must not record a ledger entry for this note's path
            // or clear this note's recovery snapshot. The original file's
            // problem is unresolved and its bar stays up.
            NoteFile.AtomicWrite(dialog.FileName, _buffer, _saves.Format);

            _bars.Show(new InlineBarRequest(
                "saved-elsewhere",
                $"Saved a copy to {Path.GetFileName(dialog.FileName)}. This note is still unsaved."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _bars.Show(new InlineBarRequest(
                "save-as-failed", $"That copy could not be written — {ex.Message}"));
        }
    }

    /// <summary>An external change arrived while the buffer was dirty.</summary>
    private void ShowChangedOnDisk(NoteContent content)
        => _bars.Show(new InlineBarRequest(
            "changed-disk",
            "Changed on disk.",
            PrimaryAction: "Reload",
            OnPrimary: () => AdoptExternalContent(content),
            SecondaryAction: "Keep Mine",
            OnSecondary: () =>
            {
                // Keeping ours means our text must actually reach the file --
                // otherwise "Keep Mine" silently keeps it only until the
                // window closes.
                _saves.MarkDirty(_buffer);
                _ = FlushAsync();
            },
            Dismissible: false));

    /// <summary>The render blocked at least one remote image.</summary>
    private void ShowRemoteImagesAvailable(int count)
        => _bars.Show(new InlineBarRequest(
            "remote-images",
            count == 1
                ? "1 remote image was blocked."
                : $"{count} remote images were blocked.",
            PrimaryAction: "Load remote images",
            OnPrimary: () =>
            {
                // Per SESSION and per NOTE, deliberately. The global opt-in
                // lives in Settings (Plan C); this one is not persisted, so
                // reopening the note blocks them again. Notes sync, and a
                // one-off decision to trust one note must not become a
                // standing one.
                _allowRemoteImages = true;
                _ = ReloadShellAsync();
            }));

    /// <summary>
    /// The single entry point for re-navigating the shell. Every caller --
    /// the remote-images opt-in, an ApplyState theme/colour change, and the
    /// viewer-stalled bar's own Reload -- goes through here, so none of them
    /// can forget to re-arm the stall guard or to post a render afterwards.
    /// A re-navigation that itself never reaches "ready" would otherwise
    /// leave the note blank with no timer running and no bar -- exactly the
    /// failure the guard exists to close.
    /// </summary>
    private async Task ReloadShellAsync()
    {
        // A CSP change needs a fresh shell -- a meta-tag CSP is fixed at parse
        // time. Content updates never do this.
        await _web.SetThemeAsync(
            _theme,
            _allowRemoteImages,
            System.Drawing.ColorTranslator.FromHtml(_theme.ContentBg))
            .ConfigureAwait(true);

        RecordShellInputs();

        // Restart, not just Start: a re-navigation while a previous guard is
        // already ticking (a second theme change in quick succession) must
        // get the full 10s from THIS navigation, not whatever was left of
        // the last one.
        _viewerStall.Stop();
        _viewerStall.Start();

        await RenderAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Records what the shell was just built with, immediately after each of
    /// the two calls that build one.
    /// </summary>
    private void RecordShellInputs()
    {
        _shellTheme = _theme;
        _shellAllowRemoteImages = _allowRemoteImages;
    }

    /// <summary>INoteWindow's seam onto <see cref="ShowRecoveredContent"/>.</summary>
    public void ShowRecovered(RecoveryEnvelope envelope) => ShowRecoveredContent(envelope);

    /// <summary>A recovery snapshot survived to this startup.</summary>
    public void ShowRecoveredContent(RecoveryEnvelope envelope)
        => _bars.Show(new InlineBarRequest(
            "recovered",
            "Unsaved changes were recovered.",
            PrimaryAction: "Restore",
            OnPrimary: () =>
            {
                _buffer = envelope.Content;
                _saves.MarkDirty(_buffer);
                TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);
                _ = FlushAsync();
                _ = RenderAsync();
            },
            SecondaryAction: "Discard",
            OnSecondary: () => new RecoveryStore(AppPaths.RecoveryDir).Clear(NotePath),
            Dismissible: false));

    /// <summary>
    /// The file is not decodable text -- an ANSI note, or a binary file with a
    /// .md extension.
    /// </summary>
    /// <remarks>
    /// NoteFile decodes STRICTLY and throws rather than substituting U+FFFD,
    /// because a lenient decode followed by a save would write replacement
    /// characters back and destroy the original bytes with no error anywhere.
    /// So the note opens read-only, autosave never runs, and the user is told
    /// why.
    /// </remarks>
    private void ShowUnreadable()
    {
        Editor.IsReadOnly = true;

        _bars.Show(new InlineBarRequest(
            "unreadable",
            "This file isn't UTF-8 text, so StickyMD won't edit it.",
            PrimaryAction: "Show in folder",
            OnPrimary: () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", $"/select,\"{NotePath}\"") { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception) { /* nothing useful to add */ }
            },
            Dismissible: false,
            // Show in folder never makes the file readable -- it only opens
            // Explorer. Dismissing here would leave a silently read-only
            // editor with no stated reason.
            PrimaryKeepsBar: true));
    }

    /// <summary>
    /// Replaces the editor's text with something that came from DISK, not from
    /// the user.
    /// </summary>
    /// <remarks>
    /// The handler is detached across the assignment. Leaving it attached ran
    /// MarkDirty over content that already matched the file -- a redundant
    /// write, and worse: IsDirty flipped true, so the NEXT external edit
    /// escalated from a silent reload to an "Ask" bar for a buffer that had
    /// never been touched. The caret is clamped rather than left at 0, since
    /// assigning Text resets it and an external edit is usually an append.
    /// </remarks>
    private void SetEditorText(string text)
    {
        var caret = Editor.CaretIndex;

        Editor.TextChanged -= OnEditorTextChanged;
        Editor.Text = text;
        Editor.TextChanged += OnEditorTextChanged;

        Editor.CaretIndex = Math.Min(caret, Editor.Text.Length);
    }

    private void OnEditorTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_editing) return;

        // The file has never been read successfully, so _buffer is empty while
        // the note's real text sits on disk. Writing this would truncate it.
        // Editor.IsReadOnly already blocks typing; this closes the path for
        // any programmatic assignment that forgets to.
        if (_loadFailed) return;

        _buffer = Editor.Text;
        _saves.MarkDirty(_buffer);
        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);

        // Restart, not start: the debounce is 500ms after the LAST keystroke,
        // so each one pushes the deadline out.
        _autosave.Stop();
        _autosave.Start();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.E
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            ToggleEditMode();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _editing)
        {
            _ = ExitEditModeAsync();
            e.Handled = true;
        }

        // TEMPORARY (Plan B only). Removed in Plan C, which gives the tray
        // menu New Note and Exit. Without an exit path the shutdown behaviour
        // -- the one that must NOT clear isOpen -- cannot be tested by hand at
        // all, and Task Manager kills the process before OnExit runs.
        if ((Keyboard.Modifiers
                & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt))
            == (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt))
        {
            if (e.Key == Key.Q)
            {
                Application.Current.Shutdown();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.N)
            {
                NewNoteRequested?.Invoke();
                e.Handled = true;
                return;
            }
        }
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        EditResult? result = e.Key switch
        {
            Key.B when control => MarkdownEditOps.ToggleBold(
                Editor.Text, Editor.SelectionStart, Editor.SelectionLength),
            Key.I when control => MarkdownEditOps.ToggleItalic(
                Editor.Text, Editor.SelectionStart, Editor.SelectionLength),
            Key.Enter when !control && !shift => MarkdownEditOps.ContinueList(
                Editor.Text, Editor.SelectionStart, Editor.SelectionLength),
            Key.Tab when !shift => MarkdownEditOps.Indent(
                Editor.Text, Editor.SelectionStart, Editor.SelectionLength),
            Key.Tab when shift => MarkdownEditOps.Outdent(
                Editor.Text, Editor.SelectionStart, Editor.SelectionLength),
            _ => null,
        };

        if (result is null) return;

        ApplyEdit(result.Value);
        e.Handled = true;
    }

    /// <summary>
    /// Applies an <see cref="EditResult"/> through the selection rather than by
    /// assigning Text.
    /// </summary>
    /// <remarks>
    /// Editor.Text = result.Text would CLEAR the undo stack -- a programmatic
    /// Text assignment is not an undoable edit -- so Ctrl+Z after Ctrl+B would
    /// do nothing, or would jump back past several edits. Replacing through
    /// SelectedText goes onto the undo stack as one unit, so Ctrl+Z undoes
    /// exactly the bold.
    /// </remarks>
    private void ApplyEdit(EditResult result)
    {
        Editor.SelectAll();
        Editor.SelectedText = result.Text;
        Editor.Select(result.SelectionStart, result.SelectionLength);
    }

    private void ToggleEditMode()
    {
        if (_editing) _ = ExitEditModeAsync();
        else EnterEditMode();
    }

    public void EnterEditMode()
    {
        _editing = true;

        Editor.Text = _buffer;
        Editor.Visibility = Visibility.Visible;

        // Collapsing the WebView is now a CHOICE, not a requirement. The old
        // rationale -- that an HwndHost paints over WPF content regardless of
        // z-order -- does not apply to WebView2CompositionControl, which
        // renders through D3DImage. It is still worth doing for focus and
        // memory.
        WebViewSlot.Visibility = Visibility.Collapsed;

        Editor.Focus();
        Editor.CaretIndex = Editor.Text.Length;
    }

    public async Task ExitEditModeAsync()
    {
        if (!_editing) return;

        _autosave.Stop();
        await FlushAsync().ConfigureAwait(true);

        _editing = false;

        Editor.Visibility = Visibility.Collapsed;
        WebViewSlot.Visibility = Visibility.Visible;

        await RenderAsync().ConfigureAwait(true);
    }

    public void StopAutomaticSaves()
    {
        _savingStopped = true;

        // An armed tick is the whole hazard: stopping the timer is not tidiness.
        _autosave.Stop();
    }

    private async Task FlushAsync()
    {
        _autosave.Stop();

        if (_savingStopped)
        {
            // This window no longer owns NotePath: a rename landed on it and
            // another window has the file now. Writing here would replace the
            // renamed-in file with this one's text. Recorded, never silent.
            Core.Diagnostics.DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"{NotePath}: a save was refused -- this note was displaced by a rename "
                    + "and no longer owns the path. Its text is intact behind the bar.");

            return;
        }

        if (_loadFailed)
        {
            // THE GATE. Every write path in this window funnels through here,
            // so refusing once is what makes "a note whose file could not be
            // read cannot overwrite that file" true rather than hopeful.
            // Reported rather than swallowed -- a save that quietly does
            // nothing is exactly the silent failure the governing rule
            // forbids.
            if (_saves.IsDirty)
            {
                Core.Diagnostics.DiagnosticsLog.Write(
                    AppPaths.DiagnosticsFile,
                    $"{NotePath}: a save was refused -- the file has never been read "
                        + "successfully, so writing would replace it with less than it has.");
            }

            return;
        }

        await _saves.FlushAsync().ConfigureAwait(true);
    }

    public void ShowNote(bool activate)
    {
        // ShowActivated must be set BEFORE the window is first shown --
        // afterwards it is ignored. Restoring a screenful of notes at logon
        // with activation on makes them fight the logon sequence for focus.
        if (!IsVisible) ShowActivated = activate;

        Show();

        if (activate) Activate();
    }

    public void HideNote()
    {
        // Hide, never Close. Hide All must not touch isOpen, and closing the
        // window would run the Closing path.
        Hide();
    }

    public void FocusNote()
    {
        if (!IsVisible) ShowNote(activate: true);
        Activate();
    }

    public void ApplyState(NoteState state, NoteTheme theme)
    {
        _state = state;

        Topmost = state.AlwaysOnTop;
        Opacity = state.Opacity;

        // Compared BEFORE ApplyTheme overwrites _theme. NoteTheme is a record,
        // so this compares all eight colours plus the resolved mode.
        var shellIsStale = !theme.Equals(_shellTheme)
            || _allowRemoteImages != _shellAllowRemoteImages;

        ApplyTheme(theme);

        if (_handle != IntPtr.Zero)
        {
            WindowGeometry.SetBounds(
                _handle, new PixelRect(state.X, state.Y, state.W, state.H));
        }

        // Only when the shell's own inputs actually moved. ApplyState also
        // arrives for a geometry re-clamp and for OS preference changes that
        // are not theme changes at all -- Windows raises those for accent
        // colour and wallpaper too -- and ReloadShellAsync re-navigates via
        // NavigateToString, which flashes the note and re-renders it. Guarded
        // here as well as in WindowManager on purpose: this makes ApplyState
        // safe for every future caller, not just today's two.
        //
        // Through ReloadShellAsync, not _web.SetThemeAsync directly: that
        // re-navigates the shell but posts no render afterwards, so an
        // ordinary theme or colour change arriving here would leave the note
        // blank until something else happened to trigger a render. Routing
        // through ReloadShellAsync also re-arms the viewer-stall guard, in
        // case THIS re-navigation is the one that never reaches "ready".
        if (_webReady && shellIsStale) _ = ReloadShellAsync();
    }

    public void ApplyExternalContent(NoteContent content)
    {
        // ComputeToken on BOTH sides, never NoteContent.ContentHash directly.
        // ContentHash is SHA-256 of the raw bytes on disk -- BOM and CRLF
        // included -- while ComputeToken hashes the UTF-8 bytes of the
        // normalised text. Comparing a token to a raw hash would report a
        // conflict for a file that is byte-identical in content but stored
        // with CRLF, which is a "Changed on disk" bar on every save for
        // anyone whose notes use CRLF.
        var action = ExternalChangePolicy.Decide(
            _saves.IsDirty,
            MarkdownRenderer.ComputeToken(_buffer),
            MarkdownRenderer.ComputeToken(content.Text));

        if (action == ExternalChangeAction.Ask)
        {
            ShowChangedOnDisk(content);
            return;
        }

        AdoptExternalContent(content);
    }

    /// <summary>
    /// Unconditionally takes the disk's version -- both when
    /// <see cref="ExternalChangePolicy"/> finds nothing at risk, and when the
    /// user explicitly clicks Reload on the "Changed on disk" bar. The click
    /// itself IS the decision, so it must not be run back through the same
    /// policy that produced the bar -- that would recompute against a buffer
    /// that is still dirty and still different from disk, and ask again
    /// instead of reloading.
    /// </summary>
    private void AdoptExternalContent(NoteContent content)
    {
        _bars.Dismiss("changed-disk");

        _buffer = content.Text;

        // The whole NoteContent, so the coordinator keeps the RAW hash rather
        // than the token: that is what goes into the recovery envelope's
        // LastKnownDiskHash, which WindowManager.OfferRecovery compares against
        // a freshly-read NoteFile.Read(...).ContentHash. SaveCoordinator.DiskHash
        // is the one copy of it -- this window deliberately keeps no second.
        _saves.AdoptFromDisk(content);

        // Through SetEditorText: a bare Editor.Text assignment re-enters
        // OnEditorTextChanged and marks a buffer dirty that already matches
        // disk. See SetEditorText's remarks.
        if (_editing) SetEditorText(content.Text);

        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);
        _ = RenderAsync();
    }

    public void NotifyFileDeleted()
        => _bars.Show(new InlineBarRequest(
            "file-gone",
            "This note's file is gone.",
            PrimaryAction: "Recreate",
            OnPrimary: () =>
            {
                // An explicit click re-takes this path. A displaced window is
                // barred from saving automatically, but Recreate is the user
                // saying "put my text here", and overwriting a file renamed
                // into place is accepted when it is asked for out loud.
                _savingStopped = false;

                // The buffer is still here, so recreating is a save. That is
                // the whole reason the buffer is never cleared on failure.
                _saves.MarkDirty(_buffer);
                _ = FlushAsync();
            },
            SecondaryAction: "Close",
            OnSecondary: () => CloseRequested?.Invoke(NotePath),
            Dismissible: false));

    public void NotifyRenamed(string canonicalPath)
    {
        NotePath = canonicalPath;
        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);

        // Remap note.local, or images in the moved note silently stop loading.
        if (_webReady)
            _web.RemapNoteDirectory(Path.GetDirectoryName(canonicalPath) ?? string.Empty);
    }

    public void SaveNow()
    {
        // The same gates FlushAsync applies -- see _loadFailed and
        // _savingStopped. Nothing should be able to mark this note dirty while
        // either flag is set, but the shutdown flush must not be the one write
        // path that assumes so. A displaced window is out of WindowManager's
        // map today and so never reaches here; Plan C wants to make such a
        // window closable, which would put it back in reach.
        if (_loadFailed || _savingStopped) return;

        // Synchronous on purpose: called during shutdown and window disposal,
        // where an awaited continuation may never be pumped because the
        // dispatcher is already shutting down. This cannot deadlock precisely
        // because SaveCoordinator awaits with ConfigureAwait(false)
        // throughout -- no continuation inside FlushAsync ever tries to
        // resume on this (the UI) thread, so blocking it here with
        // GetAwaiter().GetResult() has nothing to wait on that is itself
        // waiting on this thread.
        _saves.FlushAsync().GetAwaiter().GetResult();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // GEOMETRY ONLY. This runs during application shutdown, Windows
        // logoff, and ordinary disposal, and WPF closes EVERY window on
        // shutdown. If the close-glyph logic lived here, choosing Exit would
        // clear isOpen on every note and the next boot would restore none of
        // them -- exactly the failure the three-state model exists to prevent,
        // and a direct breach of success criterion 3.
        //
        // The close glyph goes through CloseRequested -> WindowManager
        // instead -- and WindowManager.CloseNote harvests Bounds itself,
        // because Detach has already removed this subscription by the time
        // Dispose gets here. Do not make this the only geometry harvest.
        if (_handle == IntPtr.Zero) return;

        StateChanged?.Invoke(NotePath, CurrentState());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autosave.Stop();
        _viewerStall.Stop();

        // Spec: autosave on window close and app exit. The window is going
        // away; the buffer must not go with it.
        if (_saves.IsDirty) SaveNow();

        _web.TaskToggleRequested -= OnTaskToggleRequested;
        _web.LinkClicked -= OnLinkClicked;
        _web.EditRequested -= OnEditRequested;
        _web.FellBackToPlainText -= OnFellBackToPlainText;
        _web.ControlRecreated -= OnControlRecreated;
        _web.Rendered -= OnRenderDelivered;

        _web.Dispose();

        SourceInitialized -= OnSourceInitialized;
        Closing -= OnClosing;

        Close();
    }
}
