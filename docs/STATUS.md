# StickyMD — project status

**Read this first.** It is the orientation document for anyone picking this
project up, including a fresh assistant session with no prior context.

Last updated: 2026-09-21. StickyMD is **feature-complete against the spec**.
What is left is a short list of checklist items that need hardware or a
reboot, enumerated under "What is genuinely left for a human".

---

## What this project is

A Windows sticky-notes app where **every note is a real `.md` file on disk**.

Windows Sticky Notes keeps notes in an opaque database. StickyMD's defining
property is the opposite: notes are plain Markdown files the user owns —
editable in Obsidian, VS Code, or Notepad, syncable through OneDrive or Git,
readable on GitHub, and outliving the app itself. Every design decision in
this repo serves that property.

Full spec: `docs/specs/2026-08-22-stickymd-design.md`. It is the binding
authority; where the code deliberately departs from it, the section carries a
dated revision note saying why.

The governing rule the whole codebase is written against:
**never lose text, never destroy a file, never die silently.**

---

## Shape of the code

Two projects.

| Project         | What it is                                                         |
| --------------- | ------------------------------------------------------------------ |
| `StickyMD.Core` | Notes, Markdown, persistence, theming, geometry, hotkey parsing    |
| `StickyMD.App`  | The WPF shell: windows, WebView2, tray, hotkeys, startup, Settings |

`StickyMD.Core` targets plain **`net10.0`**, deliberately _not_
`net10.0-windows`. That makes the spec's "zero WPF/Win32 in Core" boundary a
rule the **compiler enforces** — a stray WPF or Win32 reference fails the
build rather than surviving as a convention nobody checks. Everything in Core
is reachable from a headless test runner.

| Area        | Types                                                                                                   |
| ----------- | ------------------------------------------------------------------------------------------------------- |
| Notes       | `NoteFile`, `NoteFormat`, `NoteRepository`, `NoteWatcher`, `WriteLedger`, `NoteTitleResolver`, `IClock` |
| Editing     | `MarkdownEditOps` (emphasis, lists, indent — three partial files)                                       |
| Markdown    | `MarkdownRenderer`, `SourceSpanTaskListRenderer`, `TaskListToggler`                                     |
| Persistence | `AppPaths`, `JsonFile`, `NoteIndex`, `NoteIndexStore`, `AppSettings`, `SettingsStore`, `RecoveryStore`  |
| Theming     | `NotePalette`                                                                                           |
| Geometry    | `WindowPlacement`                                                                                       |
| Input       | `HotkeySpec` (hotkey string → modifiers + virtual key, as plain `uint`)                                 |

Deliberately **absent** from Core: window chrome, transparency interop,
WebView2 hosting, the tray, the hotkey registration itself, the startup
registry entry, the single-instance pipe, monitor enumeration, and Recycle Bin
deletion. All of it needs WPF or Win32. `HotkeySpec` is the one hotkey piece
that fits inside the boundary, and it does: the Win32 constants it hands out
are just numbers, which is why `StateValidator` can refuse an unusable hotkey
at load time. `NoteRepository` has no delete method for the same reason —
deletion goes through the App's `IFileDeletionService`.

**Tests: 557 pass, 0 failed, 0 build warnings** (423 Core + 134 App). The
checklists are still the real gate, not the test count: a `Window` cannot be
constructed on an xUnit thread, and several of the worst defects found so far
were invisible to a fully green run — a navigation policy that cancelled every
note's shell load, a note whose unreadable file was truncated on the first
keystroke, a note left permanently blank after a renderer crash, a tray tick
that was on screen and invisible to UI Automation.

---

## Contracts

Non-obvious facts the code depends on. Getting any of them wrong produces a
bug that looks like it lives somewhere else.

### Core

1. **Markdig `SourceSpan.End` is INCLUSIVE** — the index of the last
   character, not one past it. Verified empirically for CJK, accented text and
   emoji. `TaskListToggler` and the checkbox message bridge both depend on
   this. A span of `Start=2, End=4` covers three characters.
2. **`NotePalette` is the single source of truth for all seven colours.** Both
   the WPF chrome and the rendered HTML read from it, or "WPF yellow" and
   "WebView yellow" drift apart. `ToCssVariables` is the interface
   `HtmlDocumentBuilder` consumes. The same rule holds for the default text
   size: `HtmlDocumentBuilder.DefaultFontSizePx` is declared once and
   `AppSettings` reads it from there.
