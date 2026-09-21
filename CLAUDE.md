# Working on StickyMD

Read `docs/STATUS.md` first — it is the orientation document and it is kept
accurate. This file is only the things that bite you in the first ten minutes
and are not derivable from the code.

## Running the app

`ShowInTaskbar="False"` on notes, so the tray icon is the app's only presence
apart from the notes themselves — and **on Windows 11 a new tray icon starts
life in the hidden-icons overflow**, behind the `^` chevron, not on the taskbar.
An app cannot promote itself out of there; drag it out once and it stays.
Looking for it on the taskbar and concluding the tray did not load has already
cost time. The dev loop is kill, rebuild, launch:

```powershell
Get-Process StickyMD -EA SilentlyContinue | Stop-Process
dotnet build C:\Users\Eyal\dev\sticky-md\StickyMD.sln
Start-Process C:\Users\Eyal\dev\sticky-md\src\StickyMD.App\bin\Debug\net10.0-windows10.0.17763.0\StickyMD.exe
```

**A second launch does not start a second process any more.** `SingleInstance`
(`src/StickyMD.App/Services/SingleInstance.cs`) holds
`%LOCALAPPDATA%\StickyMD\StickyMD.lock` open with `FileShare.None` from
`OnStartup`, and hands the losing launch's command line to the live instance
over a named pipe. That matters because every process holds its own
in-memory copy of `notes.json` and writes the _whole snapshot_ on every save,
so two of them silently clobber each other's rows — a note can lose its index
entry entirely and become unreachable. (`⋯ → Open Note…` on the tray is the
answer to that last part now, but the clobbering is still real.)

The practical consequence for the dev loop: **`Start-Process StickyMD.exe`
against a running instance activates it rather than launching**, so the
`Stop-Process` line above is not optional. If you are testing by hand, exit
through **the tray menu's Exit** rather than killing the process, or that run's
geometry is not saved. The temporary `Ctrl+Shift+Alt+Q`/`N` keys are gone.

**Launching the app registers the global hotkeys**, `Ctrl+Alt+N` and
`Ctrl+Alt+S` by default, and holds them until it exits. Both verification
scripts deliberately write throwaway combinations into `settings.json` instead,
because a run that grabs `Ctrl+Alt+N` takes it away from whoever is at the
keyboard — and because `Ctrl+Alt+S` is already held by something else on this
machine, which raised a conflict balloon on every launch whose Windows toast
then covered the notification area and swallowed the next tray click.

A running instance holds `StickyMD.exe` and makes `dotnet build` fail at the
copy step with MSB3027 naming the PIDs. That error means "the app is running",
not "the build is broken".

## App state lives outside the repo

|                                    |                                           |
| ---------------------------------- | ----------------------------------------- |
| Notes                              | `~\StickyMD Notes\*.md`                   |
| Index (geometry, colour, `isOpen`) | `%LOCALAPPDATA%\StickyMD\notes.json`      |
| Settings                           | `%LOCALAPPDATA%\StickyMD\settings.json`   |
| Diagnostics                        | `%LOCALAPPDATA%\StickyMD\diagnostics.log` |
| Recovery snapshots                 | `%LOCALAPPDATA%\StickyMD\recovery\`       |

`diagnostics.log` is where every quiet correction and refusal goes. Several
checklist items are verified by what lands there, not by what appears on
screen. Read it before concluding something did nothing.

## Two build traps

**XML comments cannot contain `--`.** This repo's comment style uses `--` as
a dash constantly, which is fine in C# and fatal in `.xaml` and `.props`. It
fails as `MC3000`/`MSB4024` pointing at a line and column, and it has cost two
build cycles already. Use a colon or a comma in markup comments.

**`InvariantGlobalization` must stay unset.** It is not there any more, and
putting it back crashes the app on the first note it opens — WPF's caret setup
calls `new CultureInfo(1033)`, which invariant mode refuses, from inside a
layout pass. Every `TextBox` is affected and no unit test can see it.
`NoteRepositoryTests.The_new_note_filename_date_does_not_follow_the_machine_calendar`
cannot run under invariant mode, so it fails first if anyone tries.

## Verification

```
dotnet build      # must be 0 warnings; TreatWarningsAsErrors is on
dotnet test       # must be failed: 0
```

`dotnet test` exercises the real Recycle Bin API in
`RecycleBinServiceTests.A_real_file_is_removed_from_disk`, so test runs
accumulate temp files in your Recycle Bin. That is deliberate — it is the only
way to test `SHFileOperationW` honestly.

Automated tests cannot reach the WPF shell at all: a `Window` cannot be
constructed on an xUnit thread. The checklists are the real gate for anything
involving windows, WebView2 hosting, the tray, the hotkeys or the save path:
`docs/checklists/2026-09-02-plan-b-smoke.md` and
`docs/checklists/2026-09-08-plan-c-smoke.md`. Three of the four worst defects
found so far were invisible to a fully green test run.

Most of both checklists is scripted:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-smoke-ui.ps1
```

`verify-smoke.ps1` judges everything from disk and does not touch the app.
`verify-smoke-ui.ps1` **takes real keyboard and mouse control and it types**, so
it needs an unattended machine — it notices a pointer it did not move and labels
any failure recorded then as possibly-tainted rather than reporting it as a
defect. Use `-Only <block>` while iterating; a full run costs an app launch per
block, and that load is itself a source of the timing flakiness you then chase.

## House rules

- **Never run git write commands.** `git add`, `commit`, `push` are denied.
  Hand over paste-able commands and let the user run them.
- **PowerShell, not bash**, for anything handed to the user. A bash-escaped
  commit message has already broken one commit here. For long commit messages,
  write them to a file and hand over `git commit -F <path>`.
- **No tooling attribution in commits.** No `Co-Authored-By`, no generator
  footers, no tool names. Commit messages explain the _why_ behind non-obvious
  code, in the voice of the repository's sole author.
- Subagents must not launch the app, raise dialogs, open a browser, touch the
  Recycle Bin, or leave a GUI process running. One did and it blocked the user
  at their desktop.

## When the spec and the code disagree

`docs/specs/2026-08-22-stickymd-design.md` is the binding authority, and four of
its sections have been **deliberately overridden**. Each carries a dated
revision note saying what changed and why:

- `isOpen` is set once and never cleared (`✕` hides).
- The display title is a heading-or-filename rule, with no
  first-non-empty-line fallback.
- The single-instance guard is a lock file, not a per-user named mutex.
- The tray menu grows one conditional item when a hotkey will not register.

If you find another place the spec describes something the code does not do,
that is a bug in one of them — decide which, fix it, and date the note.
