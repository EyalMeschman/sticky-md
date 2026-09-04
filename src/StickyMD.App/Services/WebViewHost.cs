using System.Drawing;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StickyMD.App.Messaging;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Markdown;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Services;

/// <summary>
/// One note's WebView2: the locked-down browser, the static shell, the
/// note-directory mapping, the message bridge, and crash recovery.
/// </summary>
/// <remarks>
/// SPIKE 0 SETTLED THE FOLLOWING. None of it is stylistic.
///
/// WebView2CompositionControl, never the plain WebView2. The plain control is
/// an HwndHost and renders NOTHING inside a window with
/// AllowsTransparency=True. The composition control renders via D3DImage,
/// which is also why airspace does not apply and WPF adornments -- the inline
/// bars -- may legitimately overlay note content.
///
/// DefaultBackgroundColor must be OPAQUE. With alpha 0 the composition surface
/// composites against BLACK rather than against the WPF layer beneath it. That
/// is why per-pixel transparency is unavailable and rounded corners come from
/// DWM. Setting it to the note colour also removes the white flash on first
/// paint.
///
/// The shell is navigated to ONCE. Content arrives by postMessage so scroll
/// survives and an external edit does not flash. The exception is a CSP
/// change: a meta-tag CSP is fixed at parse time, so flipping the
/// remote-image setting rebuilds the shell and navigates again.
///
/// NavigationStarting cancels everything EXCEPT the shell's own load. The spec
/// says cancel unconditionally, which would leave the note permanently blank
/// with no exception anywhere -- see NavigationPolicy.DecideNavigation.
/// </remarks>
public sealed class WebViewHost : IDisposable
{
    private readonly string _notePath;

    private WebView2CompositionControl _control = new();
    private string _noteDirectory = string.Empty;
    private string _mappedDirectory = string.Empty;
    private bool _shellLoaded;
    private bool _allowRemoteImages;
    private NoteTheme? _theme;
    private Color _backdrop;
    private RenderResult? _pendingRender;
    private int _processFailures;
    private bool _disposed;

    /// <param name="notePath">
    /// Canonical, and used only for diagnostics -- so a logged WebView2 crash
    /// names the note it happened in.
    /// </param>
    public WebViewHost(string notePath) => _notePath = notePath;

    /// <summary>
    /// The current control. ITS VALUE CAN CHANGE: a first ProcessFailed
    /// recovery replaces it with a freshly-initialised instance. A consumer
    /// that reads this once and caches it will keep displaying a dead
    /// control after that recovery -- subscribe to
    /// <see cref="ControlRecreated"/> and re-parent to the new value instead
    /// of relying on this property staying stable.
    /// </summary>
    public WebView2CompositionControl Control => _control;

    /// <summary>Span start, span end (INCLUSIVE), render token.</summary>
    public event Action<int, int, string?>? TaskToggleRequested;

    /// <summary>The RAW href attribute, unresolved.</summary>
    public event Action<string?>? LinkClicked;

    public event Action? EditRequested;

    /// <summary>
    /// The renderer failed twice. The note must fall back to a plain-text pane
    /// so it stays readable. The argument is a message for the inline bar.
    /// </summary>
    public event Action<string>? FellBackToPlainText;

    /// <summary>
    /// Raised when a ProcessFailed recovery replaced the underlying control. The
    /// window owns the visual tree, so it must re-parent the new control; the host
    /// only owns the control's lifetime.
    /// </summary>
    public event Action<WebView2CompositionControl>? ControlRecreated;

    /// <summary>
    /// Raised when a render actually reaches the page -- the branch of
    /// <see cref="RenderAsync"/> that posts to a shell that has already said
    /// "ready", never the branch that only queues into <c>_pendingRender</c>.
    /// A caller that never sees this fire knows the shell is stuck, not just
    /// that content has not changed yet.
    /// </summary>
    public event Action? Rendered;

