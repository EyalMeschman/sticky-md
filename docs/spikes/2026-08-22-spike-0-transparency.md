# Spike 0 — WebView2 transparency in WPF

**Date:** 2026-08-22
**Status:** COMPLETE
**Verdict:** **PASS — but only via a path the spec did not contain.**

**Original question:** Does the standard WPF WebView2 control render correctly inside a
window using `WS_EX_LAYERED` + `SetLayeredWindowAttributes` for whole-window opacity, with
`AllowsTransparency=False`?

**Answer: No. That approach is impossible on this platform.** A different approach works and
was verified end to end.

## Environment

|                  |                                                                                   |
| ---------------- | --------------------------------------------------------------------------------- |
| OS               | Windows 11, build **10.0.26200.0**                                                |
| .NET SDK         | 10.0.400                                                                          |
| Runtime          | Microsoft.WindowsDesktop.App 10.0.11                                              |
| WebView2 SDK     | 1.0.4129.50                                                                       |
| WebView2 Runtime | 151.0.4129.101 (already installed; no bootstrapper needed)                        |
| Process          | 64-bit                                                                            |
| Monitors         | 2 × 2560×1440, both at **100%** — DISPLAY1 at (0,0) primary, DISPLAY2 at (2560,0) |

## The original approach fails, and not for the reason the spec assumed

`WS_EX_LAYERED` **cannot be added to an existing WPF window** on this build.

```
exstyle before = 0x00000108   after = 0x00000108   ← UNCHANGED
SetWindowLong  ret = 0x00000108  lastErr = 0       ← reports SUCCESS
SetLayeredWindowAttributes = FALSE  lastErr = 87   ← ERROR_INVALID_PARAMETER
```

`SetWindowLong` returns the previous ex-style and sets `lastErr = 0` — a textbook success —
while silently changing nothing. `SetLayeredWindowAttributes` then correctly refuses, because
the window is not layered.

### Evidence that isolates the cause

A style-matrix probe was run to separate "broken P/Invoke" from "platform refusal":

| Test                                                               | Result                                       |
| ------------------------------------------------------------------ | -------------------------------------------- |
| `SetWindowLong` + `WS_EX_TOOLWINDOW`                               | `0x40100 → 0x40180` **SET** — P/Invoke works |
| `SetWindowLong` + `WS_EX_TRANSPARENT`                              | `0x40100 → 0x40120` **SET** — works again    |
| `SetWindowLongPtrW` + `WS_EX_LAYERED`                              | `0x40100 → 0x40100` **MISSING**              |
| `SetWindowLong` + `LAYERED`, then `SetWindowPos(SWP_FRAMECHANGED)` | **MISSING**                                  |

**The same call sets other ex-style bits on the same window and refuses only `WS_EX_LAYERED`.**
This is a platform constraint, not a coding error.

### Three things the spec got wrong

1. **`AllowsTransparency=False` was declared "non-negotiable".** It is the exact opposite:
   `AllowsTransparency=True` is the _only_ way to obtain a layered WPF window, because WPF
   applies the style at `CreateWindowEx` — the one moment Windows permits it.
2. **WebView2 was blamed.** It is innocent. A bare WPF window containing **no WebView2 at
   all** failed identically. Tested with and without `Topmost`, and with both
   `WindowStyle=None` and `SingleBorderWindow`.
3. **The stated reason for the `WebView2CompositionControl` fallback was wrong.** The control
   swap _is_ needed — but to survive `AllowsTransparency=True`, not to fix layering.

## The approach that works

`AllowsTransparency=True` + `Window.Opacity` + `WebView2CompositionControl`.

Confirmed by readback that WPF really does create the window layered:

```
exstyle     = 0x00080108      ← WS_EX_LAYERED | WS_EX_WINDOWEDGE | WS_EX_TOPMOST
LAYERED bit = SET             ← applied by WPF at CreateWindowEx
```

### Verification results

| Check                                                              | Result                                                                                                               |
| ------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------- |
| HTML renders — headings, tables, inline code, checkboxes, links    | ✅                                                                                                                   |
| Whole window fades with `Window.Opacity`, WebView content included | ✅ at 90%, 50%, 30%                                                                                                  |
| **Dynamic repaint** — JS counter + JS-driven colour changes        | ✅ counter ran past 400                                                                                              |
| Edge/corner resize via `WindowChrome`                              | ✅                                                                                                                   |
| Window move by dragging the header                                 | ✅                                                                                                                   |
| Mouse-wheel scrolling inside the note                              | ✅                                                                                                                   |
| Scrollbar thumb dragging                                           | ✅                                                                                                                   |
| Checkbox click toggles, `onclick` fires                            | ✅                                                                                                                   |
| Link hover, cursor changes                                         | ✅                                                                                                                   |
| Second monitor                                                     | ✅                                                                                                                   |
| Rounded corners via `DWMWA_WINDOW_CORNER_PREFERENCE`               | ✅ (`hr = 0`)                                                                                                        |
| Mixed-DPI across monitors                                          | ⚠️ **UNTESTED** — both displays are at 100%. Not verifiable on this hardware. Recorded as an assumption, not a pass. |