3. **Record every save into `IWriteLedger`.** That is what lets `NoteWatcher`
   distinguish the app's own writes from an external edit. Identity is the
   SHA-256 content hash plus byte length — never a timestamp.
4. **`NoteWatcher.Recovered` is the window manager's job to handle.** When
   the underlying watcher dies and is recreated, `NoteWatcher` does not decide
   which notes to re-read — it has no idea which are open.
5. **`ThemePreference` and `ThemeMode` are deliberately different types.**
   `ThemePreference` is what the user chose and includes `System`; `ThemeMode`
   is the resolved light-or-dark that `NotePalette` needs. Collapsing them
   would force `NotePalette` to answer a question it cannot see the input for.
6. **Every path is `NotePath.Canonical`, compared with `NotePath.Comparer`.**
   The repository, the watcher, the index store and the window manager's
   open-notes map all key on it. Canonicalisation resolves `..` and case, but
   not symlinks or 8.3 short names (known limitation).
7. **`StateValidator` clamps or defaults, never rejects.** Opacity
   `0.20`-`1.0`, width/height `160`/`120` minimum up to `8192`, coordinates
   ±`65536` (guarding the `int` overflow in `WindowPlacement`'s `X + Width`).
   `NoteIndexStore.Load` deserialises entries one at a time, so one bad entry
   costs that note's geometry, not the whole file. Every correction lands in
   `LastLoadIssues` and `diagnostics.log`. A missing `fontSizePx` is the one
   silent correction: it is an upgrade from an older index, not a bad value.
8. **`NoteRepository.CreateNewRecorded` is not thread-safe.** Callers
   serialise. `WM_HOTKEY` is dispatched on the UI thread, so the hotkey path
   needs no lock.

### Shell

1. **`StickyMD.App` must target `net10.0-windows10.0.17763.0`.** The Windows
   version suffix is required by `WebView2CompositionControl`. Plain
   `net10.0-windows` throws `FileNotFoundException: Microsoft.Windows.SDK.NET`
   from inside `TryInitializeD3DImage()`, and the stack trace points at D3D
   while never mentioning the target framework.
2. **A `Window`-level `MouseLeftButtonDown` → `DragMove()` handler silently
   steals every click from the WebView.** Checkboxes stop toggling, scrollbar
   drags move the window, text selection dies. The drag handler is scoped to
   the header element only.
3. **Nothing may clear `isOpen`, including `CloseNote`.** `✕` is "off my
   screen"; `⋯ → Delete` is the only thing that removes a note from the
   restore set, taking the index entry with the file. The tray has no "forget
   this note" action, its Exit goes through `Application.Shutdown()` into
   `OnExit` and `ShutdownWithoutClosingNotes`, and hiding is `HideAll`. What
   keeps the restore set bounded: a `.md` merely present in the notes root
   spawns no window. That is also why Recent Notes and the Show Notes tick
   read "a window exists / is visible right now" rather than the index's
   `isOpen`, which is true for every note ever opened.
4. **Anything that raises `StateChanged` must stamp live geometry through
   `NoteWindow.CurrentState()`.** `WindowManager.Persist` replaces the whole
   index entry, so a handler that passes `_state` straight through persists
   the geometry the note had when it opened and silently reverts every move
   since. `CloseNote` and `ShutdownWithoutClosingNotes` harvest
   `window.Bounds` themselves because `Detach` has already removed the
   subscription by then.
5. **`WindowManager.CreateAndOpenNote` returns `string?` and never throws.**
   `EnsureRootExists` throws for an unplugged drive or a dropped share, and
   from a Click handler that reaches `DispatcherUnhandledException` — one New
   Note on a disconnected share used to close the whole app. The null is
   turned into a balloon in `TrayIconService.NewNote`, and the hotkey and
   `--new` both route through it rather than reaching the manager directly.
6. **`SystemTheme.Changed`, `SystemEvents.DisplaySettingsChanged`, every
   `NoteWatcher` event and the single-instance pipe fire off the dispatcher.**
   Every handler in `App` marshals through `Dispatch(...)` before touching a
   window.
7. **`HtmlDocumentBuilder`'s CSP is per shell.** A meta-tag CSP is fixed at
   parse time, so a change to `allowRemoteImages`, the theme or the text size
   must re-navigate every open note's shell, not just re-render its content.
   That is why they are parameters of `INoteWindow.ApplyState` with one
   staleness check, so a Settings save re-navigates each note once. The
   per-note remote-image bar's opt-in is per session, unpersisted, and OR'd
   with the global flag.
8. **`WindowManager.OnSystemThemeChanged` early-returns when the resolved
   `ThemeMode` has not changed.** Windows raises `UserPreferenceChanged` for
   accent colour, wallpaper and a broad slice of `WM_SETTINGCHANGE` traffic,
   not only light/dark.
9. **`settings.json` must never grow a `launchAtStartup` field.** The HKCU
   Run key is the single source of truth, per spec §5, and both the tray item
   and the Settings checkbox read it back on every open. Cleanup tools, group
   policy and third-party startup managers strip those entries behind the
   app's back. (`startHidden` is a different thing — it is about what a
   startup launch does, not whether one happens — and does live in
   `settings.json`.)
