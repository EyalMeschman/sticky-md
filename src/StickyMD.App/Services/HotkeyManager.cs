using System.Runtime.InteropServices;
using System.Windows.Interop;
using StickyMD.App.Interop;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Input;

namespace StickyMD.App.Services;

/// <summary>
/// One hotkey that could not be registered, and why.
/// </summary>
/// <remarks>
/// Spec §7: registration failure "names the conflicting combination in a tray
/// balloon and flags it in Settings; the app keeps running". Both surfaces read
/// this, which is why it is a value rather than just a log line.
/// </remarks>
public sealed record HotkeyFailure(string Combination, string Reason);

/// <summary>
/// The global hotkeys, per spec §7: a hidden <c>HwndSource</c> plus
/// <c>RegisterHotKey</c>.
/// </summary>
/// <remarks>
/// The sink is a MESSAGE-ONLY window (<c>HWND_MESSAGE</c>), not a hidden
/// top-level one. A hidden top-level window still shows up in
/// <c>EnumWindows</c>, and <c>scripts/verify-smoke-ui.ps1</c> finds note
/// windows by enumerating this process's windows -- so a stray top-level
/// window is a harness that miscounts notes. A message-only window cannot be
/// enumerated at all, which is the property wanted here.
///
/// Notes cannot host these: a hotkey has to work with no note focused, and
/// with no note open at all. That is also why the temporary
/// <c>Ctrl+Shift+Alt+N</c> key on <c>NoteWindow</c> was never a hotkey -- it
/// needed a focused note window to exist.
/// </remarks>
public sealed class HotkeyManager : IDisposable
{
    private readonly string _diagnosticsFile;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private readonly List<HotkeyFailure> _failures = [];

    private int _nextId = 1;
    private bool _disposed;

    public HotkeyManager(string diagnosticsFile)
    {
        _diagnosticsFile = diagnosticsFile;

        _source = new HwndSource(new HwndSourceParameters("StickyMD.Hotkeys")
        {
            Width = 0,
            Height = 0,
            ParentWindow = NativeMethods.HWND_MESSAGE,
        });

        _source.AddHook(OnMessage);
    }

    /// <summary>
    /// Combinations that Windows refused. Empty when everything registered.
    /// </summary>
    public IReadOnlyList<HotkeyFailure> Failures => _failures;

    /// <summary>
    /// Replaces every registration with the supplied set.
    /// </summary>
    /// <remarks>
    /// Wholesale rather than incremental because that is what the callers
    /// actually do: startup registers both, and a Settings save re-registers
    /// both. Registering the new combination before releasing the old one
    /// would also make a hotkey conflict with its own previous value.
    /// </remarks>
    public void Apply(IEnumerable<(string Combination, Action OnPressed)> hotkeys)
    {
        UnregisterAll();

        foreach (var (combination, onPressed) in hotkeys)
        {
            if (!HotkeySpec.TryParse(combination, out var spec))
            {
                // StateValidator already replaces an unparseable hotkey at
                // load, so this is only reachable from a Settings window that
                // let one through. Reported rather than assumed impossible.
                Record(combination, "is not a hotkey StickyMD can register.");
                continue;
            }

            var id = _nextId++;

            // MOD_NOREPEAT, or leaning on Ctrl+Alt+N creates notes at the
            // keyboard repeat rate -- each one a real file in the notes root.
            if (!NativeMethods.RegisterHotKey(
                    _source.Handle, id, spec.Modifiers | HotkeySpec.ModNoRepeat, spec.VirtualKey))
            {
                var error = Marshal.GetLastWin32Error();

                Record(
                    spec.Canonical,
                    error == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                        ? "is already in use by another application."
                        : $"could not be registered (Windows error {error}).");
                continue;
            }

            _actions[id] = onPressed;
        }
    }

    private void Record(string combination, string reason)
    {
        _failures.Add(new HotkeyFailure(combination, reason));

        DiagnosticsLog.Write(
            _diagnosticsFile, $"The hotkey {combination} {reason}");
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_source.Handle, id);

        _actions.Clear();
        _failures.Clear();
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;
        if (!_actions.TryGetValue(wParam.ToInt32(), out var action)) return IntPtr.Zero;

        handled = true;

        // Guarded because this is a window procedure. An exception thrown from
        // inside a WndProc during message dispatch does reach
        // DispatcherUnhandledException, but by then the app is exiting -- and
        // "the new-note hotkey killed StickyMD" is a worse outcome than one
        // hotkey press that did nothing and said so.
        try
        {
            action();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write(_diagnosticsFile, $"A hotkey action failed: {ex}");
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        _source.RemoveHook(OnMessage);
        _source.Dispose();
    }
}