### Required configuration

Every item below was learned by failing first. None are optional.

```
TFM            net10.0-windows10.0.17763.0     NOT plain net10.0-windows
Window         AllowsTransparency = true
               WindowStyle        = None
Opacity        Window.Opacity     — never SetLayeredWindowAttributes
Control        WebView2CompositionControl — never the plain WebView2
Backdrop       DefaultBackgroundColor must be OPAQUE
Resize         WindowChrome { CaptionHeight = 0, ResizeBorderThickness = 6 }
Drag           attach to the HEADER element only — never to the Window
```

**Why each one bites:**

- **TFM.** `WebView2CompositionControl` renders through `D3DImage` and needs the Windows SDK
  WinRT projections, which only flow in from a Windows-version-specific TFM. Plain
  `net10.0-windows` throws `FileNotFoundException: Microsoft.Windows.SDK.NET, Version=
10.0.17763.10` from inside `TryInitializeD3DImage()` — a stack trace that points at D3D and
  gives no hint that the TFM is the culprit. The requirement is visible in the package layout
  (`lib_manual/net8.0-windows10.0.17763.0/…Core.Projection.dll`) and easy to read past.

- **Opaque `DefaultBackgroundColor`.** With alpha 0 the composition surface composites against
  **black**, not against the WPF layer beneath it. **Consequence: per-pixel transparency is
  not available.** The WPF layer cannot show through the WebView region, so non-rectangular
  notes are out and rounded corners still require the DWM call.

- **`WindowChrome`.** `AllowsTransparency=True` removes WPF's resize frame entirely. Without
  `WindowChrome` there is _no_ edge resizing at all. (`AllowsTransparency=False` retains
  `WS_THICKFRAME`, which is why the earlier probe resized without it.)

- **Header-scoped drag — the dangerous one.** `MouseLeftButtonDown` bubbles. A handler on the
  **Window** calls `DragMove()` for every left-click anywhere, _including over the WebView_,
  swallowing the mouse-down before the page receives it. Observed symptoms: checkbox clicks
  do nothing (`onclick` never fires, click counter stays at 0), scrollbar thumb drags move the
  window instead of scrolling, and text selection inside the note is impossible.
  **There is no error, warning, or crash — the note simply stops responding to clicks.**
  This was initially misattributed to the composition control.

## Consequences for the design

- **Spec §6.2 must be rewritten.** Its primary approach is impossible, its stated fallback
  rationale is wrong, and its "non-negotiable" `AllowsTransparency=False` is inverted.
- **Spec §6 "Mode toggle" rationale is now obsolete.** It said the WebView2 must be collapsed
  when entering edit mode because an `HwndHost` paints over WPF content regardless of
  z-order. `WebView2CompositionControl` is a normal WPF element rendering via `D3DImage`, so
  **airspace does not apply.** Collapsing it remains reasonable for memory and focus, but it
  is no longer _required_, and WPF adornments can now legitimately overlay the note content —
  which the inline "changed on disk" and "couldn't save" bars need.
- **Per-pixel transparency is unavailable.** Rounded corners come from DWM, as specced.
- **Mixed-DPI remains unverified.** Physical-pixel geometry (spec §5) is still the right
  design, but it has not been exercised across differing scale factors.

## Reproducing the working configuration

The throwaway spike project is deleted per plan. The minimum viable setup:

```xml
<TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
<UseWPF>true</UseWPF>
```

```csharp
AllowsTransparency = true;                 // layered at CreateWindowEx
WindowStyle = WindowStyle.None;
Background = new SolidColorBrush(noteColour);

WindowChrome.SetWindowChrome(this, new WindowChrome
{
    CaptionHeight = 0,                     // whole window is client area
    ResizeBorderThickness = new Thickness(6),
    GlassFrameThickness = new Thickness(0),
});

// Opacity: WPF property, not Win32.
Opacity = 0.90;

// Content: composition control, opaque backdrop.
var web = new WebView2CompositionControl();
await web.EnsureCoreWebView2Async();
web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, r, g, b);

// Drag: HEADER ONLY. Never `this.MouseLeftButtonDown`.
header.MouseLeftButtonDown += (_, e) =>
{
    if (e.ButtonState == MouseButtonState.Pressed)
    {
        try { DragMove(); } catch (InvalidOperationException) { }
    }
};
```