10. **The `NoteWatcher` belongs to `App`, not to `WindowManager`.** A notes
    root change is applied in two places: `WindowManager.ApplySettings`
    replaces the `NoteRepository`, and `App.ApplySettings` restarts the watcher
    on `_manager.Repository`. Doing only one leaves New Note landing in the
    old folder or every edit in the new one missed.
11. **Nothing in the app may occupy a top-level window it does not intend to
    show.** `verify-smoke-ui.ps1` finds note windows by enumerating this
    process's visible `HwndWrapper` windows. That is why `HotkeyManager`'s sink
    is a message-only window (`HWND_MESSAGE`). `SettingsWindow` IS such a
    window, deliberately, so any check that counts notes closes Settings first.
12. **Do not reintroduce a note-hosted key** for anything the app must be able
    to do with zero notes open. The temporary `Ctrl+Shift+Alt+Q`/`N` keys
    existed only until the tray did.
13. **Only the two CORRUPT-file cases and the notes-root failure get a
    balloon**, not every entry in `LastLoadIssues`. A balloon per correction
    trains the user to dismiss the one that matters.

---

## The shell services, and what is worth knowing about each

### The tray

`TrayIconService`, on **`H.NotifyIcon.Wpf` 2.4.1** per spec §7. WinForms'
`NotifyIcon` needs no package, but it costs `<UseWindowsForms>` plus a
`<Using Remove>` in two projects to stop the implicit-usings clash on
`MessageBox` and `Application` from failing a warnings-as-errors build, and a
WinForms-rendered menu beside the WPF `⋯` menu.

- **The menu is rebuilt on every open**, in `PreviewTrayContextMenuOpen`, which
  H.NotifyIcon raises before it reads `ContextMenu`. It has to be: Recent
  Notes, the Show Notes tick and the Launch at Startup tick are all live state,
  and the startup tick is read out of the **registry** each time.
- **Show Notes is one checkable item** (revised 2026-09-21, replacing Show All
  and Hide All). Its tick and the left-click toggle both read
  `WindowManager.AnyVisible`, so they cannot disagree.
- **`ForceCreate(enablesEfficiencyMode: false)`** is required because the icon
  is created in code. The `false` is not decoration — efficiency mode defaults
  to on, and it applies EcoQoS throttling to an app that hosts a WebView2 per
  note.
- **`IsChecked` alone is invisible to UI Automation.** WPF's `MenuItem`
  automation peer exposes `TogglePattern` only for a _checkable_ item, so every
  ticked item is also `IsCheckable`. The click-toggles-the-mark side effect is
  unobservable because the next open rebuilds the menu.
- **The icon starts in Windows 11's hidden-icons overflow**, behind the `^`
  chevron, and no app can promote itself out of there. This is the first thing
  that looks like the tray failing to load.
- The exe icon is embedded **twice** from one `.ico`: `ApplicationIcon` for
  Explorer, and a WPF `<Resource>` so the tray can pick the frame matching
  `SM_CXSMICON` — 16px at 100%, 24px at 150%.

### The hotkeys

`HotkeyManager` plus `Core/Input/HotkeySpec.cs`.

