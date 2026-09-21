# StickyMD — Design Spec

**Date:** 2026-08-22
**Status:** Approved for implementation planning

---

## 1. Purpose

A Windows sticky-notes app where **every note is a real `.md` file on disk**.

Windows Sticky Notes stores notes in an opaque database. StickyMD's defining property is that
notes are plain Markdown files the user owns: editable in Obsidian, VS Code, or Notepad,
syncable through OneDrive or Git, readable on GitHub, and outliving the app itself.

Everything else in this spec serves that property.

### Success criteria

1. A note created in StickyMD opens correctly in Obsidian with no app-specific clutter in the file.
2. A note edited in VS Code updates on screen within ~200ms without losing unsaved local edits.
3. Notes reappear on the correct monitor at the correct size after a reboot.
4. Uninstalling StickyMD leaves every note intact and readable.

### Non-goals for v1

Note list / search window, tag support, wikilinks, note history or versioning, `.md` file-type
association, cloud sync of app state, installer, encryption, cross-platform support,
"Locate moved file" recovery UI.

---

## 2. Feature scope (v1)

**Core (all required):**

- Multiple independent sticky windows
- Resizable windows
- Always on top (per note)
- Remembered window position and size (multi-monitor, mixed-DPI safe)
- Window transparency (per note)
- System tray icon
- Launch with Windows
- Open and save actual `.md` files
- Markdown editor with rendered preview
- Per-note colors

**Approved additions:**

- Global hotkeys — `Ctrl+Alt+N` (new note), `Ctrl+Alt+S` (show/hide all)
- Clickable task-list checkboxes in the rendered view, writing back to the `.md`
- Editor conveniences — `Ctrl+B` bold, `Ctrl+I` italic, Enter continues list items
  (`-`, `*`, `1.`, `- [ ]`), `Tab`/`Shift+Tab` indent/outdent list items

**Explicitly excluded from the additions:** auto-closing brackets.

---

## 3. Key decisions

| Decision          | Choice                                            | Rationale                                                                                                                                                                     |
| ----------------- | ------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Note ↔ file model | Every note **is** a `.md` file                    | The entire point of the product                                                                                                                                               |
| Metadata location | Sidecar index in `%LOCALAPPDATA%\StickyMD\`       | `.md` files stay 100% clean; window geometry is machine-specific and must not roam                                                                                            |
| Editor UX         | Preview by default, edit on demand                | Reading is the common case at sticky-note size                                                                                                                                |
| Renderer          | `WebView2CompositionControl` + Markdig→HTML       | Full fidelity (tables, code, checkboxes, images) free; note colors are CSS variables. The composition control, not the plain `WebView2` — required for transparency, see §6.2 |
| Transparency      | `AllowsTransparency=True` + `Window.Opacity`      | The only route that works; `WS_EX_LAYERED` cannot be added post-creation (§6.2, Spike 0)                                                                                      |
| New note          | Auto-filed into a notes root                      | Zero-friction capture                                                                                                                                                         |
| External edits    | Live two-way sync via `FileSystemWatcher`         | Required for the real-files promise to mean anything                                                                                                                          |
| Target framework  | Core `net10.0`; App `net10.0-windows10.0.17763.0` | LTS. Core omits `-windows` to compiler-enforce its purity; App needs the Windows version suffix for the composition control                                                   |

---

## 4. Architecture

Two projects. The boundary is load-bearing: **`StickyMD.Core` has zero WPF, WinForms, or Win32
references**, so all logic is unit-testable headlessly.

```
StickyMD.Core                    StickyMD.App (WPF)
│  pure logic                    │  OS + UI
├── Notes/                       ├── Windows/       NoteWindow, SettingsWindow
│   NoteFile                     ├── ViewModels/    NoteViewModel, SettingsViewModel
│   NoteRepository               ├── Services/
│   NoteTitleResolver            │   WindowManager
│   NoteWatcher                  │   TrayIconService
├── Markdown/                    │   HotkeyManager
│   MarkdownRenderer             │   StartupManager
│   HtmlDocumentBuilder          │   WebViewHost
│   TaskListToggler              │   RecycleBinService
├── Editing/                     │   MonitorEnumerator
│   MarkdownEditOps              │   SingleInstanceService
├── Persistence/                 └── Interop/
│   NoteIndexStore                   LayeredWindow
│   SettingsStore                    DwmWindowAttributes
│   RecoveryStore
├── Geometry/
│   WindowPlacement
└── Theming/
    NotePalette
