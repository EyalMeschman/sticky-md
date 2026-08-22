# StickyMD — project status

**Read this first.** It is the orientation document for anyone picking this
project up, including a fresh assistant session with no prior context.

Last updated: 2026-08-23, after Plan A completed.

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

| Plan  | Scope                                                    | Status                        |
| ----- | -------------------------------------------------------- | ----------------------------- |
| **A** | `StickyMD.Core` — the platform-free core                 | **Complete**                  |
| **B** | `StickyMD.App` — the WPF shell                           | Not started (not yet written) |
| **C** | Shell services — tray, hotkeys, startup, single-instance | Not started (not yet written) |

`StickyMD.Core` targets plain **`net10.0`**, deliberately _not_
`net10.0-windows`. That makes the spec's "zero WPF/Win32 in Core" boundary a
rule the **compiler enforces** — a stray WPF or Win32 reference fails the
build rather than surviving as a convention nobody checks. Everything in Core
is reachable from a headless test runner.

---

## Where Plan A stands

**Complete. 252 tests, 252 passing, 16 commits, zero build warnings.**

Verify with:

```
dotnet test
```

Expect: `total: 252, failed: 0, succeeded: 252, skipped: 0`.

### There is nothing to run by hand yet

Plan A produced a **library**. There is no entry point, no window, no
executable — so there is no manual testing to do at this stage, and nothing
to click. `dotnet test` is the only verification available until Plan B
builds `StickyMD.App`. This is intentional, not an omission.

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

## Open findings — resolve these in Plan B, not later

Both were found by the whole-branch review at the end of Plan A and
deliberately deferred rather than designed hastily at the final gate. Neither
is a defect inside a single Core type; both are cross-component questions that
only have a right answer once the shell exists.

### 1. Four components disagree about what a note's path is

`NoteRepository.EnumerateRoot` returns verbatim-joined paths, while
`NoteWatcher` emits canonicalised ones. A path from one will not compare equal
to a path from the other. Any dictionary keyed by note path — the window
manager's open-notes map is the obvious one — will silently miss.

**Plan B must pick one canonical form and apply it at every boundary.**

### 2. Validation is absent throughout persistence

Deserialization checks shape, not values. `{"color": 99}` loads cleanly and
then crashes `NotePalette.Get`. The same holds for out-of-range window
geometry and unknown enum values.

**Plan B needs a validation pass on load**, clamping or defaulting invalid
values, consistent with "never die silently".

---

## What is left

**Plan B — the WPF shell.** Not yet written. Scope:

- `NoteWindow` chrome
- `MonitorEnumerator` (produces the `MonitorInfo` list Win32-side and feeds
  `WindowPlacement.Clamp`)
- `WebViewHost` with a shared `CoreWebView2Environment`
- `HtmlDocumentBuilder` — belongs in Core, but is written in Plan B alongside
  its only consumer rather than stranded in Plan A without one
- The render/edit mode toggle
- The checkbox message bridge (depends on contract 1 above)
- `IFileDeletionService` for Recycle Bin deletion
- The resource and navigation policy from spec §6

**Plan B must start from the Spike 0 findings, not from intuition** —
`docs/spikes/2026-08-22-spike-0-transparency.md`. The transparency and
WebView2 hosting approach was settled empirically there; re-deriving it from
first principles will reproduce the two traps listed above.

**Plan C — shell services.** Tray icon with a Recent Notes list (consumes
`NoteTitleResolver.Resolve`), global hotkeys, startup registry entry,
single-instance pipe.

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
