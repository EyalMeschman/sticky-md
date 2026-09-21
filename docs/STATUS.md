# StickyMD — project status

**Read this first.** It is the orientation document for anyone picking this
project up, including a fresh assistant session with no prior context.

Last updated: 2026-09-21, after a readability pass over the whole tree — see
"2026-09-21: the readability pass" below for what moved and what was removed.

Plan C landed 2026-09-08: the tray icon, the global hotkeys, the startup
registry entry and the Settings window. **StickyMD is feature-complete against
the spec.** What is left is a short list of checklist items that need hardware
or a reboot, and they are enumerated at the end.

---

## What this project is

A Windows sticky-notes app where **every note is a real `.md` file on disk**.

Windows Sticky Notes keeps notes in an opaque database. StickyMD's defining
property is the opposite: notes are plain Markdown files the user owns —
editable in Obsidian, VS Code, or Notepad, syncable through OneDrive or Git,
readable on GitHub, and outliving the app itself. Every design decision in
this repo serves that property.

Full spec: `docs/specs/2026-08-22-stickymd-design.md`.

The governing rule the whole codebase is written against:
**never lose text, never destroy a file, never die silently.**

---

## Architecture: three plans

Two projects, three phases of work.

| Plan  | Scope                                             | Status       |
| ----- | ------------------------------------------------- | ------------ |
| **A** | `StickyMD.Core` — the platform-free core          | **Complete** |
| **B** | `StickyMD.App` — the WPF shell                    | **Complete** |
| **C** | Shell services — tray, hotkeys, startup, Settings | **Complete** |

`StickyMD.Core` targets plain **`net10.0`**, deliberately _not_
`net10.0-windows`. That makes the spec's "zero WPF/Win32 in Core" boundary a
rule the **compiler enforces** — a stray WPF or Win32 reference fails the
build rather than surviving as a convention nobody checks. Everything in Core
is reachable from a headless test runner.

---

## Plan B is complete — the checklist is the real gate

**All 14 of the plan's tasks are implemented and tested**, the whole-branch
review has run, and its single fix wave is applied. The plan documents
themselves were removed on 2026-09-21 once the work was complete; `git log`
holds them (`docs/plans/` before that date) if the task-by-task record is ever
wanted.

- **557 tests pass, 0 failed, 0 build warnings** (423 Core + 134 App). Plan C
  added 27 Core tests (`HotkeySpecTests`, plus two in `StateValidatorTests`) and
  22 App tests (`StartupManagerTests`, plus the tray, Settings and
  unusable-notes-root additions to `WindowManagerTests`); per-note text size
  added 9 Core and 2 App; the 2026-09-21 readability pass removed 7 that only
  exercised members it deleted.
- **Both of Plan A's open findings are resolved** — see Resolved findings
  below.
- **The checklist is still the real gate, not the test count**, and Plan C
  added its own evidence for that: the tray's Recent Notes check mark was
  correct on screen and unreadable by UI Automation, and no unit test could
  have said so. Five plan defects surfaced only when code actually ran during
  Tasks 1-13 — including one where `NavigateToString` reports a
  `data:text/html` URI rather than `about:blank`, so the navigation policy was
  cancelling every note's shell load and **no note would have rendered at
  all**. The whole-branch review then found three more merge blockers in the
  same blind spot — a note whose file could not be read truncating that file on
  the first keystroke, a note left permanently blank after a WebView2 renderer
  crash, and no last-resort exception handler at all — none of which any unit
  test in this repo can reach. Treat the checklist as the real gate, not the
  test count.
- **The whole-branch review returned 3 Critical, 4 Important and 14 Minor, and
  triaged 17 deferred minor findings** (ledger items 1-19; the count reconciles
  because 7/8 and 17/18 are each two views of one gap). All of the Critical and
  Important findings and every cheap Minor were fixed in one wave. Of the
  deferred list: items 1, 2, 7, 8, 10, 11, 13, 14, 16 and **19** (the parked
  menu-geometry finding, which was missing from the earlier count) are **fixed**;
  items 3, 4, 5, 9, 12, 15, 17 and 18 were triaged **accept as is**; item 6 —
  the belt-and-braces canonical re-check in `NavigationPolicy` — was
  independently confirmed correct and kept, and turned out not to be
  unreachable at all (a drive-root notes folder was hitting it as a false
  positive; that half is fixed). One finding was **parked with the code
  untouched** and is recorded under known limitations: the shutdown flush can
  block the UI thread for roughly 1.3s per note whose saves are failing.
- **The execution record was a git-ignored scratch ledger** and is not part of
  the repository. `git log` is the durable record; the conclusions that matter
  are in this document and in the code's own comments.

## Where Plan A stands

**Complete at the time: 252 Core tests, 16 commits, zero build warnings.**
Those 252 are a historical snapshot of Plan A alone and a subset of today's
423 Core tests (557 total, Core + App — Plans B and C both added Core tests as
well as App tests). Running `dotnet test` today returns the current total, not
252; that number is not a command to reproduce.

### What Core contains

| Area        | Types                                                                                                   |
| ----------- | ------------------------------------------------------------------------------------------------------- |
| Notes       | `NoteFile`, `NoteFormat`, `NoteRepository`, `NoteWatcher`, `WriteLedger`, `NoteTitleResolver`, `IClock` |
| Editing     | `MarkdownEditOps` (emphasis, lists, indent — three partial files)                                       |
| Markdown    | `MarkdownRenderer`, `SourceSpanTaskListRenderer`, `TaskListToggler`                                     |
| Persistence | `AppPaths`, `JsonFile`, `NoteIndex`, `NoteIndexStore`, `AppSettings`, `SettingsStore`, `RecoveryStore`  |
| Theming     | `NotePalette`                                                                                           |
| Geometry    | `WindowPlacement`                                                                                       |
| Input       | `HotkeySpec` (Plan C — hotkey string → modifiers + virtual key, as plain `uint`)                        |