```

### Component responsibilities

| Component             | Does                                                                 | Depends on            |
| --------------------- | -------------------------------------------------------------------- | --------------------- |
| `NoteFile`            | Atomic read/write of one `.md`, preserving encoding/BOM/line endings | filesystem            |
| `NoteRepository`      | Enumerate root, create, rename, resolve paths                        | `NoteFile`            |
| `NoteTitleResolver`   | Derive display title from content                                    | — (pure)              |
| `NoteWatcher`         | Debounced change events with self-write suppression                  | `FileSystemWatcher`   |
| `MarkdownRenderer`    | Markdig AST → HTML fragment with source spans                        | Markdig               |
| `HtmlDocumentBuilder` | Shell HTML + CSP + theme CSS variables                               | `NotePalette`         |
| `TaskListToggler`     | Flip a checkbox at an exact source span                              | — (pure)              |
| `MarkdownEditOps`     | Bold/italic/list-continue/indent as pure functions                   | — (pure)              |
| `NoteIndexStore`      | Load/save `notes.json` atomically                                    | `NoteFile` primitives |
| `RecoveryStore`       | Write/clear/read self-describing unsaved-buffer snapshots            | filesystem            |
| `WindowPlacement`     | Clamp a saved rect to supplied monitors                              | — (pure)              |
| `NotePalette`         | Single source of truth for all note colors                           | — (pure)              |
| `WindowManager`       | Owns live `NoteWindow`s; open/hide/show/dispose                      | Core + WPF            |
| `MonitorEnumerator`   | Win32 monitor discovery → `MonitorInfo[]`                            | Win32                 |
| `RecycleBinService`   | Delete a file to the Recycle Bin                                     | Win32 shell           |
| `SingleInstance`      | Per-user lock file + command pipe                                    | .NET IO / IPC         |

`MonitorInfo { Bounds, WorkArea, Dpi, IsPrimary, DeviceName }` is plain data. **Monitor
discovery lives in App; placement math lives in Core** — `WindowPlacement.Clamp(savedRect,
IReadOnlyList<MonitorInfo>)`.

`NotePalette` in Core is what prevents WPF chrome and WebView2 content from drifting to
different shades of the same named color.

---

## 5. Data model

### Locations

```
<notes root>\                        default %USERPROFILE%\StickyMD Notes\, configurable
└── *.md                             user content ONLY — StickyMD writes nothing else here

%LOCALAPPDATA%\StickyMD\
├── notes.json                       per-note StickyMD state
├── settings.json                    app-wide preferences
└── recovery\                        unsaved-buffer snapshots
```

`%LOCALAPPDATA%`, not `%APPDATA%` — window coordinates, monitor placement and startup state are
machine-specific, and roaming them to another PC makes things worse rather than better.

### `notes.json`

Keyed by normalized absolute path, compared case-insensitively.

```json
{
  "version": 1,
  "notes": {
    "C:\\Users\\Eyal\\StickyMD Notes\\standup.md": {
      "x": 1200,
      "y": 80,
      "w": 320,
      "h": 420,
      "monitor": "\\\\.\\DISPLAY2",
      "color": "yellow",
      "opacity": 0.95,
      "alwaysOnTop": true,
      "isOpen": true,
      "lastOpenedUtc": "2026-08-22T10:14:00Z"
    }
  }
}
```

`monitor` is written from v1 but not yet read — it costs nothing now and lets prefer-original-monitor
restore land later without an index migration.

### `settings.json`

`notesRoot`, `defaultColor` (`yellow`), `defaultOpacity` (`1.0`), `defaultSize` (`300×340`),
`defaultFontSizePx` (`16`), `theme` (light|dark|system), `hotkeys`, `allowRemoteImages`
(default `false`).

> **Revised 2026-09-09, after the app was used for a day.** `defaultFontSizePx` was not in this
> list, and text size was not adjustable at all: the shell stylesheet took `HtmlShellOptions`'
> 14px default because nothing ever passed a value, the editor had its own hardcoded `FontSize`
> in XAML, and Ctrl+scroll zoom is deliberately off (§6). The only lever was Windows display
> scaling, which enlarges the whole desktop to fix one note.
>
> Text size is now **per note**, in `notes.json` beside `color` and `opacity`, because that is
> what it is: a property of this note, not of the app. `defaultFontSizePx` here is only what a
> NEW note starts at, exactly like `defaultColor` and `defaultSize`. The number 16 is declared
> once, on `HtmlDocumentBuilder.DefaultFontSizePx`, and `AppSettings` reads it from there —
> two constants both meaning "the default text size" is how the CSS and `settings.json` come to
> disagree, which is the drift `NotePalette` exists to prevent for colour.
>
> **A missing `fontSizePx` is an upgrade, not a correction.** Every note in an index written
> before this field deserialises to zero, and `StateValidator` turns a zero into the default
> **silently**. Reporting it would put one line per note into `diagnostics.log` on the first run
> after an update and teach the reader to skim past the corrections that matter. An out-of-range
> value is a different thing and is clamped and reported, like every other bound.

**`launchAtStartup` is deliberately absent.** The Run registry key is the single source of truth —
present means enabled, absent means disabled. Caching it here would create two states to
reconcile, and the registry can change behind the app's back (§7, Startup).

### New note filenames

`New Note` creates `<yyyy-MM-dd>-untitled.md` in the notes root and opens it immediately — no
dialog. Collisions get a numeric suffix (`2026-08-22-untitled-2.md`).

The **display title** is the note's first Markdown heading if it has one, and otherwise the
filename. To change the filename, `⋯ → Rename` renames the file on disk and re-keys the index
entry.

> **Revised 2026-09-04, after Plan B was first run by a human.** The title was originally
> "derived from content, independent of the filename", with a third resolution rule — first
> non-empty line, leading `#` stripped, truncated to 60 characters — under the two heading rules.
> Every test fed that rule prose and it read fine. The first real note anybody pasted in was a
> Markdown table, and the title became `|Shortcut|Action|` on a file the user had deliberately
> renamed to say what the note was. The rule promoted a fragment of content over a name chosen on
> purpose, and it fired for any note starting with a table, list, quote or paragraph. It is gone.
> Headings now match at **any** level (`## Setup` is a title), which is what the first-line rule
> was really covering, and the filename is the fallback because the user controls it.

