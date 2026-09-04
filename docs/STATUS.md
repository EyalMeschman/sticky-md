# StickyMD — project status

**Read this first.** It is the orientation document for anyone picking this
project up, including a fresh assistant session with no prior context.

Last updated: 2026-09-04, after Plan B's whole-branch review and its single
fix wave.

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

| Plan  | Scope                                                    | Status                            |
| ----- | -------------------------------------------------------- | --------------------------------- |
| **A** | `StickyMD.Core` — the platform-free core                 | **Complete**                      |
| **B** | `StickyMD.App` — the WPF shell                           | **Complete — unverified by hand** |
| **C** | Shell services — tray, hotkeys, startup, single-instance | Not started — next                |

`StickyMD.Core` targets plain **`net10.0`**, deliberately _not_
`net10.0-windows`. That makes the spec's "zero WPF/Win32 in Core" boundary a
rule the **compiler enforces** — a stray WPF or Win32 reference fails the
build rather than surviving as a convention nobody checks. Everything in Core
is reachable from a headless test runner.

---

## Plan B is complete — the checklist is the real gate

Plan is `docs/plans/2026-09-02-stickymd-plan-b-wpf-shell.md`. **All 14 tasks
are implemented and tested**, the whole-branch review has run, and its single
fix wave is applied. What is left before Plan C is the checklist, by hand.

- **498 tests pass, 0 failed, 0 build warnings** (391 Core + 107 App).
- **Both of Plan A's open findings are resolved** — see Resolved findings
  below.
- **Nothing in Plan B has ever been run by a human.** Task 14 wrote the manual
  smoke checklist, `docs/checklists/2026-09-02-plan-b-smoke.md`, and it has not
  been executed. Five plan defects surfaced only when code actually ran during
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
- **The execution record lives in
  `.superpowers/sdd/2026-09-02-stickymd-plan-b-wpf-shell/progress.md`** (a
  git-ignored ledger), alongside `final-fix-wave.md` (the review's full findings
  and rulings) and `deferred-minors.md`. `git clean -fdx` destroys all of it;
  `git log` is the durable record.

## Where Plan A stands

**Complete at the time: 252 Core tests, 16 commits, zero build warnings.**
Those 252 are a historical snapshot of Plan A alone and a subset of today's
391 Core tests (498 total, Core + App — Plan B added both App tests and more
Core tests). Running `dotnet test` today returns the current total, not 252;
that number is not a command to reproduce.

### What Core contains

| Area        | Types                                                                                                   |
| ----------- | ------------------------------------------------------------------------------------------------------- |
| Notes       | `NoteFile`, `NoteFormat`, `NoteRepository`, `NoteWatcher`, `WriteLedger`, `NoteTitleResolver`, `IClock` |
| Editing     | `MarkdownEditOps` (emphasis, lists, indent — three partial files)                                       |
| Markdown    | `MarkdownRenderer`, `SourceSpanTaskListRenderer`, `TaskListToggler`                                     |
| Persistence | `AppPaths`, `JsonFile`, `NoteIndex`, `NoteIndexStore`, `AppSettings`, `SettingsStore`, `RecoveryStore`  |
| Theming     | `NotePalette`                                                                                           |
| Geometry    | `WindowPlacement`                                                                                       |

Deliberately **absent** from Core, and correctly so: window chrome,
transparency interop, WebView2 hosting, the tray, hotkeys, the startup
registry entry, the single-instance pipe, monitor enumeration, and Recycle Bin
deletion. All of it needs WPF or Win32 and belongs to Plans B and C.
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

### Known limitations carried into Plan C

- **Path identity** (above): canonicalisation resolves `..` and case, but not
  symlinks or 8.3 short names.
- **Recycle Bin deletion is not guaranteed to be recoverable.** A notes root on
  a network share, a removable drive, or a drive with the Recycle Bin disabled
  makes `⋯ → Delete` **permanently** delete the file: `SHFileOperationW`
  returns success and `File.Exists` reports the file gone, so
  `RecycleBinService` reports `Deleted` for a destroyed file, and the
  confirmation dialog's "Send to the Recycle Bin?" wording is inaccurate
  there. There is no reliable pre-flight check for it.