- **`MOD_NOREPEAT` is OR'd in at registration.** Without it a leant-on
  `Ctrl+Alt+N` creates notes at the keyboard repeat rate, each a real file.
- **At least one modifier is mandatory.** `RegisterHotKey` happily accepts a
  bare key and then swallows it system-wide for every other application.
  `HotkeySpec` refuses one, the Settings capture box will not accept one, and
  `StateValidator` replaces one from a hand-edited file with the default.
- **`hotkeysEnabled: false`** (added 2026-09-21) applies an empty set, which
  releases both combinations and clears `Failures`, so the tray's ⚠ item goes
  with them. The combinations themselves are kept for when it is re-ticked.
- **`Ctrl+Alt+S`, a spec default, is already taken on this machine.** Every
  launch on the defaults raises the conflict balloon. That is the feature
  working; both verification scripts write throwaway combinations instead.
- Hotkey strings are stored **canonically** (`Ctrl`, `Alt`, `Shift`, `Win`,
  then the key), so one combination has one spelling in `settings.json`.

### The startup entry

`StartupManager` behind an `IStartupRegistry` seam, as spec §9 asks: a test
that wrote to the real HKCU would change the developer's own logon. Presence
of the Run value **is** the state — a `0` would still be a Run entry.

**Known limitation:** enabled means "the value is present", not "the value
points at THIS exe". Rebuild the app somewhere else and the stale entry stays.
Re-ticking rewrites it, which is the whole repair.

**`startHidden`** (added 2026-09-21): a launch carrying `--startup` restores
the open notes and then `HideAll`s them, so `isOpen` is untouched and one
left-click brings back exactly what a normal launch shows. A manual launch is
unaffected.

### Single instance

`SingleInstance`: a per-user lock file plus a named pipe, taken in
`App.OnStartup` before anything reads or writes app state. A losing launch
hands its command line down the pipe and exits. Two processes each hold their
own copy of `notes.json` and write the whole snapshot on every save, and a
note has already lost its index entry that way.