### `isOpen` is set once and never cleared

> **Revised 2026-09-04, after Plan B was first run by a human.** This section
> originally made `✕` clear `isOpen`, so a closed note did not come back. The
> first person to use the app closed three notes, relaunched, and reasonably
> read their absence as the app failing to restore them. `✕` now means "off my
> screen", `⋯ → Delete` is the only way a note leaves the desktop set, and the
> table below is what the code does. The paragraphs on why shutdown must not
> clear `isOpen` are unchanged and still binding — that hazard was never the
> part in doubt.

| State          | Meaning                                                  | Set by                                                    |
| -------------- | -------------------------------------------------------- | --------------------------------------------------------- |
| `isOpen: true` | This note belongs on my desktop and returns next startup | Opening a note. **Nothing clears it.**                    |
| Hidden         | Temporarily not drawn                                    | `✕`, Hide All / `Ctrl+Alt+S` — **never touches `isOpen`** |
| Instantiated   | A `NoteWindow` + WebView2 exists in memory               | `WindowManager`                                           |

```
✕ on note        → isOpen unchanged, dispose window + WebView2
Hide All         → isOpen unchanged, Visibility.Hidden
Exit StickyMD    → isOpen unchanged, process exits
⋯ → Delete       → file to the Recycle Bin, index entry removed
Next startup     → recreate every isOpen == true note
```

**Nothing in the app may set `isOpen = false`.** Application exit, Windows logoff or shutdown,
internal window disposal, and the close glyph must all leave it alone. A note leaves the desktop
set exactly one way: the user deletes it, and the index entry goes with the file.

What keeps this from carpeting the desktop is the rule below: a `.md` merely present in the notes
root spawns no window. Only notes the user has actually opened carry an `isOpen` entry, so the
restore set is what they built by hand, not the contents of a folder.

The shutdown hazard still needs stating, because WPF closes every window during application
shutdown. If the ordinary `Window.Closing` handler carried close-glyph logic, then:

```
User chooses Exit → WPF closes all NoteWindows → each handler sets isOpen = false
                  → next boot restores zero notes
```

That is precisely the failure this model exists to prevent, and it would break success
criterion 3. Implementation therefore routes `✕` through an explicit `WindowManager.CloseNote()`,
while `Window.Closing` triggered by shutdown or disposal only persists geometry. `SessionEnding`
follows the shutdown path. `CloseNote` no longer clears `isOpen`, so there is now no code path at
all that could produce the failure above — but the routing stays, because it is also where the
geometry harvest and the buffer flush live.

A `.md` added to the notes root **spawns no window** — pointing StickyMD at an Obsidian vault must
not carpet the desktop. It becomes available to StickyMD, but does not enter Recent Notes until it
has actually been opened, since Recent is ordered by `lastOpenedUtc`. `Open Note…` defaults to the
configured notes root. **Recent means recently opened.**

### Geometry

Stored in **physical screen pixels**, restored via `SetWindowPos`. WPF's `Left`/`Top` are DIPs
relative to the primary monitor's DPI; saving on a 150% monitor and restoring on a 100% one puts
the window in the wrong place. The app manifests as PerMonitorV2.

On restore, `WindowPlacement.Clamp` moves any rect that no longer intersects a visible work area
onto the nearest monitor. Off-screen notes are re-clamped on `WM_DISPLAYCHANGE`.

### File format preservation

**Existing files keep what they have**: encoding, BOM presence, CRLF vs LF, and
trailing-newline-or-not are detected on read and reproduced on write.

**New StickyMD files are canonical**: UTF-8 without BOM, CRLF, trailing newline.

Writes go through `NoteFile.AtomicWrite()`. The contract is atomicity, not a specific mechanism —
`File.Replace` where it applies, with a safe fallback where it does not.

### Self-write suppression

Timestamps alone are unreliable across OneDrive, differing filesystems, timestamp resolution, and
metadata-touching tools. Each internal write records a fingerprint:

```
{ normalizedPath, size, lastWriteUtc, contentHash }
```

```
FileSystemWatcher event → debounce 150ms → read current state
                                             ├── hash matches last internal write → ignore
                                             └── otherwise → external change
```

`contentHash` and the render token (§6) are both SHA-256 of the file's bytes. Notes are small;
hashing on every write is correct and cheap.

The 150ms debounce coalesces the burst of raw events a single external save produces, and keeps
the end-to-end external-edit latency inside the ~200ms success criterion.

