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
    void ApplyState(NoteState state, NoteTheme theme);

    /// <summary>An external edit arrived and the buffer was clean.</summary>
    void ApplyExternalContent(NoteContent content);

    void NotifyFileDeleted();
    void NotifyRenamed(string canonicalPath);
    void SaveNow();

    /// <summary>A recovery snapshot survived to this startup.</summary>
    void ShowRecovered(RecoveryEnvelope envelope);

    /// <summary>
    /// The user clicked the close glyph. THE ONLY event that may clear
    /// <c>isOpen</c>.
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
    private string _buffer = string.Empty;
    private string _diskHash = string.Empty;
    private bool _webReady;
    private bool _disposed;
    private bool _editing;

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

        _autosave.Tick += async (_, _) => { _autosave.Stop(); await _saves.FlushAsync().ConfigureAwait(true); };
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

    // CS0067 suppressed for the three below: they are part of INoteWindow but
    // are only raised starting in Tasks 10-12 (rename, delete, and link
    // navigation are not wired in this task). Left inert on purpose -- see
    // WireHeader.
#pragma warning disable CS0067
    public event Action<string>? DeleteRequested;
    public event Action<string, string>? RenameRequested;
    public event Action<string>? OpenNoteRequested;
#pragma warning restore CS0067

    // `new`: System.Windows.Window already declares a parameterless
    // StateChanged event (window minimize/maximize/restore). This one is
    // INoteWindow's, with a different signature, and deliberately shadows it
    // -- nothing in this class means to observe the base Window's version.
    public new event Action<string, NoteState>? StateChanged;

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
        Header.MouseLeave += (_, _) => FadeHeaderButtons(0.0);

        CloseButton.Click += (_, _) => CloseRequested?.Invoke(NotePath);

        PinButton.Click += (_, _) =>
        {
            _state = _state with { AlwaysOnTop = !_state.AlwaysOnTop };
            Topmost = _state.AlwaysOnTop;
            StateChanged?.Invoke(NotePath, _state);
        };

        // Colour, Opacity, Rename, Delete and the edit toggle are wired in
        // Tasks 10 and 12. Left inert here so this task's deliverable is a
        // window that opens, drags, resizes, and renders.
    }

    private void FadeHeaderButtons(double to)
        => HeaderButtons.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)));

    private void ApplyTheme(NoteTheme theme)
    {
        _theme = theme;

        Root.Background = new SolidColorBrush(Parse(theme.ContentBg));
        Root.BorderBrush = new SolidColorBrush(Parse(theme.Border));
        Header.Background = new SolidColorBrush(Parse(theme.ChromeBg));
        TitleText.Foreground = new SolidColorBrush(Parse(theme.ChromeFg));
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

        await _web.InitializeAsync(
            directory,
            _theme,
            allowRemoteImages: _allowRemoteImages,
            backdrop: System.Drawing.ColorTranslator.FromHtml(_theme.ContentBg))
            .ConfigureAwait(true);

        _webReady = true;

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
    }

    private void LoadFromDisk()
    {
        try
        {
            var content = NoteFile.Read(NotePath);
            _saves.AdoptFromDisk(content);
            _buffer = content.Text;
            _diskHash = content.ContentHash;
        }
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
            // had.
            _buffer = string.Empty;
            Core.Diagnostics.DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile, $"{NotePath}: could not be read -- {ex.Message}");
        }

        TitleText.Text = NoteTitleResolver.Resolve(_buffer, NotePath);
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

        // Restart, not just Start: a re-navigation while a previous guard is
        // already ticking (a second theme change in quick succession) must
        // get the full 10s from THIS navigation, not whatever was left of
        // the last one.
        _viewerStall.Stop();
        _viewerStall.Start();

        await RenderAsync().ConfigureAwait(true);
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

    private void OnEditorTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_editing) return;

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

    private async Task FlushAsync()
    {
        _autosave.Stop();
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

        ApplyTheme(theme);

        if (_handle != IntPtr.Zero)
        {
            WindowGeometry.SetBounds(
                _handle, new PixelRect(state.X, state.Y, state.W, state.H));
        }

        // Through ReloadShellAsync, not _web.SetThemeAsync directly: that
        // re-navigates the shell but posts no render afterwards, so an
        // ordinary theme or colour change arriving here would leave the note
        // blank until something else happened to trigger a render. Routing
        // through ReloadShellAsync also re-arms the viewer-stall guard, in
        // case THIS re-navigation is the one that never reaches "ready".
        if (_webReady) _ = ReloadShellAsync();
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
        _diskHash = content.ContentHash;

        // The RAW hash, not the token: this is what SaveCoordinator puts in
        // the recovery envelope's LastKnownDiskHash, which Task 13 compares
        // against a freshly-read NoteFile.Read(...).ContentHash.
        _saves.AdoptFromDisk(content);

        if (_editing) Editor.Text = content.Text;

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
        // instead.
        if (_handle == IntPtr.Zero) return;

        var bounds = WindowGeometry.GetBounds(_handle);

        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        _state = _state with
        {
            X = bounds.X, Y = bounds.Y, W = bounds.Width, H = bounds.Height,
        };

        StateChanged?.Invoke(NotePath, _state);
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