Deliberately **absent** from Core, and correctly so: window chrome,
transparency interop, WebView2 hosting, the tray, the hotkey registration
itself, the startup registry entry, the single-instance pipe, monitor
enumeration, and Recycle Bin deletion. All of it needs WPF or Win32 and belongs
to Plans B and C. `HotkeySpec` is the one hotkey piece that could come inside
the boundary, and it does: the Win32 constants it hands out are just numbers,
which is why `StateValidator` can refuse an unusable hotkey at load time.
`NoteRepository` has no delete method for exactly this reason — deletion goes
through Plan B's `IFileDeletionService`.

---

## Contracts Plan B must honour

These are the non-obvious facts Plan B depends on. Getting any of them wrong
produces a bug that looks like it lives somewhere else.

1. **Markdig `SourceSpan.End` is INCLUSIVE** — the index of the last
   character, not one past it. Verified empirically three times, independently.
   It holds for CJK, accented text, and emoji. `TaskListToggler` and the
   checkbox message bridge both depend on this. A span of `Start=2, End=4`
   covers three characters.
2. **`StickyMD.App` must target `net10.0-windows10.0.17763.0`.** The Windows
   version suffix is required by `WebView2CompositionControl`. Plain
   `net10.0-windows` throws `FileNotFoundException: Microsoft.Windows.SDK.NET`
   from inside `TryInitializeD3DImage()`, and the stack trace points at D3D
   while never mentioning the target framework.
3. **A `Window`-level `MouseLeftButtonDown` → `DragMove()` handler silently
   steals every click from the WebView.** No error, no warning. Checkboxes
   stop toggling, scrollbar drags move the window, text selection dies. Scope
   the drag handler to the header element only.
4. **`NotePalette` is the single source of truth for all seven colours.** Both
   the WPF chrome and the rendered HTML must read from it, or "WPF yellow" and
   "WebView yellow" drift apart. `ToCssVariables` is the interface
   `HtmlDocumentBuilder` consumes.
5. **Record every save into `IWriteLedger`.** That is what lets `NoteWatcher`
   distinguish the app's own writes from an external edit. Identity is the
   SHA-256 content hash plus byte length — never a timestamp.
6. **`NoteWatcher.Recovered` is Plan B's job to handle.** When the underlying
   watcher dies and is recreated, `NoteWatcher` deliberately does not decide
   which notes to re-read — it has no idea which are open. Plan B's window
   manager owns that, because that is where the knowledge lives.
7. **`ThemePreference` and `ThemeMode` are deliberately different types.**
   `ThemePreference` is what the user chose and includes `System`; `ThemeMode`
   is the resolved light-or-dark that `NotePalette` needs. Plan B resolves one
   into the other by reading the OS setting. Collapsing them would force
   `NotePalette` to answer a question it cannot see the input for.

---

## Resolved findings

Both were found by the whole-branch review at the end of Plan A, deliberately
deferred rather than designed hastily at the final gate, and are now resolved
in Plan B.

### 1. Path identity → `NotePath.Canonical` and `NotePath.Comparer`

`NoteRepository.EnumerateRoot` used to return verbatim-joined paths while
`NoteWatcher` emitted canonicalised ones, so a path from one would not compare
equal to a path from the other. Every boundary now produces `NotePath`'s one
canonical form — the repository, the watcher, the index store, and the window
manager's open-notes map all key on it, compared with `NotePath.Comparer`.

**Known limitation:** canonicalisation resolves `..` and case, but not
symlinks or 8.3 short names. Two paths that are the same file only through one
of those will still be treated as two different notes.

### 2. Validation → `StateValidator`, per-entry index loading, `LastLoadIssues`