---

## 6. Note window

### Chrome

`WindowStyle=None`, **`AllowsTransparency=True`** (mandatory — see §6.2), `ShowInTaskbar=False`,
`WindowChrome` with `CaptionHeight=0` and `ResizeBorderThickness=6`.

A thin header bar shows the derived title, with a color dot, pin toggle, pencil (edit), `⋯`
and `✕`. The body is a `Grid` layering the `WebView2CompositionControl` and a `TextBox`;
exactly one is visible at a time.

> **Revised 2026-09-05, after a screenshot of a running note was compared against this
> paragraph.** The glyphs were originally "revealing on hover", implemented as `Opacity="0"` at
> rest. That shipped and was wrong in the same way for the same reason as the `isOpen` and title
> rules: on a dark note there was nothing whatsoever to suggest the header had controls, and the
> first person to run the app could not find them. They now rest at `0.45` and go to `1.0` on
> hover — present enough to be discoverable, quiet enough not to compete with the text. The two
> halves of that number live in `NoteWindow.xaml`'s `HeaderButtons` and
> `NoteWindow.RestingGlyphOpacity` and must agree.

**`WindowChrome` is mandatory, not cosmetic.** `AllowsTransparency=True` removes WPF's resize
frame entirely; without `WindowChrome` a note cannot be resized at all.

Header drag uses `DragMove()` on `MouseLeftButtonDown`, guarded on `ButtonState == Pressed`.
With `CaptionHeight=0` the whole window is client area, so header buttons behave normally.

> **The drag handler MUST be attached to the header element, never to the `Window`.**
> `MouseLeftButtonDown` bubbles, so a `Window`-level handler calls `DragMove()` for every
> left-click anywhere — including over the note content — swallowing the mouse-down before the
> WebView receives it. Symptoms: task checkboxes silently stop toggling, scrollbar drags move
> the window instead of scrolling, and text selection inside a note becomes impossible. There
> is no exception, warning, or crash. Verified the hard way in Spike 0.

`✕` takes the note off the screen and leaves `isOpen` alone, so it returns on the next launch.
**It never deletes the file.** Deletion is `⋯ → Delete`, which sends the file to the Recycle Bin
via `RecycleBinService`, and that is the only thing that removes a note from the desktop set.

The `⋯` menu is exactly: **Rename…**, **Color ▸**, **Opacity ▸**, **Text size ▸**,
**Always on Top ☑**, ─, **Delete**. Every entry maps to a v1 feature; nothing else belongs there.

> **Revised 2026-09-09.** **Text size ▸** was added, and it is the sixth entry rather than a
> seventh thing somewhere else because text size is per-note state exactly like colour and
> opacity — the menu that owns those is the menu that owns this. It is a slider bounded by
> `StateValidator.MinFontSizePx`/`MaxFontSizePx` rather than by numbers picked in the view, so
> the control cannot offer a value the validator would then correct on the next load: the user
> would watch their own choice change by itself.
>
> Changing it re-navigates the note's shell rather than re-rendering its content, for the same
> reason a colour change does: the size is a rule in the stylesheet of the page the WebView
> navigated to, and every heading, code and blockquote size is an `em` multiple of it. So it
> joins the theme and the remote-image policy in `NoteWindow.ApplyState`'s staleness check, and
> one Settings save re-navigates each note once instead of twice.

### 6.2 Transparency — measured, not assumed

**Spike 0 has run. Verdict: the approach originally specced here is impossible; a different one
works.** Full evidence in `docs/spikes/2026-08-22-spike-0-transparency.md`.

**`WS_EX_LAYERED` cannot be added to an existing WPF window** on Windows 11 26200.
`SetWindowLong` returns the previous ex-style with `lastErr = 0` — a textbook success — and
changes nothing. `SetLayeredWindowAttributes` then fails with `ERROR_INVALID_PARAMETER`.

This is a platform constraint, not a coding error, and **not** WebView2's fault:

- The same call successfully sets `WS_EX_TOOLWINDOW` and `WS_EX_TRANSPARENT` on the same window.
- `SetWindowLongPtrW` and a forced `SWP_FRAMECHANGED` both fail identically.
- A bare WPF window containing **no WebView2 at all** fails identically.

**The approach that works:**

| Setting                  | Value                                                      | Why                                                                                                                                                               |
| ------------------------ | ---------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `AllowsTransparency`     | **`True`**                                                 | The _only_ route to a layered WPF window — WPF applies `WS_EX_LAYERED` at `CreateWindowEx`, the one moment Windows permits it. Confirmed: `exstyle = 0x00080108`. |
| Opacity                  | **`Window.Opacity`**                                       | Never `SetLayeredWindowAttributes`. Fades the whole window including WebView content.                                                                             |
| Content control          | **`WebView2CompositionControl`**                           | The plain `WebView2` is an `HwndHost` and renders nothing inside a transparent window.                                                                            |
| `DefaultBackgroundColor` | **opaque**                                                 | Alpha 0 composites against **black**, not against the WPF layer.                                                                                                  |
| Rounded corners          | `DwmSetWindowAttribute` + `DWMWA_WINDOW_CORNER_PREFERENCE` | Unchanged from the original plan; verified working (`hr = 0`).                                                                                                    |