- **The guard is a LOCK FILE, not the spec's named mutex** (dated revision
  note in the spec). A `Global\` mutex needs a privilege a standard user does
  not have; a `Local\` one is per logon session, and remoting into a machine
  you are already logged into at the console gives one user two sessions. The
  file is `FileShare.None` with `DeleteOnClose`, so a crash, a Task Manager
  kill and a clean exit all release it identically.
- **Only `IOException` means "somebody else is running".**
  `UnauthorizedAccessException` means the guard could not be taken; that path
  logs and starts anyway, because refusing would turn an unwritable
  `%LOCALAPPDATA%` into an app that never starts again.
- **`App.ApplyLaunchArgs` is spec §7's command protocol**, and this process's
  own command line goes through it too. No args activates (`ShowAll`), `--new`
  creates, anything not starting with `-` is a path for `OpenNote`.
  Unrecognised flags such as `--startup` are ignored rather than logged as
  missing files.
- **Known limitation:** a second logon session of the same user hands its
  note to the instance on the OTHER session's desktop. Nothing is lost, and the
  alternative was two writers on `notes.json`.

### The Settings window

`SettingsWindow`, **shown non-modal**: `ShowDialog` would spin a nested
dispatcher frame and freeze every open note, autosave ticks included, for as
long as Settings was open. `App` holds the single instance and activates it on
a second request.

- `IsCancel` is not used; it routes through `Window.DialogResult`, which only
  a `ShowDialog` window has. Escape is wired in code-behind.
- The hotkey boxes are **capture** fields. Every candidate goes through
  `HotkeySpec.TryParse`, so a key StickyMD cannot register just leaves the box
  as it was. WPF's `Key` names match the parser except the digits (`D0`..`D9`)
  and `PageUp`/`PageDown`, whose enum values are declared first as
  `Prior`/`Next`; `HotkeySpec` carries those as aliases.
- Save goes through **`StateValidator.ValidateSettings`**, the same validator
  the loader uses, and every slider takes its bounds from `StateValidator`. A
  control that can produce a value the validator then corrects means the user
  watches their own choice change by itself on the next start.
- **Order in `App.ApplySettings` matters.** The new notes root is proved
  usable BEFORE `settings.json` is written, and the file is written before the
  running app adopts the change, so a refused folder leaves both exactly as
  they were.
- Launch at Startup is applied **on Save, not on toggle**, so Cancel really
  cancels.

### The note window

- **Clicking away ends edit mode** (added 2026-09-21), through
  `Window.Deactivated`. Not through `Editor.LostFocus`, which also fires for
  the `⋯` menu and the rename prompt. The text was never at risk either way;
  autosave and `LostFocus` flush it.
- **Text size is per note**, in `notes.json` beside `color` and `opacity`;
  `AppSettings.DefaultFontSizePx` is only what a new note starts at.
  `Editor.FontSize` is set from the note's state, not from XAML, or preview
  and edit mode show different sizes and `Ctrl+E` looks like it reformatted
  the note.
- **Spec §8's notes-root failure works as written.** The app stays up,
  balloons, opens Settings on the banner, and recovers without a restart when
  pointed somewhere writable.

---

## Known limitations

- **Recycle Bin deletion is not guaranteed to be recoverable.** On a network
  share, a removable drive, or a drive with the Recycle Bin disabled,
  `SHFileOperationW` returns success and the file is gone, so `⋯ → Delete`
  **permanently** deletes while the confirmation says "Send to the Recycle
  Bin". There is no reliable pre-flight check for it.
- **`.stickymd-tmp` files are written INTO the notes root.** An atomic replace
  has to happen on the same volume. A process killed mid-save leaves the file
  behind, there is no cleanup pass, and OneDrive will sync it. The checklist
  has a step that looks for leftovers.
- **The minimum note size is specified in two units.** `NoteWindow.xaml`'s
  `MinWidth="160" MinHeight="120"` are DIPs; `StateValidator`'s are physical
  pixels. On a 150% display a note clamped to the validator's minimum comes
  back larger than it was saved. Left as is: converting one number without
  the other only moves the defect.
- **A window displaced by a rename collision cannot be closed.** Its
  automatic saves are stopped (`StopAutomaticSaves`), so the text-loss half is
  fixed, but it is out of `_windows` and its `✕`, `⋯ → Delete` and bar Close
  are inert until exit. Fixing it wants an orphan list in `WindowManager`, a
  change to the ownership model that deserves its own review round.
- **The shutdown flush can block the UI thread ~1.3s per note whose saves are
  failing.** `SaveCoordinator` runs its full retry schedule synchronously on
  that path. Deliberately parked: skipping the retries needs a shutdown-mode
  flag threaded through the coordinator and its tests, and a slow logoff when
  saves are _already_ failing is milder than the risk of editing the save
  path.
- **`InvariantGlobalization` must stay unset.** WPF's caret setup calls
  `new CultureInfo(1033)` from inside a layout pass, and invariant mode
  refuses it, so every `TextBox` dies. `NoteRepositoryTests.The_new_note_filename_date_does_not_follow_the_machine_calendar`
  cannot run under invariant mode, so re-adding the switch fails a test
  instead of the app.

---

## Verification

```
dotnet build      # 0 warnings; TreatWarningsAsErrors is on
dotnet test       # failed: 0
```

Two checklists, mostly scripted: `docs/checklists/2026-09-02-plan-b-smoke.md`
(the notes themselves: chrome, editing, saving, external edits, geometry,
degradation) and `docs/checklists/2026-09-08-plan-c-smoke.md` (tray, hotkeys,
startup, Settings).

### `verify-smoke.ps1`, 32 checks

Judges everything from disk and does not touch the app's UI: the Single
instance section, Three states bar the click-driven ones, the
config-corruption and file-safety half of Degradation. It backs up
`%LOCALAPPDATA%\StickyMD`, points the app at a scratch notes root, restores
afterwards and **asserts the restore worked** — the first version nested the
backup inside the folder it was meant to replace. It exercises
`WM_QUERYENDSESSION`, not the tray's Exit; that is the UI script's job. The
settings-fallback item touches the real notes root by design and names the
note it leaves rather than deleting it.

### `verify-smoke-ui.ps1`, 90 checks

Real keystrokes, real mouse, UI Automation for menus, bars and dialogs, and
screenshot comparison. `-Only <substring>` runs one block; without it,
debugging one check costs twenty app launches, and that load is itself a
source of the timing flakiness you then chase. Both scripts write throwaway
hotkeys, because a run that grabs `Ctrl+Alt+N` takes it from whoever is at
the keyboard.

Mechanisms that each cost a confident wrong answer before they were understood
— every one the same mistake, asserting on a state nobody established:

1. **`SetForegroundWindow` silently does nothing** for a process that is not
   already foreground. `AttachThreadInput` to the foreground thread lifts it.
2. **The WebView2 is a child HWND in another process.** While it holds focus
   the window-level `PreviewKeyDown` never sees a key. `SetFocus` on the WPF
   window fixes it. Clicking the header must NOT be used: `DragMove`'s nested
   message loop eats whatever is typed next.
3. **A fixed sleep after `Ctrl+E` is a race.** Poll UIA until the editor
   really holds focus.
4. **`InvokePattern.Invoke()` waits for the click handler to return**, so
   invoking `⋯ → Delete` deadlocks against the modal it opened. Click those by
   bounding rectangle.
5. **A teleported cursor is not a hover.** WPF decides `IsMouseOver` from move
   messages. Glide it.
6. **A screenshot tolerance has to be measured, not estimated.** One line of
   body text changing is `diff=0.0034`, not the ~1% a first guess assumed.
7. **A conflict balloon's Windows toast lands ON the notification area** and
   swallows the next tray click. The one block that wants a conflict waits the
   toast out.
8. **A PowerShell parameter named `$settings` shadows the script's own
   `$Settings` path**, because names are case-insensitive. The parameter is
   `$extraSettings`.

**The harness notices a human at the keyboard.** A failure recorded while the
pointer is somewhere the script did not put it is labelled possibly-tainted
rather than reported as a defect. A modal answered by hand nearly became a
"Delete does not ask first" data-loss report.

### What is genuinely left for a human

- **Mixed DPI**, and **the tray icon at 150%.** Need a display at a different
  scale.
- **The WebView2-runtime-missing dialog.** Needs the runtime uninstalled.
- **`App.DispatcherUnhandledException`.** Staging it means shipping a crash.
- **No white flash on first paint**, and **content staying sharp during a
  resize.** Sub-frame timing and a judgement call.
- **A pinned note above a fullscreen application.**
- **Unplugging or disabling DISPLAY2.**
- **Recycle Bin on a network share or removable drive.**
- **The startup entry surviving a reboot**, and **a `--startup` launch with
  `startHidden` on** at a real logon.
- **`explorer.exe` restarting under the app** (H.NotifyIcon's `TaskbarCreated`
  handling).
- **Holding a hotkey down.** The harness cannot produce a real auto-repeat, so
  `MOD_NOREPEAT` is reasoned about rather than observed.
- **The icon leaving the notification area on Exit.**
- **An external edit in a notes root that was changed at runtime.**
- **The corrupt-file tray balloons.** They never fired until a double load of
  the stores was removed on 2026-09-21; the checklist item is the way to prove
  they do now.

---

## Environment confirmed

|                  |                                                 |
| ---------------- | ----------------------------------------------- |
| .NET SDK         | 10.0.400                                        |
| Desktop runtime  | Microsoft.WindowsDesktop.App 10.0.11            |
| WebView2 Runtime | 151.0.4129.101 present — no bootstrapper needed |
| OS               | Windows 11 10.0.26200.0                         |
| Monitors         | 2 × 2560×1440, both 100%, DISPLAY2 at x=2560    |

**Packages:** `Markdig`, `Microsoft.Web.WebView2` 1.0.4129.50,
`H.NotifyIcon.Wpf` 2.4.1. Tests add `xunit` and `Shouldly`. NuGet sources are
disabled machine-wide; the repo-scoped `NuGet.config` at the solution root is
what makes `dotnet add package` work here at all.

---

## Where the reasoning lives

The spec for the design, its dated revision notes for where the code departs
from it, `docs/spikes/2026-08-22-spike-0-transparency.md` for why the window
renders the way it does, the code's own comments for the local why, and
`git log` for the rest. The commit messages explain the non-obvious code: the
U+FFFD substitution hazard on both read and write paths, the rename ambiguity
in the watcher, the trailing-newline rule, the percent-encoded traversal
bypass, and the `FileMode.CreateNew` limit that cannot be closed from inside
`NoteRepository`.