- **Closing the last open note strands the process with no reachable exit.**
  `CloseNote` never shuts the app down, `ShutdownMode` is
  `OnExplicitShutdown`, and both temporary keys (`Ctrl+Shift+Alt+Q`/`N`) live
  on a note window — with zero windows open there is nothing to press either
  on. The only way out is ending `StickyMD.exe` from Task Manager (which
  skips `OnExit` and loses that run's geometry) and relaunching. This is
  correct architecture, not a bug to fix here: shutting down at zero notes
  would be what Plan C's tray has to undo, since with a tray StickyMD must
  keep running with zero notes open so New Note stays reachable. Plan C's
  tray removes the hazard.
- **No single-instance guard.** Two StickyMD processes share one WebView2
  user-data folder and one `notes.json`, and neither knows about the other:
  whichever writes the index last wins, so one instance can silently undo the
  other's geometry and `isOpen` state. `CoreWebView2Environment.CreateAsync`
  against a user-data folder already held by another process is also a
  documented failure mode. Plan C owns the fix (the single-instance pipe);
  until then, do not run two copies.
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
- **A window displaced by a rename collision keeps saving to a path it no
  longer owns, and cannot be closed.** When a rename lands on a path that
  already has a note open, `WindowManager.Rekey` detaches the displaced window,
  drops it from `_windows` and shows it the "file is gone" bar so its text
  stays visible. What it cannot do is stop it saving: the window keeps its
  `SaveCoordinator` and its autosave timer, so an already-armed 500ms tick — or
  an `Editor.LostFocus` — writes the displaced text over the file that was just
  renamed into place, **with no user action at all**. Detaching is not
  optional (its `NotePath` is now the survivor's map key, so a live
  `CloseRequested` would close the wrong window), which also means its `✕`,
  `⋯ → Delete` and the bar's Close are all inert, and being out of `_windows`
  it never gets `SaveNow`/`Dispose` at exit. This is the honest floor of a
  detect-and-notify fix: **`INoteWindow` has no stop-saving verb, and Plan C
  must add one** as part of answering "one file, two owners". Low probability,
  real text loss.
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

## What is left

**Plan C — shell services.** Not yet written. Scope:

- Tray icon with a Recent Notes list (consumes `NoteTitleResolver.Resolve`)
- Global hotkeys (New Note, Show/Hide All), replacing the temporary
  `Ctrl+Shift+Alt+N`/`Q` keys Task 14 added
- Startup registry entry
- Single-instance pipe
- A Settings window (`allowRemoteImages`, default colour/opacity/size, theme,
  hotkeys, notes root)

---

## Contracts Plan C must honour

These are the non-obvious facts Plan C depends on. Getting any of them wrong
produces a bug that looks like it lives somewhere else.

1. **Nothing may clear `isOpen`, including `CloseNote`.** Revised 2026-09-04
   after the app was first run by hand: `✕` used to clear it, the first user
   closed three notes, relaunched, and read their absence as a restore bug.
   `✕` is now "off my screen" and `⋯ → Delete` is the only thing that removes a
   note from the restore set, taking the index entry with the file. So Plan C's
   tray gets no "forget this note" action short of deletion, and its Exit and
   Hide All must still use `ShutdownWithoutClosingNotes` and `HideAll`. What
   keeps the restore set bounded is unchanged: a `.md` merely present in the
   notes root spawns no window, so only notes the user actually opened come
   back.
2. **`NoteRepository.CreateNewRecorded` is not thread-safe.** The New Note
   hotkey must create notes on the UI thread, or add its own lock.
3. **`LastLoadIssues` on both stores is what the tray balloon should read.**
   `SettingsStore.LastLoadIssues` and `NoteIndexStore.LastLoadIssues` are
   already populated by Task 14's bootstrap and written to
   `diagnostics.log`; Plan C surfaces the same data as a balloon instead of
   only a log line.
4. **The temporary `Ctrl+Shift+Alt+Q`/`Ctrl+Shift+Alt+N` keys and
   `NoteWindow.NewNoteRequested` must be removed** once the tray lands — they
   exist only because Plan B has no tray and `ShutdownMode` is
   `OnExplicitShutdown`.
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
   its content.
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

---

## Environment confirmed

|                  |                                                 |
| ---------------- | ----------------------------------------------- |
| .NET SDK         | 10.0.400                                        |
| Desktop runtime  | Microsoft.WindowsDesktop.App 10.0.11            |
| WebView2 Runtime | 151.0.4129.101 present — no bootstrapper needed |
| OS               | Windows 11 10.0.26200.0                         |
| Monitors         | 2 × 2560×1440, both 100%, DISPLAY2 at x=2560    |

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