**Consequence — per-pixel transparency is NOT available.** Because the WebView backdrop must be
opaque, the WPF layer cannot show through the note content. Non-rectangular notes are out;
rounded corners come from DWM only.

**Consequence — the App project MUST target `net10.0-windows10.0.17763.0`.** Plain
`net10.0-windows` throws `FileNotFoundException: Microsoft.Windows.SDK.NET` from inside
`WebView2CompositionControl.TryInitializeD3DImage()`, because the composition control renders
through `D3DImage` and needs the Windows SDK WinRT projections.

**Verified in Spike 0:** rendering (headings, tables, code, checkboxes, links), opacity at
90/50/30%, dynamic repaint (a JS-driven counter and colour changes), edge resize, header drag,
wheel scrolling, scrollbar dragging, checkbox clicks firing `onclick`, link hover, and the
second monitor.

**Not verified:** mixed-DPI across monitors — both test displays run at 100%, so it was not
exercisable. Physical-pixel geometry (§5) remains the right design but carries an unproven
assumption.

### Mode toggle

`Ctrl+E` or the pencil icon enters edit; `Esc` returns to preview. Entering edit collapses the
`WebView2CompositionControl` and exiting saves, re-renders, and restores.

**Note on rationale:** an earlier version of this spec claimed collapsing was _required_
because a WebView2 `HwndHost` paints over WPF content regardless of z-order. That reasoning no
longer applies — `WebView2CompositionControl` is an ordinary WPF element rendering via
`D3DImage`, so **airspace does not apply.** Collapsing remains worthwhile for focus and memory,
but it is now a choice rather than a constraint. The useful corollary: **WPF adornments may
legitimately overlay note content**, which is what the inline "changed on disk" and "couldn't
save" bars (§8) need.

Double-click enters edit **only** when the shell's own handler sees `event.target` as the body or
content container. Double-clicking rendered text, links, code, or checkboxes keeps native
word-selection.

A newly created empty note opens directly in edit mode with focus.

### Render pipeline

One shared Markdig pipeline:

```
UseAdvancedExtensions()            tables, task lists, autolinks, footnotes
UseSoftlineBreakAsHardlineBreak()  a single newline breaks visually — nobody expects
                                   Markdown soft-wrap semantics in a sticky note
UsePreciseSourceLocation()         exact source spans for checkbox write-back
DisableHtml()                      raw HTML renders as escaped text, never executes
```

WebView2 is locked down: dev tools off, default context menus off, host objects off, browser
accelerator keys off, zoom control off, CSP meta in the shell. `DefaultBackgroundColor` is set to
the note color so there is no white flash on first paint.

Content is delivered by **postMessage, not re-navigation**: navigate once to a static shell, then
push `{ type: "render", html, cssVars, token }` and swap `innerHTML` while preserving `scrollTop`.
Re-navigating per update would flash and reset scroll — most noticeable when a long note reloads
from an external edit.

### Resource and navigation policy

Each note's WebView2 maps its own directory:
`SetVirtualHostNameToFolderMapping("note.local", <note dir>, DenyCors)`. Relative image `src`s are
rewritten to `https://note.local/...` at render time, and remapped when the note moves.

`DenyCors`, not `Allow`: StickyMD only needs ordinary subresource loads such as `<img src>`.
`Allow` would additionally permit cross-origin `fetch`/XHR against the mapped note directory,
which nothing in this design requires. Stronger boundary at zero cost.

```
relative image (in note dir)  → allowed via note.local
data: image                   → allowed
remote https image            → BLOCKED by default; per-note "Load remote images" bar
                                (per session) plus a global opt-in in Settings
absolute local path image     → blocked, muted placeholder
https link click              → default browser
relative .md link click       → opens as a StickyMD note
#anchor                       → in-note scroll
everything else               → rejected
```

**The CSP is built per render from the effective remote-image setting** — a fixed
`img-src https://note.local data:` would keep blocking remote images after the user opted in:

```
default                 img-src https://note.local data:
remote images enabled   img-src https://note.local data: https:
```

Blocking remote images by default matters because these files sync — a shared note containing
`![](https://example.com/tracker?id=123)` must not silently make network requests just because
StickyMD rendered it.

**All navigation is intercepted before the WebView2 acts on it.** `NavigationStarting` and
`NewWindowRequested` cancel unconditionally; the URI is then handed to the host, which applies the
allowlist above and performs the action itself. Nothing is allowed through by default, so a
Markdown link carrying an unexpected or hostile scheme is rejected rather than executed inside the
WebView.

**Known v1 limitation:** paths escaping the note directory (`../shared/x.png`) do not resolve.

### Checkbox write-back

A custom `HtmlObjectRenderer<TaskList>` emits each checkbox with its exact source span:

```html
<input type="checkbox" data-span-start="N" data-span-end="M" />
```

Clicking posts `{ type: "toggleTask", spanStart, spanEnd, token }`, where `token` is a hash of the
markdown at render time.