`StateValidator.ValidateNote` and `.ValidateSettings` clamp or default every
persisted value that deserialises cleanly but is not usable — never reject,
per "never die silently". The actual bounds: opacity `0.20`-`1.0`
(`StateValidator.MinOpacity`/`MaxOpacity`), width/height `160`/`120` minimum up
to `8192` (`MinNoteWidth`/`MinNoteHeight`/`MaxNoteEdge`), coordinates clamped to
±`65536` (`MaxCoordinate`, guarding the `int` overflow in
`WindowPlacement`'s `X + Width`). `NoteIndexStore.Load` deserialises entries one
at a time, so one bad entry costs that note's geometry, not the whole file.
Every correction is recorded in `LastLoadIssues` and written to
`diagnostics.log` by the bootstrap.

### Known limitations that survive Plan C

- **Path identity** (above): canonicalisation resolves `..` and case, but not
  symlinks or 8.3 short names.
- **Recycle Bin deletion is not guaranteed to be recoverable.** A notes root on
  a network share, a removable drive, or a drive with the Recycle Bin disabled
  makes `⋯ → Delete` **permanently** delete the file: `SHFileOperationW`
  returns success and `File.Exists` reports the file gone, so
  `RecycleBinService` reports `Deleted` for a destroyed file, and the
  confirmation dialog's "Send to the Recycle Bin?" wording is inaccurate
  there. There is no reliable pre-flight check for it.
- ~~**Closing the last open note strands the process with no reachable
  exit.**~~ **FIXED by Plan C's tray, 2026-09-08.** `CloseNote` still never
  shuts the app down and `ShutdownMode` is still `OnExplicitShutdown` — both
  correct — but there is now always a tray icon carrying New Note and Exit, so
  zero notes open is an ordinary state rather than a dead end. The temporary
  `Ctrl+Shift+Alt+Q`/`N` keys that used to be the only way out are gone with it.
- **`.stickymd-tmp` files are written INTO the notes root.**
  `NoteFile.AtomicWriteBytes` writes `note.md.stickymd-tmp` beside the note and
  then `File.Replace`s it, which sits awkwardly beside the "nothing app-owned
  is ever written into the notes root" constraint. It is transient, and it is
  Plan A code with no better option — an atomic replace has to happen on the
  same volume, and a temp file elsewhere would silently become a copy. But a
  process killed mid-save leaves the file behind, there is no cleanup pass for
  it, and OneDrive will happily sync it. The constraint is therefore very
  nearly absolute rather than absolute. The smoke checklist now has a step that
  looks for leftovers.
- **The minimum note size is specified in two different units.**
  `NoteWindow.xaml`'s `MinWidth="160" MinHeight="120"` are **DIPs**;
  `StateValidator.MinNoteWidth`/`MinNoteHeight` are the same numbers in
  **physical pixels**. On a 150% display WPF enforces a 240x180px floor while
  the validator still considers 160x120px legal, so a note clamped to the
  validator's minimum comes back larger than it was saved. Left as it is
  deliberately: the defect is a DIP leaking into a physical-pixel design, and
  converting one number without the other only moves it. There is a comment at
  each end.
- **A window displaced by a rename collision cannot be closed.** Its
  automatic saves are now stopped: `INoteWindow.StopAutomaticSaves()` exists,
  `WindowManager.Rekey` calls it on the displaced window before showing the
  bar, and `NoteWindow.FlushAsync` refuses and logs. The text-loss half of this
  is **fixed** — an armed autosave tick used to land after the rename and write
  the displaced buffer over the file just renamed into place, with no user
  action at all. `⋯ → Delete` on the survivor's path, an explicit Recreate, and
  the shutdown flush are unaffected; Recreate deliberately clears the flag,
  because a click that says what it will do is a choice rather than a race.

  What remains is a UX wart, not data loss: the displaced window is out of
  `_windows`, so its `✕`, `⋯ → Delete` and the bar's Close are all inert, and
  it never gets `SaveNow`/`Dispose` at exit. **Still open after Plan C, and
  deliberately.** The earlier note said the tray was "the natural home" for
  the second owner concept this needs, and that turned out to be the wrong
  read: the tray gave the app an Exit and a New Note, neither of which is what
  makes a detached window closable. That wants an orphan list in
  `WindowManager` — windows it no longer owns but still has to dispose — and it
  is a change to the window-ownership model, not to the tray. It was left out
  of Plan C rather than bolted onto it, because Plan C's scope was the four
  shell services and a fifth owner state is exactly the kind of thing that
  wants its own review round. WPF still closes the window at exit, so nothing
  leaks; the buttons on it just do nothing until then.

- **`InvariantGlobalization` must stay unset.** It was set solution-wide in
  `Directory.Build.props` and made the app crash on the first note it ever
  opened: WPF's caret setup calls `InputLanguageSource.CurrentInputLanguage`,
  which does `new CultureInfo(1033)`, and invariant mode throws
  `CultureNotFoundException` from inside a layout pass. Every TextBox is
  affected, so the editor, the rename prompt and the plain-text fallback all
  die. Found by launching the app for the first time, after 497 green tests and
  three reviews had all passed it. `NoteRepository` now asks for
  `InvariantCulture` at the one call site that needs it, and
  `NoteRepositoryTests.The_new_note_filename_date_does_not_follow_the_machine_calendar`
  cannot even run under invariant mode, so re-adding the switch fails a test
  instead of the app.
- **The shutdown flush can block the UI thread ~1.3s per note whose saves are
  failing.** `ShutdownWithoutClosingNotes` calls `SaveNow` on every window, and
  `SaveCoordinator` runs its full retry schedule (100/300/900ms) synchronously
  on that thread. Ten notes with a locked or full notes root is a ~13s freeze
  at exit or logoff. Reviewed and **deliberately parked**: skipping the retries
  on the shutdown path needs a shutdown-mode flag threaded through
  `SaveCoordinator` and its tests, and a slow logoff when saves are _already_
  failing is milder than the risk of editing the save path. Fix it with a
  proper review round, not in passing.

---

## Plan C is complete: what was built, and what is worth knowing

Done 2026-09-08, in the order STATUS then recommended, and that order earned its
keep: the tray retired the temporary keys and unblocked everything after it.

### The tray

`src/StickyMD.App/Services/TrayIconService.cs`. **`H.NotifyIcon.Wpf` 2.4.1**,
which is what spec §7 named — the decision was re-made rather than inherited,
and the spec's reasoning held. `System.Windows.Forms.NotifyIcon` needs no
package at all, because WinForms ships in the same `Microsoft.WindowsDesktop.App`
runtime the app already uses. Its real cost is not the runtime: it is
`<UseWindowsForms>` plus a `<Using Remove="System.Windows.Forms" />` in **both**
the app and the test project to stop the implicit-usings clash on `MessageBox`
and `Application` from failing a build that treats warnings as errors, and a
WinForms-rendered menu sitting next to the WPF `⋯` menu on the same notes. One
`PackageReference` was the smaller change. Spec §7 carries a dated note saying
this was reconsidered and confirmed.

- **The menu is rebuilt on every open**, in `PreviewTrayContextMenuOpen`.
  H.NotifyIcon raises that before it reads `ContextMenu` and opens it — the
  library's own comment says the event exists so a client can set the menu on
  demand — so a menu assigned in the handler is the one that shows. It has to be
  rebuilt: Recent Notes, its check marks and the Launch at Startup tick are all
  live state, and the startup tick is read out of the **registry** each time.
- **`ForceCreate(enablesEfficiencyMode: false)`** is required because the icon is
  created in code rather than declared in XAML; without it the `TaskbarIcon`
  never reaches the loaded state that adds it to the notification area. The
  `false` is not decoration — efficiency mode defaults to **on**, and it applies
  EcoQoS process throttling to an app that hosts a WebView2 per note.
- **`IsChecked` alone is invisible to UI Automation.** WPF's `MenuItem`
  automation peer exposes `TogglePattern` only for a _checkable_ item, so the
  Recent Notes check mark was painted on screen and unreadable by both a screen
  reader and the harness until `IsCheckable = true` was set alongside it. The
  side effect — a click toggles the mark as well as opening the note — is
  unobservable, because the click closes the menu and the next open rebuilds
  every item.
- **The icon starts in Windows 11's hidden-icons overflow**, behind the `^`
  chevron, and no app can promote itself out of there. This is the first thing
  that looks like the tray failing to load. It is also why
  `verify-smoke-ui.ps1`'s `Find-TrayIcon` opens the flyout and looks again.
- The exe icon is now embedded **twice** from one `.ico`: `ApplicationIcon` for
  the Win32 resource Explorer reads, and a WPF `<Resource>` so the tray can open
  a `pack://` stream and pick the frame matching `SM_CXSMICON` — 16px at 100%,
  24px at 150%. `Icon(Stream, int, int)` selects the nearest frame instead of
  rescaling the largest one.

### The hotkeys

`HotkeyManager` plus `StickyMD.Core/Input/HotkeySpec.cs`. The parser is in
**Core**, because it is the only part of the feature with branches in it and
because `StateValidator` has to reject an unusable string at load — a hotkey
that cannot be parsed is a hotkey that silently never fires. The `MOD_*` and
virtual-key numbers are declared as plain `uint` constants, so Core's zero-Win32
boundary still holds.

- **The sink is a MESSAGE-ONLY window** (`HWND_MESSAGE`), not a hidden top-level
  one. A hidden top-level window still appears in `EnumWindows`, and
  `verify-smoke-ui.ps1` finds note windows by enumerating this process's
  windows — so a stray top-level window is a harness that miscounts notes.
- **`MOD_NOREPEAT` is OR'd in at registration.** Without it Windows repeats
  `WM_HOTKEY` while the combination is held, and a leant-on `Ctrl+Alt+N` creates
  notes at the keyboard repeat rate. Every one of them is a real file in the
  notes root.
- **At least one modifier is mandatory.** `RegisterHotKey` happily accepts a
  bare key and then swallows it system-wide for every other application: a
  "hotkey" of `N` makes the letter N unusable everywhere until StickyMD exits.
  `HotkeySpec` refuses one, the Settings capture box will not accept one, and
  `StateValidator` replaces one from a hand-edited file with the default.
- **`Ctrl+Alt+S`, a spec default, is already taken on this machine.** The
  conflict path is therefore not theoretical here: every launch on the defaults
  raises the balloon naming it. That is the spec's behaviour working, and
  Settings is where it gets fixed. It also bit the harness — see below.
- Hotkey strings are stored **canonically** (`Ctrl`, `Alt`, `Shift`, `Win`, then
  the key), so one combination has one spelling in `settings.json`.

### The startup entry

`StartupManager` behind an `IStartupRegistry` seam, exactly as spec §9 asks —
"the one App service with enough logic to be worth covering", and a test that
wrote to the real HKCU would change the developer's own logon.

**The registry is the only source of truth**, per spec §5: `settings.json` has
no `launchAtStartup` field and must never grow one, because cleanup tools, group
policy and third-party "startup managers" strip these entries behind the app's
back. Presence of the value **is** the state — a `0` or a `false` would still be
a Run entry, and Windows would still launch it.

**Known limitation:** enabled means "the value is present", not "the value
points at THIS exe". Rebuild the app somewhere else and the stale entry stays,
so the tray shows a tick for a path Windows may not be able to launch.
Re-ticking rewrites it, which is the whole repair. A launch-time comparison was
considered and left out: it puts a registry write on every start for the one
user whose install genuinely lives somewhere else.

### The Settings window

`SettingsWindow.xaml`. **Shown non-modal**, and that is load-bearing:
`ShowDialog` spins a nested dispatcher frame and would freeze every open note —
the watcher's marshalled handlers and the autosave ticks included — for as long
as Settings was open. `App` holds the single instance and activates it on a
second request, because two non-modal copies would each hold their own snapshot
of the settings and save over each other.

- `IsCancel` is **not** used on the Cancel button. It routes through
  `Window.DialogResult`, which only a `ShowDialog` window has. Escape is wired
  in code-behind instead.
- The hotkey boxes are **capture** fields, not free text. Every candidate goes
  through `HotkeySpec.TryParse`, so a key StickyMD cannot register just leaves
  the box as it was — which needs no error message, because nothing changed.
  WPF's `Key` names already match the parser's vocabulary except for the digits
  (`D0`..`D9`) and for `PageUp`/`PageDown`: WPF's enum declares `Prior` and
  `Next` first at the same values, so `Key.PageUp.ToString()` returns `"Prior"`.
  `HotkeySpec` carries both as aliases rather than the window carrying a second
  key table.
- Save goes through **`StateValidator.ValidateSettings`**, the same validator
  the loader uses. Settings must not be able to write a value that
  `settings.json` would quietly correct on the next start; the user would watch
  their own choice change by itself.
- **Order in `App.ApplySettings` matters.** The new notes root is proved usable
  BEFORE `settings.json` is written, and the file is written before the running
  app adopts the change — so a refused folder or a failed write leaves both the
  file and the app exactly as they were, with the reason on the banner. The
  alternative is an app running on settings its own file does not hold.
- **The notes root repoints live.** `WindowManager.ApplySettings` replaces the
  `NoteRepository`, and `App` restarts the `NoteWatcher` on the new root,
  because the watcher is App's and the manager cannot reach it. Half-applying
  this was the tempting shortcut and it is invisible when wrong: New Note keeps
  landing in the old folder while `settings.json` names the new one, and nobody
  finds out until they go looking for a note they just made. Notes already open
  stay open at their own absolute paths.
- Launch at Startup is applied **on Save, not on toggle**, so Cancel really
  cancels.

### Spec §8's notes-root failure now works as written

"Notes root missing → created on startup; if creation fails, Settings opens with
a banner and the app stays alive in tray." Plan B could do neither half and
answered with a `MessageBox` and `Shutdown()`. The app now stays up, balloons,
opens Settings on the banner, and **recovers without a restart** when pointed
somewhere writable. `verify-smoke-ui.ps1`'s `settings-root-failure` block drives
the whole path, and `verify-smoke.ps1`'s Degradation check now asserts the app
is still alive rather than that it exited.

### Three things that cost a confident wrong answer

Each one is the same mistake the Plan B harness notes already catalogue —
asserting on a state nobody established.

1. **A conflict balloon's Windows toast lands ON the notification area.** It
   covers the overflow flyout and swallows the very next tray click. Two tray
   checks failed against a tray that was working perfectly, and the screenshot
   is what settled it. Both scripts now write throwaway hotkeys so no balloon
   fires, and the one block that wants a conflict waits the toast out.
2. **`IsChecked` without `IsCheckable` is invisible to UI Automation** (above).
   The check mark was on screen and the assertion was correct to fail.
3. **A PowerShell parameter named `$settings` shadows the script's own
   `$Settings` path**, because variable names are case-insensitive. Every block
   then wrote `settings.json` to an empty string. The parameter is
   `$extraSettings`.

### Added 2026-09-09: per-note text size

The first change that came from using the app rather than from the plan, and the
question that prompted it was "can I make the text bigger?" — to which the
answer was no, in three separate ways. The shell stylesheet took
`HtmlShellOptions`' 14px default because `WebViewHost` never passed a value; the
editor had its own hardcoded `FontSize="14"` in XAML; and Ctrl+scroll zoom is
deliberately off per §6's WebView hardening. The only lever was Windows display
scaling, which enlarges the entire desktop to fix one note.

It is **per note**, in `notes.json` beside `color` and `opacity`, because that
is what it is — a property of this note, not of the app.
`AppSettings.DefaultFontSizePx` is only what a NEW note starts at, exactly like
`defaultColor` and `defaultSize`. Four things are worth knowing:

- **The number 16 is declared once**, on
  `HtmlDocumentBuilder.DefaultFontSizePx`, and `AppSettings` reads it from
  there. Two constants both meaning "the default text size" is how the CSS and
  `settings.json` come to disagree — the drift `NotePalette` exists to prevent
  for colour. It lives on the builder rather than on `HtmlShellOptions` for a
  language reason: a record's primary-constructor default cannot reference a
  const declared in its own body.
- **A missing `fontSizePx` is an UPGRADE, not a correction.** Every note in an
  index written before the field deserialises to zero, and `StateValidator`
  turns a zero into the default **silently**. Reporting it would write one line
  per note into `diagnostics.log` on the first run after an update and teach the
  reader to skim past the corrections that matter. An out-of-range value is a
  different thing and is clamped and reported like every other bound.
- **It is a SHELL input, not a content one**, so it joins the theme and the
  remote-image policy in `NoteWindow.ApplyState`'s staleness check. The size is
  a rule in the stylesheet of the page the WebView navigated to and every
  heading and code size is an `em` multiple of it, so changing it re-navigates
  rather than re-renders — and being in the one staleness check is what makes a
  Settings save re-navigate each note once instead of twice.
- **The slider's bounds come from `StateValidator`**, not from numbers in the
  view, in both the `⋯` menu and Settings. A control that can produce a value
  the validator then corrects means the user watches their own choice change by
  itself on the next start.

`Editor.FontSize` is now set in `ApplyTheme` from the note's state, and the XAML
literal is gone: preview and edit mode showing text at different sizes makes
Ctrl+E look like it reformatted the note. Spec §5 and §6 both carry dated
revision notes.

### One defect Plan C introduced, and closed

Keeping the app alive with an unusable notes root — which spec §8 asks for and
Plan B could not do — put an **unguarded throw one click away from the user.**
`NoteRepository.CreateNewRecorded` begins with `EnsureRootExists`, which throws
for a root that is not there: an unplugged drive, a dropped network share, a
folder deleted from under the app. Before Plan C that was unreachable, because
such a root exited the app during startup. Afterwards it was reachable from the
tray's New Note, from the hotkey, and from `--new` — and from a Click handler it
reaches `DispatcherUnhandledException`, so **one New Note on a disconnected
share closed the whole app and took every other note's unsaved buffer with it.**

`WindowManager.CreateAndOpenNote` now returns `string?` and reports the refusal
instead of throwing. Guarded there rather than at the three call sites, because
that is where they all route through. `TrayIconService.NewNote` is the one place
that turns a null into a balloon naming the folder, and the hotkey and `--new`
both go through it rather than reaching the manager directly — a hotkey action
that failed would otherwise be swallowed into `diagnostics.log` by
`HotkeyManager`'s own guard, and a hotkey that quietly does nothing is exactly
what "never die silently" forbids.

Found by reading the new startup path against `CreateNewRecorded`, not by any
test or script. `WindowManagerTests` now pins the null return, and
`verify-smoke-ui.ps1`'s `settings-root-failure` block presses the hotkey while
the root is still unusable and asserts the app is still there afterwards.

### What Plan C did NOT do

The rename-collision wart. The earlier note here said the tray was "the natural
home" for the second owner concept a displaced window needs, and that was the
wrong read — see the known limitation above. It stays open on purpose.

### 2026-09-21: the readability pass

A whole-tree review for dead code and duplication, with the logic left alone.
What a reader picking the code up now should know:

- **`WindowManager` takes the already-loaded `AppSettings` and `NoteIndex`**
  instead of the `SettingsStore` and loading them itself. That is a
  simplification with one behavioural consequence: the stores were being
  loaded **twice** at startup — once by `App.OnStartup`, once by the manager's
  constructor — and the second load reset `LastCorruptBackupPath` to null after
  the corrupt file had already been moved aside, so **the two corrupt-file tray
  balloons in `App.ReportStartupStateToTray` never actually fired.** They do
  now. The corresponding item in `docs/checklists/2026-09-08-plan-c-smoke.md`
  is still unticked and is the way to prove it.
- **`HtmlDocumentBuilder.VirtualHost` is the one definition of `note.local`.**
  It was a literal in four files and a configurable-but-never-configured
  parameter on `HtmlShellOptions` and `RenderOptions`. The renderer, the CSP,
  `WebViewHost`'s mapping and `NavigationPolicy` all read the constant.
- **`NoteState.Bounds` and `NoteState.WithBounds(PixelRect)`** replace the six
  hand-written `state with { X = …, Y = …, W = …, H = … }` copies.
- **Removed as dead:** `InlineBarHost.DismissAll`/`IsShowing`,
  `RecoveryStore.LoadAll`, `NoteRepository.CreateNew` (callers use
  `CreateNewRecorded`), `WriteLedger.Normalize` (use `NotePath.Canonical`),
  `WriteOutcome.LastWriteUtc` and `WriteFingerprint.LastWriteUtc` (never
  compared, by design, and never read), and the null-hash branch of
  `ExternalChangePolicy` that no caller could reach.
- **Comments no longer narrate history.** References to "Plan A/B/C", task
  numbers and "until the app was first run by hand" were rewritten to state the
  rule that survives. The plan documents they pointed at are gone; the spike
  document and this file remain, and `git log` has the rest.

## What is left

**Nothing in the spec.** All of §7's cross-cutting services exist, §8's error
table is implemented end to end, and the two verification scripts are green.

What remains is checklist items that need hardware, a reboot, or a judgement
call — enumerated under "What is genuinely left for a human" below and in
`docs/checklists/2026-09-08-plan-c-smoke.md`.

### Done 2026-09-04: single instance, pulled forward out of Plan C

Built first, before the checklist, because **the checklist could not be trusted
without it.** Every geometry, colour, opacity and three-states item verifies
itself by reading `notes.json`, and a second instance can overwrite that file
between two of them. It had already happened once: four instances were running
during the first manual session and `2026-09-04-untitled.md` lost its index
entry entirely, which in Plan B makes a note unreachable because there is no
Open command. Against a file with two writers a real failure and a race look
identical, so the list would have had to be run twice.

`SingleInstance` (`src/StickyMD.App/Services/SingleInstance.cs`) is a per-user
lock file plus a named pipe, taken in `App.OnStartup` before the
`WebViewEnvironment.DetectRuntimeVersion()` check and before anything reads or
writes app state. A losing launch hands its command line down the pipe and
exits. What is worth knowing about it:

- **The guard is a LOCK FILE, and the spec's "per-user named mutex" now carries
  a dated revision note saying why.** A named mutex is per-user only in the
  `Global\` namespace, which needs `SeCreateGlobalPrivilege` and so is closed to
  a standard non-elevated user; a `Local\` one is per LOGON SESSION, and one
  user gets a second session just by remoting into a machine they are already
  logged into at the console — two processes, one `notes.json`, which is the
  failure the whole service exists to prevent. `%LOCALAPPDATA%` is per-user by
  construction. The file is opened `FileShare.None` with
  `FileOptions.DeleteOnClose`, so a crash, a Task Manager kill and a clean exit
  all release it identically and there is no stale-lock case to reason about.
- **Only `IOException` means "somebody else is running".** An
  `UnauthorizedAccessException` means the guard could not be TAKEN, which is a
  different thing: that path logs and starts anyway, because refusing would turn
  an unwritable `%LOCALAPPDATA%` into an app that never starts again.
- **`PipeOptions.CurrentUserOnly` is on both ends** per spec 7, and the pipe name
  carries the user's SID — the pipe namespace is machine-wide and the server
  allows one instance, so without it two logged-on users could not both listen.
- **`App.ApplyLaunchArgs` is spec 7's command protocol**, and this process's own
  command line goes through it too. A launch must not behave one way with the
  app already up and another way with it down. No args activates (`ShowAll`),
  `--new` creates, anything not starting with `-` is a path for
  `WindowManager.OpenNote`, which is already idempotent per path and already
  focuses an existing window. Unrecognised flags are ignored rather than passed
  to `OpenNote`, which would refuse each one into `diagnostics.log`;
  `--startup` needs no handling because `RestoreOpenNotes` is unconditionally
  `ShowActivated=false` already.
- **Known limitation, and the deliberate cost of choosing per-user over
  per-session:** a second logon session of the same user hands its note to the
  instance running on the OTHER session's desktop, where the user cannot see it.
  Nothing is lost, and the alternative was two writers on `notes.json`. Plan C's
  tray does not change this; a fix would mean comparing session ids and telling
  the user, and it is not worth the plumbing until someone hits it.

### The disk-judged half: `verify-smoke.ps1`, 32 checks, all green

`scripts/verify-smoke.ps1` runs every item whose verdict lives on disk rather
than on screen, and **32 checks pass mechanically**: the whole Single instance
section, Three states bar the click-driven ones, the config-corruption and
file-safety half of Degradation, and the three Verify-at-the-end commands. It
takes two minutes and can be re-run after any change, which is what the
checklist header asks for and nobody would do by hand.

It backs up `%LOCALAPPDATA%\StickyMD`, points the app at a scratch notes root,
restores afterwards and **asserts the restore worked** — that last check exists
because the first version did not do it: `Remove-Item` cannot clear the
WebView2 folder while the renderer's handles are still closing, so `Copy-Item`
nested the whole backup inside the folder it was meant to replace and put the
user's state one level too deep. Nothing was lost, and it looked exactly like
something had been.

Two things about it are worth knowing:

- **It does not touch the tray.** The shutdown path it exercises is
  `WM_QUERYENDSESSION` — a real product path, the one the log-off item is about
  — and it asserts what that path promises: `notes.json` rewritten, `isOpen`
  intact. Proving the tray's **Exit item** is `verify-smoke-ui.ps1`'s job.
- **The settings-fallback item touches the real notes root**, because forcing
  the app onto its DEFAULT root is the item. The script names the note it
  leaves rather than deleting it.
- Both scripts now write **throwaway hotkeys** into `settings.json`. A run that
  registers `Ctrl+Alt+N` globally takes it away from whoever is at the keyboard
  for the length of the run, and `Ctrl+Alt+S` is already held on this machine,
  so the defaults raised a conflict balloon on every launch.

### The interaction items are automated too

`scripts/verify-smoke-ui.ps1` drives the running app: real keystrokes, real
mouse, UI Automation for menus, bars and dialogs, and screenshot comparison for
anything whose answer is on screen. **90 checks** — 54 from Plan B, 32 that
Plan C added, and 4 for per-note text size. With the 32 in `verify-smoke.ps1`
that is **122 automated checks**, covering roughly 60 of the Plan B checklist's
92 items and most of the Plan C checklist.

It covers the Editing and saving section, the header chrome, the whole `⋯` menu
through Rename and Delete-to-the-Recycle-Bin, the close glyph, the tray menu's
Exit, checkbox clicks, images (relative, `data:`, absolute, outside-root,
remote-blocked), links (`.md`, `#anchor`, `javascript:`), the WebView hardening
(no context menu, no devtools, no zoom), external edits (reload, delete and
Recreate, rename re-keying the index), and save failures (the couldn't-save
bar, Retry, and the recovery snapshot).

`-Only <substring>` runs a single block, and it matters: without it, debugging
one check costs twenty app launches, and that load is itself a source of the
timing flakiness you then go and chase.

Nine mechanisms each cost a confident wrong answer before they were understood
— six from Plan B and three from Plan C, listed under "Three things that cost a
confident wrong answer" above. Every one is the same mistake: asserting on a
state nobody established.

1. **`SetForegroundWindow` silently does nothing** for a process that is not
   already foreground. `AttachThreadInput` to the foreground window's thread
   lifts it. Without that, the temporary `Ctrl+Shift+Alt+Q` exit key was
   written off as unscriptable twice, and it became a passing check — which is
   what made repointing that check at the tray's Exit a rename rather than a
   rewrite.
2. **The WebView2 is a child HWND in another process.** While it holds keyboard
   focus every key goes to the browser and the window-level `PreviewKeyDown`
   never sees it. `SetFocus` on the WPF window fixes it. Clicking the header
   also fixes it and must NOT be used: that click fires `DragMove`, whose
   nested modal message loop eats whatever is typed next.
3. **A fixed sleep after `Ctrl+E` is a race.** `Enter-EditMode` polls UIA until
   the editor really holds keyboard focus, and retries.
4. **`InvokePattern.Invoke()` waits for the click handler to return**, so
   invoking `⋯ → Delete` deadlocks against the modal it just opened. Click
   those by bounding rectangle.
5. **A teleported cursor is not a hover.** WPF decides `IsMouseOver` from move
   messages. Glide it: 41.9 at rest, 47.1 on the header, 52.1 on a glyph.
6. **A screenshot tolerance has to be measured, not estimated.** One line of
   body text changing is `diff=0.0034`, not the ~1% a first guess assumed, and
   a threshold set from that guess reported a broken file watcher against a
   watcher that worked perfectly. `Get-ShotDiff` returns the ratio and each
   call site picks its own side of `$ShotSame`.

**The harness now notices a human at the keyboard.** Every cursor move goes
through one place, and a failure recorded while the pointer is somewhere the
script did not put it is labelled as possibly-tainted rather than reported as a
defect. This is not paranoia: a modal dialog answered by hand nearly became a
"Delete does not ask first" data-loss report, and a note scrolled by hand nearly
became "long notes open at the wrong scroll position". Both were false, both
looked completely solid, and the app was correct in both cases.

### What is genuinely left for a human

Thirteen items across both checklists, and only these. From Plan B:

- **Mixed DPI.** Needs a display at a different scale. Already flagged
  UNVERIFIED on this hardware.
- **The WebView2-runtime-missing dialog.** Needs the runtime uninstalled.
- **`App.DispatcherUnhandledException`.** Already deliberately unverified;
  staging it means shipping a crash.
- **No white flash on first paint**, and **content staying sharp during a
  resize.** Sub-frame timing and a judgement call.
- **A pinned note above a fullscreen application.**
- **Unplugging or disabling DISPLAY2.**
- **Recycle Bin on a network share or removable drive** — the known limitation,
  and it needs hardware someone has to choose.

And from Plan C:

- **The startup entry surviving a reboot.** Needs a reboot; nothing else will
  do, since the whole claim is about what Windows does at logon.
- **The tray icon at 150% DPI.** Same missing hardware as mixed DPI, and it is
  the frame-selection code in `LoadTrayIcon` that wants proving.
- **`explorer.exe` restarting under the app**, which is what H.NotifyIcon's
  `TaskbarCreated` handling is for.
- **Holding a hotkey down.** The harness sends discrete keystrokes and cannot
  produce a real auto-repeat, so `MOD_NOREPEAT` is reasoned about rather than
  observed.
- **The icon leaving the notification area on Exit.** A judgement about what
  the shell is showing, not something readable from a process.
- **An external edit in a notes root that was changed at runtime.** The
  repository half is automated; the watcher half is the same launch and nobody
  has watched it.

Everything else on either list passes mechanically or is the same kind of work
as what does.

---

## Contracts the shell services honour

These are the non-obvious facts Plan C was built against, kept in the present
tense because they still bind anything that touches the tray, the hotkeys, the
startup entry or Settings. Getting any of them wrong produces a bug that looks
like it lives somewhere else.

1. **Nothing may clear `isOpen`, including `CloseNote`.** Revised 2026-09-04
   after the app was first run by hand: `✕` used to clear it, the first user
   closed three notes, relaunched, and read their absence as a restore bug.
   `✕` is now "off my screen" and `⋯ → Delete` is the only thing that removes a
   note from the restore set, taking the index entry with the file. **Honoured
   by the tray:** it has no "forget this note" action, its Exit goes through
   `Application.Shutdown()` into `OnExit` and `ShutdownWithoutClosingNotes`,
   and Hide All is `HideAll`. What keeps the restore set bounded is unchanged: a
   `.md` merely present in the notes root spawns no window, so only notes the
   user actually opened come back. That is also why Recent Notes ticks
   "a window exists right now" rather than the index's `isOpen`, which is true
   for every note ever opened and would tick the whole list.
2. **`NoteRepository.CreateNewRecorded` is not thread-safe.** The New Note
   hotkey must create notes on the UI thread, or add its own lock. **Honoured
   for free:** `WM_HOTKEY` is dispatched on the UI thread, so
   `HotkeyManager`'s hook already runs there and no lock was needed.
3. **Only the two CORRUPT-file cases get a balloon**, not every entry in
   `LastLoadIssues`. `App.ReportStartupStateToTray` balloons a `settings.json`
   or `notes.json` that had to be moved aside, and the notes-root failure;
   `diagnostics.log` keeps the full per-field record. A clamped opacity is a
   correction the user will not miss, and a balloon per correction trains them
   to dismiss the one that matters.
4. **The temporary `Ctrl+Shift+Alt+Q`/`Ctrl+Shift+Alt+N` keys and
   `NoteWindow.NewNoteRequested` are GONE**, removed in the same change that
   added the tray, along with `WindowManager`'s `window is NoteWindow` check
   for them. Both `scripts/verify-smoke-ui.ps1` blocks that drove the exit key
   now drive the tray's Exit instead. Do not reintroduce a note-hosted key:
   anything the app must be able to do with zero notes open cannot live on a
   note window.
5. **`SystemTheme.Changed` and `SystemEvents.DisplaySettingsChanged` fire off
   the dispatcher.** Every handler marshals onto the UI thread before touching
   a window — see `App.OnSystemThemeChanged` and `App.OnDisplaySettingsChanged`,
   both of which just `Dispatch(...)` into `WindowManager`.
6. **The remote-image opt-in is per note, per session, and deliberately
   unpersisted.** The global equivalent belongs in Settings; do not make the
   per-note bar's choice sticky.
7. **`HtmlDocumentBuilder`'s CSP is per shell.** A meta-tag CSP is fixed at
   parse time, so a Settings change to `allowRemoteImages` must re-navigate
   every open note's shell (`NoteWindow.ReloadShellAsync`), not just re-render
   its content. **This is why `allowRemoteImages` is a parameter of
   `INoteWindow.ApplyState`** rather than a method of its own: two entry points
   each deciding whether the shell is stale re-navigate twice for one Settings
   save, and the note flashes each time. One method, one staleness check, one
   re-navigation. The window keeps the global flag and the per-note bar's
   session opt-in as separate fields and ORs them, so turning the global off
   does not revoke a yes the user already gave for one note.
8. **Anything that raises `StateChanged` must stamp live geometry through
   `NoteWindow.CurrentState()`.** `WindowManager.Persist` replaces the whole
   index entry, and `_state`'s rect is only as fresh as the last event that
   wrote it — so a handler that passes `_state` straight through persists the
   geometry the note had when it opened and silently reverts every move since.
   The `⋯` menu's colour, opacity and pin items, the header `PinButton` and
   `OnClosing` all go through `CurrentState()` now; `CloseNote` and
   `ShutdownWithoutClosingNotes` harvest `window.Bounds` themselves because
   `Detach` has already removed the subscription by then. A new Plan C entry
   point that skips this reintroduces the bug at that call site only, which is
   why it is worth knowing rather than rediscovering.
9. **`WindowManager.OnSystemThemeChanged` early-returns when the resolved
   `ThemeMode` has not changed, and `NoteWindow.ApplyState` re-navigates the
   shell only when the theme, colour or remote-image inputs actually differ.**
   Windows raises `UserPreferenceChanged` for accent colour, wallpaper and a
   broad slice of `WM_SETTINGCHANGE` traffic, not only light/dark. Both guards
   exist on purpose: the first stops the storm, the second keeps `ApplyState`
   safe for callers that do not know about the first.
10. **`settings.json` must never grow a `launchAtStartup` field.** The HKCU Run
    key is the single source of truth, per spec §5, and both the tray item and
    the Settings checkbox read it back on every open. Cleanup tools, group
    policy and third-party startup managers strip those entries behind the
    app's back, so a cached copy shows a tick for something that is not there.
11. **The `NoteWatcher` belongs to `App`, not to `WindowManager`.** So a notes
    root change has to be applied in two places: `WindowManager.ApplySettings`
    replaces the `NoteRepository`, and `App.ApplySettings` restarts the watcher
    on `_manager.Repository`. Doing only the first leaves the app watching a
    folder that is no longer the notes root while missing every edit in the one
    that is — and doing only the second leaves New Note creating files in the
    old folder while `settings.json` names the new one, which nobody discovers
    until they go looking for a note they just made.
12. **Nothing in the app may occupy a top-level window it does not intend to
    show.** `verify-smoke-ui.ps1` finds note windows by enumerating this
    process's visible `HwndWrapper` windows, so a stray one makes the harness
    miscount notes. That is why `HotkeyManager`'s sink is a message-only
    window (`HWND_MESSAGE`) rather than a hidden top-level one. `SettingsWindow`
    IS such a window, deliberately and visibly, so any check that counts notes
    has to close Settings first.

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

**`Ctrl+Alt+S` is already taken on this machine.** It is one of the spec's two
hotkey defaults, so a launch on the defaults raises the conflict balloon every
time — the feature working, not a bug, and Settings is where it gets changed.
Both verification scripts write throwaway combinations instead of the defaults.

---

## Where the reasoning lives

Plan A was built test-first and reviewed at every step; 23 defects were found
and fixed before it was committed. That review record was kept in a scratch
directory that is **gitignored and will not survive `git clean -fdx`**, so the
durable conclusions have been lifted into this document and into the commit
messages, which explain the _why_ behind the non-obvious code: the U+FFFD
substitution hazard on both read and write paths, the rename ambiguity in the
watcher, the trailing-newline rule, the percent-encoded traversal bypass, and
the `FileMode.CreateNew` limit that cannot be closed from inside
`NoteRepository`. `git log` is the reliable record; the ledger is scratch.