    public async Task InitializeAsync(
        string noteDirectory, NoteTheme theme, bool allowRemoteImages, Color backdrop)
    {
        _noteDirectory = noteDirectory;
        _theme = theme;
        _allowRemoteImages = allowRemoteImages;
        _backdrop = backdrop;

        // BEFORE EnsureCoreWebView2Async, so the very first frame is the note
        // colour. Setting it afterwards shows a white flash first.
        _control.DefaultBackgroundColor = backdrop;

        // ConfigureAwait(TRUE) on both, and it must stay true. Everything
        // after these awaits touches the WPF control and CoreWebView2, both of
        // which belong to the UI thread; resuming on a ThreadPool thread
        // throws InvalidOperationException from inside the WebView2 wrapper.
        // On the RecreateAsync path it matters twice over -- see the note
        // there about the reparent-before-Dispose ordering. A future
        // ConfigureAwait(false) here produces a silent hang or a dispose race
        // with no local clue that this line was the cause.
        var environment = await WebViewEnvironment.GetAsync().ConfigureAwait(true);
        await _control.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var core = _control.CoreWebView2;

        ApplyLockdown(core.Settings);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += OnProcessFailed;

        MapNoteDirectory(core, noteDirectory);
        NavigateShell();
    }

    private static void ApplyLockdown(CoreWebView2Settings settings)
    {
        // A note is a text renderer. Everything below is a browser affordance
        // it has no use for, and every one of them is a way for a synced note
        // to do something the user did not ask for.
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        // Scripting stays ON: the bridge script is the only script that can
        // run, since Markdig runs with DisableHtml() and the CSP admits only
        // the shell's nonce.
        settings.IsScriptEnabled = true;
        settings.IsWebMessageEnabled = true;
    }

    private void MapNoteDirectory(CoreWebView2 core, string noteDirectory)
    {
        if (!Directory.Exists(noteDirectory)) return;

        // DenyCors, not Allow. Notes need ordinary subresource loads -- <img
        // src> -- and nothing more. Allow would additionally permit
        // cross-origin fetch/XHR against the mapped note directory, which
        // nothing in this design requires. A stronger boundary at zero cost.
        core.SetVirtualHostNameToFolderMapping(
            "note.local", noteDirectory, CoreWebView2HostResourceAccessKind.DenyCors);

        _mappedDirectory = noteDirectory;
    }

    /// <summary>
    /// Points <c>note.local</c> at a new directory after the note moved or was
    /// renamed. Without this, images in a moved note silently stop loading.
    /// </summary>
    public void RemapNoteDirectory(string noteDirectory)
    {
        _noteDirectory = noteDirectory;

        var core = _control.CoreWebView2;
        if (core is null) return;

        if (_mappedDirectory.Length > 0)
        {
            core.ClearVirtualHostNameToFolderMapping("note.local");

            // Cleared HERE, not left to MapNoteDirectory. That method returns
            // early when the new directory does not exist, so _mappedDirectory
            // would keep naming the old, now-unmapped folder -- and Dispose
            // would then try to clear a mapping that is already gone.
            _mappedDirectory = string.Empty;
        }

        MapNoteDirectory(core, noteDirectory);
    }

    private void NavigateShell()
    {
        var core = _control.CoreWebView2;
        if (core is null || _theme is null) return;

        _shellLoaded = false;

        core.NavigateToString(
            HtmlDocumentBuilder.BuildShell(
                new HtmlShellOptions(_theme, _allowRemoteImages)));
    }