```
token != hash(current content)  → drop the click, re-render
otherwise                       → validate the span still reads [ ] / [x] / [X],
                                  replace exactly those characters, save, re-render
```

Source spans survive document edits that ordinals would not; the token closes the remaining
race. The failure mode is "rarely ignores a click and refreshes," never "edits the wrong task."

### Editor conveniences

Pure functions in Core: `(text, selStart, selLen) → EditResult`.

- **Bold / italic** — wrap the selection, unwrap if already wrapped; empty selection inserts
  markers and places the caret between them.
- **Enter** — continues `-`, `*`, `1.`, and `- [ ]` prefixes preserving indent; ordered lists
  increment. **Enter on an empty list item removes the prefix and ends the list.**
- **Tab / Shift+Tab** — shift selected list lines by 2 spaces; outdent at column 0 is a no-op.
  `AcceptsTab=false` so Tab belongs to the editor; `Esc` is the way out of edit mode.

### Autosave

Debounced 500ms after the last keystroke, plus on blur, mode toggle, window close, and app exit.

---

## 7. Cross-cutting services

### Theming

Seven note colors — **yellow, green, blue, pink, purple, gray, charcoal** — each with a light and
a dark variant, defined once in `NotePalette` as
`NoteTheme { ChromeBg, ChromeFg, Border, ContentBg, ContentFg, Accent, CodeBg, Muted }`.
WPF builds brushes from it; the HTML shell receives the same values as CSS custom properties.

Theme follows light / dark / system; system reads `AppsUseLightTheme` and listens for
`WM_SETTINGCHANGE`.

### Tray

`H.NotifyIcon.Wpf` — proper WPF menus without a WinForms dependency.

Left-click toggles Show All / Hide All. Right-click:

```
New Note
Recent Notes ▸   (10 by lastOpenedUtc, open ones check-marked)
Open Note…
Show All
Hide All
─────────
Settings
Launch at Startup ☑
─────────
Exit
```

`ShutdownMode=OnExplicitShutdown` — hiding or closing the last note must not exit the app.

> **Revised 2026-09-08, when the tray was built.** Two things, and the package
> choice is not one of them — `H.NotifyIcon.Wpf` was re-decided on its merits
> against `System.Windows.Forms.NotifyIcon`, which needs no package because
> WinForms ships in the same `Microsoft.WindowsDesktop.App` runtime, and the
> reasoning above held: WinForms would cost `<UseWindowsForms>` plus a
> `<Using Remove>` in two projects to stop the implicit-usings clash on
> `MessageBox` and `Application` from failing a build that treats warnings as
> errors, and would put a WinForms-rendered menu beside the WPF `⋯` menu on the
> same notes.
>
> **The menu grows one conditional item.** When a hotkey could not be
> registered, a warning item appears above the final separator naming it, and
> clicking it opens Settings. The Hotkeys section below already required the
> failure to be "flagged in Settings"; the balloon that announces it is gone by
> the time anyone goes looking, and Settings is two clicks away through a menu
> that otherwise gives no hint anything is wrong. The item is absent whenever
> both hotkeys registered, so the menu above is what a working install shows.
>
> **The icon starts in Windows 11's hidden-icons overflow**, behind the `^`
> chevron, and no app can promote itself out of it — the user drags it out once.
> Not a design change, but it is the first thing that looks like the tray
> failing to load, and `scripts/verify-smoke-ui.ps1` has to open the flyout to
> find the icon at all.

> **Revised 2026-09-21.** `Show All` and `Hide All` are one checkable item,
> **`Show Notes ☑`**, ticked when at least one note is on screen and clicking
> it runs the same toggle as the left-click. Two items that each only ever
> make sense in one state were a pair of buttons where a switch was wanted; the
> tick also tells the user which state they are in before they click. `HideAll`
> and `ShowAll` remain in code, for the hotkey toggle and for a second launch.
>
> The same revision adds two settings. **Start hidden** (`startHidden`, off):
> a `--startup` launch restores the open notes and hides them at once, so
> signing in does not carpet the desktop; a manual launch still shows them.
> **Hotkeys enabled** (`hotkeysEnabled`, on): off registers neither
> combination, leaving both free for other applications, and keeps the
> combinations for when it is turned back on.

### Hotkeys

Hidden `HwndSource` + `RegisterHotKey`. Defaults `Ctrl+Alt+N` (new note), `Ctrl+Alt+S`
(show/hide all), both configurable. Registration failure names the conflicting combination in a
tray balloon and flags it in Settings; the app keeps running.

**A hotkey must carry at least one of Ctrl, Alt, Shift or Win.** `RegisterHotKey` accepts a bare
key and then swallows it system-wide for every other application, so a "hotkey" of `N` would make
the letter N unusable everywhere until StickyMD exits. `HotkeySpec` in Core refuses one, the
Settings capture box will not accept one, and `StateValidator` replaces one in a hand-edited
`settings.json` with the default and says so. `MOD_NOREPEAT` is added at registration: without it
Windows repeats `WM_HOTKEY` while the combination is held, and a leant-on `Ctrl+Alt+N` creates
notes at the keyboard repeat rate — every one of them a real file in the notes root.

