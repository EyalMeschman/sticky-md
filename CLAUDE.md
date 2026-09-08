# Working on StickyMD

Read `docs/STATUS.md` first — it is the orientation document and it is kept
accurate. This file is only the things that bite you in the first ten minutes
and are not derivable from the code.

## Running the app

`ShowInTaskbar="False"` on notes and no tray icon until Plan C, so the app has
no visible presence while running. The dev loop is kill, rebuild, launch:

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
entry entirely and become unreachable, because Plan B has no Open command.

The practical consequence for the dev loop: **`Start-Process StickyMD.exe`
against a running instance activates it rather than launching**, so the
`Stop-Process` line above is not optional. If you are testing by hand, exit with
`Ctrl+Shift+Alt+Q` (the temporary exit key) rather than killing the process, or
that run's geometry is not saved.

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
constructed on an xUnit thread. `docs/checklists/2026-09-02-plan-b-smoke.md` is
the real gate for anything involving windows, WebView2 hosting, or the save
path, and it has to be run by a human. Three of the four worst defects found so
far were invisible to 498 passing tests.

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

`docs/specs/2026-08-22-stickymd-design.md` is the binding authority, and two of
its sections have been **deliberately overridden** after the app was first run
by a human. Both carry a dated revision note saying what changed and why:
`isOpen` is now set once and never cleared (`✕` hides), and the display title
is a heading-or-filename rule with no first-non-empty-line fallback. If you
find another place the spec describes something the code does not do, that is
a bug in one of them — decide which, fix it, and date the note.