    /// <summary>
    /// Pushes rendered content into the page, and REMEMBERS it.
    /// </summary>
    /// <remarks>
    /// The last render is this host's state, not a one-shot queue. It used to
    /// be stored only when the shell was not yet loaded and cleared the moment
    /// it was delivered, and that left the ProcessFailed recovery with nothing
    /// to repaint: RecreateAsync builds a new control, InitializeAsync
    /// navigates a fresh shell, the new page posts "ready", and the handler
    /// found nothing pending and posted nothing. The note stayed blank
    /// FOREVER -- no exception, no bar, and no timer running, because
    /// NoteWindow's viewer-stall guard was stopped by the first successful
    /// render hours earlier. Keeping the last render here makes every
    /// re-navigation self-healing instead.
    /// </remarks>
    public Task RenderAsync(RenderResult result)
    {
        _pendingRender = result;

        if (!_shellLoaded || _control.CoreWebView2 is null) return Task.CompletedTask;

        _control.CoreWebView2.PostWebMessageAsJson(WebMessages.Render(result));
        Rendered?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies a new theme, and a new remote-image setting.
    /// </summary>
    /// <remarks>
    /// A theme change alone could be a postMessage that rewrote the CSS
    /// variables. A remote-image change cannot: the CSP lives in a meta tag,
    /// fixed at parse time, so the shell has to be rebuilt and navigated
    /// again. Both go down the same path because the one-frame flash on a
    /// deliberate colour change is not worth two code paths -- and content
    /// updates, which happen constantly, still never navigate.
    /// </remarks>
    public Task SetThemeAsync(NoteTheme theme, bool allowRemoteImages, Color backdrop)
    {
        _theme = theme;
        _allowRemoteImages = allowRemoteImages;
        _backdrop = backdrop;
        _control.DefaultBackgroundColor = backdrop;

        NavigateShell();
        return Task.CompletedTask;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // This runs on the UI thread. Nothing in here may throw, or a
        // malformed message from a renderer process takes the app down.
        InboundMessage? message;

        try { message = WebMessages.Parse(e.WebMessageAsJson); }
        catch (ArgumentException) { return; }

        if (message is null) return;

        switch (message.Type)
        {
            case "ready":
                _shellLoaded = true;

                // _processFailures is DELIBERATELY NOT reset here. The guard
                // counts recreations over the note's whole lifetime, not
                // consecutive failures -- resetting on every successful
                // "ready" would let a renderer that crashes, recreates,
                // briefly reaches ready, then crashes again, recreate
                // forever and never reach the plain-text fallback. A
                // long-lived note that crashes twice hours apart, with
                // nothing but healthy time in between, still falls back to
                // plain text on the second failure. That is the trade-off:
                // a predictable single retry protects readability, where an
                // endless recreate loop is exactly the flickering-note
                // failure the fallback exists to prevent.

                // Post the LAST render, and do NOT clear it. Two cases, one
                // line: the first paint of a note opened at startup, whose
                // render raced the shell load; and every shell built AFTER
                // that -- a theme change, the remote-image opt-in, and above
                // all the ProcessFailed recovery, where this is the only
                // thing that will ever repaint the note. See RenderAsync.
                if (_pendingRender is not null) _ = RenderAsync(_pendingRender);

                break;

            case "toggleTask":
                TaskToggleRequested?.Invoke(
                    message.SpanStart, message.SpanEnd, message.Token);
                break;

            case "link":
                LinkClicked?.Invoke(message.Href);
                break;

            case "requestEdit":
                EditRequested?.Invoke();
                break;

            default:
                DiagnosticsLog.Write(
                    AppPaths.DiagnosticsFile,
                    $"{_notePath}: ignored an unknown web message '{message.Type}'.");
                break;
        }
    }

    private void OnNavigationStarting(
        object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var decision = NavigationPolicy.DecideNavigation(e.Uri, _shellLoaded);

        if (decision.Action == NavigationAction.AllowShellLoad) return;

        e.Cancel = true;

        DiagnosticsLog.Write(
            AppPaths.DiagnosticsFile, $"{_notePath}: {decision.Reason}");
    }

    private void OnNewWindowRequested(
        object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Handled, never opened. A link that wants a new window is still just
        // a link, and it goes through the same allowlist as any other -- via
        // the page's 'link' message, not here.
        e.Handled = true;

        DiagnosticsLog.Write(
            AppPaths.DiagnosticsFile,
            $"{_notePath}: refused a new-window request for '{e.Uri}'.");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        DiagnosticsLog.Write(
            AppPaths.DiagnosticsFile,
            $"{_notePath}: WebView2 {e.ProcessFailedKind} (failure {_processFailures + 1}).");

        _processFailures++;

        if (_processFailures >= 2)
        {
            // Recreating a second time risks a crash loop. A plain-text pane
            // keeps the note READABLE, which is the property that matters --
            // and the text is already in memory, so nothing is lost.
            FellBackToPlainText?.Invoke(
                "The renderer stopped twice. Showing the note as plain text.");
            return;
        }

        _ = RecreateAsync();
    }

    private async Task RecreateAsync()
    {
        if (_disposed || _theme is null) return;

        var previous = _control;
        var restored = false;

        try
        {
            _control = new WebView2CompositionControl();

            // ConfigureAwait(TRUE), and it must stay true for two separate
            // reasons. First, everything below this line touches the WPF
            // visual tree via ControlRecreated. Second, the ORDERING: the
            // subscriber re-parents the new control on this same thread before
            // the finally block disposes `previous`. With
            // ConfigureAwait(false) the continuation runs on a ThreadPool
            // thread, ControlRecreated marshals back onto the dispatcher
            // asynchronously, and `previous` can be disposed while it is still
            // the control parented in the tree -- a dispose race that shows up
            // as a blank or torn note, not as an exception here.
            await InitializeAsync(
                _noteDirectory, _theme, _allowRemoteImages, _backdrop)
                .ConfigureAwait(true);

            // Re-check AFTER the await, not just on entry. Dispose() can run
            // while InitializeAsync is in flight -- the note window closed
            // mid-recreation -- and a subscriber that has already torn its
            // window down must not be handed a new control or told to swap
            // panes. Neither event fires in that case; the newly created
            // control is disposed instead, same as any other control this
            // host owns once it is disposed.
            if (_disposed)
            {
                try { _control.Dispose(); } catch (InvalidOperationException) { }
                return;
            }

            // Only on SUCCESS. The window owns the visual tree and must
            // re-parent to the new control; firing this after a failed
            // recreation (see the catch below, where FellBackToPlainText
            // fires instead) would hand the window a control that never
            // finished initialising.
            ControlRecreated?.Invoke(_control);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The half-initialised control goes, and the previous one comes
            // back as _control. Leaving the failed one assigned meant this
            // host was holding a control that never finished InitializeAsync:
            // Dispose() would read CoreWebView2 off it (null, so no handlers
            // detached and no virtual-host mapping cleared) and the previous
            // control's renderer process would leak instead. The restored
            // control is dead too -- ProcessFailed is why we are here -- but
            // it is a control this host has consistent bookkeeping for, and
            // the note is about to stop showing it anyway.
            var failed = _control;
            _control = previous;
            restored = true;

            try { failed.Dispose(); } catch (InvalidOperationException) { }

            // Also guarded: a host that was disposed while this await was in
            // flight must not tell an already-torn-down window to fall back
            // to plain text either.
            if (!_disposed)
            {
                FellBackToPlainText?.Invoke(
                    "The renderer could not be restarted. Showing the note as plain text.");
            }
        }
        finally
        {
            // Not when it was restored above: disposing it there would leave
            // _control pointing at a disposed control, which is the same
            // bookkeeping hole from the other side.
            if (!restored)
            {
                try { previous.Dispose(); } catch (InvalidOperationException) { }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var core = _control.CoreWebView2;

        if (core is not null)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.ProcessFailed -= OnProcessFailed;

            if (_mappedDirectory.Length > 0)
            {
                try { core.ClearVirtualHostNameToFolderMapping("note.local"); }
                catch (InvalidOperationException) { /* already torn down */ }
            }
        }

        // Disposing the control is what releases the renderer process for this
        // note. Closing a note without it leaves a browser process behind, and
        // it only shows up after the tenth note.
        try { _control.Dispose(); } catch (InvalidOperationException) { }
    }
}