Hotkey strings are stored canonically (`Ctrl`, `Alt`, `Shift`, `Win`, then the key), so one
combination has exactly one spelling in `settings.json`.

### Startup

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `StickyMD` → `"<exe>" --startup`.
No admin rights required. The Settings checkbox reads the registry back rather than trusting
cached state, since cleanup tools strip these entries.

`--startup` reopens notes with `ShowActivated=false` so a screenful of notes does not fight the
logon sequence for focus.

### Single instance

> **Revised 2026-09-04, when the service was built.** "Per-user named mutex" was
> not implementable as written. A named mutex is per-user only in the `Global\`
> namespace, and creating an object there needs `SeCreateGlobalPrivilege`, which
> a standard non-elevated user does not have. The `Local\` namespace works
> without it but is scoped per LOGON SESSION, not per user — and one user gets a
> second session merely by remoting into a machine they are already logged into
> at the console, which would put two processes on one `notes.json`. That is the
> exact failure this section exists to prevent, so the mechanism gave way and
> the requirement did not: the guard is a lock file held with `FileShare.None`
> in `%LOCALAPPDATA%\StickyMD`, which is per-user by construction, needs no
> privilege, and is released by the kernel however the process dies. The pipe is
> unchanged, `PipeOptions.CurrentUserOnly` included, and is keyed on the user's
> SID for the same reason. `SingleInstance` in `StickyMD.App.Services`.

Per-user guard. A second launch sends a command over a named pipe scoped with
`PipeOptions.CurrentUserOnly`, then exits.

```
StickyMD.exe                      → Activate
StickyMD.exe --new                → NewNote
StickyMD.exe "C:\Notes\Test.md"   → OpenNote(path)
```

A command protocol rather than a single "create note" signal — it costs nothing now and is the
prerequisite for ever associating `.md` files with StickyMD.

---

## 8. Error handling

Governing rule: **never lose text, never destroy a file, never die silently.**

| Condition                                   | Behavior                                                                                                                                                                                       |
| ------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Save fails (lock / AV / OneDrive)           | Retry 3× at 100/300/900ms → amber inline bar "Couldn't save — Retry / Save As…". Buffer stays in memory and in the editor. A **recovery snapshot** is written (§8.1). Cleared on next success. |
| External change, clean buffer               | Reload and re-render, scroll preserved                                                                                                                                                         |
| External change, dirty buffer               | Bar: "Changed on disk — Reload / Keep Mine". Never auto-clobbers either side                                                                                                                   |
| File deleted externally                     | "This note's file is gone" → Recreate (writes buffer back) / Close                                                                                                                             |
| File renamed within the watched folder      | `Renamed` supplies both paths — follow it, re-key the index, remap `note.local`                                                                                                                |
| File moved out of the watched folder        | Reported as **deletion**, not rename (§8.2)                                                                                                                                                    |
| `notes.json` corrupt                        | Renamed to `notes.json.corrupt-N`, start empty. **Notes are untouched — only geometry is lost.** Tray balloon explains                                                                         |
| `settings.json` corrupt                     | Same, fall back to defaults                                                                                                                                                                    |
| Notes root missing                          | Created on startup; if creation fails, Settings opens with a banner and the app stays alive in tray                                                                                            |
| Hotkey already taken                        | Balloon naming the combination + red flag in Settings; app runs on                                                                                                                             |
| WebView2 Runtime absent                     | `GetAvailableBrowserVersionString()` returns null → dialog with the Evergreen bootstrapper link + Retry                                                                                        |
| WebView2 process crash                      | `ProcessFailed` → recreate once; a second failure falls back to a plain-text pane so the note stays readable                                                                                   |
| `FileSystemWatcher.Error` (buffer overflow) | Recreate the watcher and re-read the affected note. `FileSystemWatcher` can drop events when its internal buffer overflows, so a watcher must never be allowed to silently stop watching       |
| Monitor removed / resolution change         | Clamp on restore; re-clamp off-screen notes on `WM_DISPLAYCHANGE`                                                                                                                              |
| Note > 2MB                                  | Opens in edit mode, preview skipped with a banner — do not hang the WebView2                                                                                                                   |
| Same note opened twice                      | Focus the existing window; never two windows on one file                                                                                                                                       |

### 8.1 Recovery snapshots

When a save fails after all retries, the buffer is written to
`%LOCALAPPDATA%\StickyMD\recovery\<sha256-of-note-path>.json` as a **self-describing envelope**:

```json
{
  "originalPath": "C:\\Users\\Eyal\\StickyMD Notes\\standup.md",
  "content": "…the unsaved buffer…",
  "createdUtc": "2026-08-22T10:14:00Z",
  "lastKnownDiskHash": "sha256:…"
}
```

The filename is a hash and therefore **not reversible**, so the note's identity must live inside
the file. Otherwise a corrupt `notes.json`, a note moved on disk, or a note stored outside the
notes root would leave StickyMD holding recovered text it cannot attribute to any file.

- One snapshot per note, overwritten on subsequent failures.
- Deleted on the next successful save of that note.
- Also written on app exit if the note is still unsaved.
- On startup, a note with a surviving snapshot opens showing a bar:
  **"Unsaved changes were recovered — Restore / Discard."**

Minimal by design: no version history, no multi-snapshot retention.

### 8.2 Known limitation — moves outside the watcher

`FileSystemWatcher` reports `Renamed` only when both the old and new paths are inside the watched
directory. A note moved **out** of its folder arrives as a `Deleted` event, so StickyMD shows the
"file is gone" state and offers Recreate / Close.

This is accepted behavior for v1. There is no "Locate moved file" feature.

---

## 9. Testing

xUnit against `StickyMD.Core`, TDD throughout. Everything with logic in it is reachable without a
UI — that is what the project boundary buys.

### Pure logic

- `NoteTitleResolver` — ATX vs setext H1, `#` inside a fenced block, first-non-empty-line
  fallback, filename fallback
- `MarkdownEditOps` — ~25 cases: bold on empty selection, unwrap already-bold, Enter on an empty
  list item terminates the list, ordered-list increment, outdent at column 0 is a no-op,
  indent preserves selection
- `TaskListToggler` — span validation, `[X]` uppercase, nested items, a span pointing at a
  non-checkbox is rejected, toggle is idempotent in pairs
- `WindowPlacement.Clamp` — synthetic monitor sets: fully off-screen, partially off-screen,
  monitor removed, negative coordinates left of primary, mixed DPI
- `MarkdownRenderer` / `HtmlDocumentBuilder` — HTML approval tests, CSP and theme-variable
  injection, image `src` rewriting to `note.local`, remote image blocking

### File I/O (temp directories)

- Encoding / BOM / line-ending round-trips: UTF-8 with BOM, UTF-16LE, LF-only, no trailing newline
- New files get the canonical format (UTF-8 no BOM, CRLF, trailing newline)
- **A failed atomic write leaves the original file intact and unmodified**
- A failed atomic write leaves no orphan temp file

### Recovery

- A save failure writes a recovery snapshot containing the unsaved buffer and its `originalPath`
- A subsequent successful save deletes the snapshot

### `NoteWatcher`

Real files with poll-until-condition helpers — no sleep-and-assert. The two that matter most:

- Writing **through** `NoteFile` fires **no** external-change event
- A genuine external write fires **exactly one** event despite multiple raw filesystem events

### `NoteIndexStore`

Round-trip, corrupt-file recovery, unknown-version handling, case-insensitive path keys.

### `StartupManager`

Takes an `IStartupRegistry` seam so the Run-key logic is testable with a fake — the one App
service with enough logic to be worth covering.

### Not unit tested

Windows, WebView2, tray, hotkeys. Covered by a written manual smoke checklist run at the end of
each implementation phase:

- Opacity at 50% and 95%, with resize
- Topmost over a fullscreen application
- Every tray action
- Hotkey conflict path
- Startup entry survives a reboot
- Drag a note between two different-DPI monitors, restart, verify placement
- Unplug a monitor, restart, verify clamping
- Edit a note in VS Code while it is open in StickyMD
- Delete a note from Explorer while it is open

---

## 10. Prerequisites and build

**Environment as verified on 2026-08-22:**

|                  |                                                              |
| ---------------- | ------------------------------------------------------------ |
| .NET SDK         | **10.0.400 — installed**                                     |
| Desktop runtime  | Microsoft.WindowsDesktop.App 10.0.11                         |
| WebView2 Runtime | 151.0.4129.101 — **already present**, no bootstrapper needed |
| OS               | Windows 11 build 10.0.26200.0                                |

**Target frameworks:**

- `StickyMD.Core` → `net10.0` (deliberately _not_ `-windows`, so the zero-WPF/Win32 boundary is
  compiler-enforced)
- `StickyMD.App` → **`net10.0-windows10.0.17763.0`** (the Windows version suffix is required by
  `WebView2CompositionControl`; see §6.2)

Packages: `Markdig`, `Microsoft.Web.WebView2`, `H.NotifyIcon.Wpf`. Tests add `xunit` and
`Shouldly`.

**NuGet sources are disabled machine-wide.** `%APPDATA%\NuGet\nuget.config` contains an
explicitly emptied `<packageSources>` block (dated 2023-10-28) — a deliberate posture, left
untouched. A repo-scoped `NuGet.config` at the solution root enables `nuget.org` for this
repository only, using `<clear />` for deterministic resolution. Without it, every `dotnet add
package` fails with "There are no versions available".

### Spike 0 — COMPLETE

**Verdict: PASS, via a revised approach.** The originally specced technique proved impossible;
§6.2 has been rewritten against measured results. Full evidence, including the four-test style
matrix that isolated the cause, is in
`docs/spikes/2026-08-22-spike-0-transparency.md`.

No further spike work is required before implementation.

### Distribution

Framework-dependent `win-x64` during development; self-contained single-file for sharing.
No installer in v1.
