# Plan B manual smoke checklist

Run this at the end of Plan B and again after any change to window chrome,
WebView2 hosting, or the save path. Spec §9 puts windows, WebView2, the tray,
and hotkeys outside unit testing and covers them here instead.

**Environment this was written against:** Windows 11 10.0.26200, two
2560×1440 displays both at 100%, DISPLAY2 at x=2560, WebView2 runtime
151.0.4129.101, .NET SDK 10.0.400.

## Run the two scripts first

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-smoke-ui.ps1
```

`verify-smoke-ui.ps1` drives the app with real keys and a real mouse and covers
the Editing and saving section, plus the header's resting glyphs, hover
brighten, and drag behaviour. It takes genuine foreground focus, so it steals
your keyboard while it runs.

31 of these items verify themselves from disk rather than on screen, and
`scripts/verify-smoke.ps1` does those: the whole **Single instance** section,
all of **Three states** except the ones needing a click, the config-corruption
and file-safety half of **Degradation**, and the three **Verify at the end**
commands. It backs up `%LOCALAPPDATA%\StickyMD` and restores it afterwards,
and points the app at a scratch notes root so the real folder is untouched.

It takes about two minutes, launches the app around fifteen times, and one
check deliberately raises a modal dialog. Its last line lists what it did
**not** cover; that list is the rest of this document.

One thing it cannot judge, by construction, and which stays here:

- **One note in the real notes root.** The garbage-`settings.json` item forces
  the app onto its DEFAULT root, which is the whole point of that item, so it
  creates a note in `~\StickyMD Notes`. The script names the file rather than
  deleting it; nothing here ever deletes from the real notes folder.

**Updated 2026-09-08, when Plan C's tray landed.** This list was written against
the temporary `Ctrl+Shift+Alt+Q` and `Ctrl+Shift+Alt+N` keys, which are **gone**
— they lived on a note window and existed only because Plan B had no tray. Every
item below that said "exit with `Ctrl+Shift+Alt+Q`" now means **the tray menu's
Exit**, and New Note is on the same menu. The hazard that came with them is gone
with them: closing the last note no longer strands the process, because the tray
is always there.

On Windows 11 the tray icon starts in the **hidden-icons overflow** behind the
`^` chevron rather than on the taskbar. That is Windows, not StickyMD, and an
app cannot promote itself out of it. Drag it onto the taskbar once and it stays.

## Chrome and transparency

- [ ] A note opens, renders Markdown, and sits on top of other windows when pinned.
- [ ] The note is visibly translucent at 90% and at 50%, **content included**.
- [ ] Resizing while translucent keeps the content sharp and correctly laid out.
- [ ] A pinned note stays above a **fullscreen** application.
- [ ] Corners are rounded.
- [ ] Dragging the header moves the note; dragging any edge or corner resizes it.
- [ ] The header glyphs are **faintly visible at rest** and brighten on hover.
      Not hidden-then-revealed: that was the original design and it made them
      undiscoverable on a dark note. See the 2026-09-05 revision note in the
      spec.
- [ ] There is no white flash on first paint.
- [ ] With notes open and Windows set to "System" theme (the default), switch
      Windows between light and dark in Settings: every open note's chrome
      **and** content re-theme without a restart, and a note that was moved
      but never saved does not jump back to its last saved position.

## The click-stealing regression check

Run these four together. **If all four fail, the drag handler moved to the
`Window`.** There is no error message when that happens.

- [ ] The mouse wheel scrolls the note content.
- [ ] The scrollbar thumb drags and scrolls, and does **not** move the window.
- [ ] Text inside the note can be selected by click-dragging.
- [ ] Clicking a task checkbox toggles it.

## Editing and saving

- [ ] `Ctrl+E` enters edit mode; `Esc` returns to preview with the edit visible.
- [ ] Double-clicking empty space enters edit mode; double-clicking a word selects the word instead.
- [ ] Typing, then waiting ~1s, puts the change on disk — verify in Notepad.
- [ ] `Ctrl+B` / `Ctrl+I` wrap and unwrap; empty selection leaves the caret between the markers.
- [ ] **`Ctrl+Z` after `Ctrl+B` undoes only the bold.**
- [ ] `Enter` continues `-`, `*`, `1.` and `- [ ]`; on an empty item it ends the list.
- [ ] `Tab` / `Shift+Tab` indent and outdent; `Shift+Tab` at column 0 does nothing; `Tab` never moves focus.
- [ ] Losing focus saves. Closing with `✕` saves.

## Checkboxes

- [ ] Clicking a box updates the `.md` file — verify the exact characters in Notepad.
- [ ] A nested box toggles itself and not its parent.
- [ ] `- [X]` (uppercase) toggles off.
- [ ] Rapid clicking never leaves a box disagreeing with the file.

## External edits

- [ ] Edit the note in VS Code and save: the note updates within ~200ms with **scroll preserved** and no bar.
- [ ] Type without saving, then edit and save in VS Code: the **Changed on disk** bar appears. **Reload** takes the file's version; **Keep Mine** writes yours to the file.
- [ ] Delete the note from Explorer: the **file is gone** bar appears. **Recreate** brings the file back with the buffer's content.
- [ ] Rename the note in Explorer: the title updates, the note stays open, and images in it still load.
- [ ] Move the note **out** of the folder: it reports as a deletion. Known v1 limitation; there is no Locate feature.

## Save failures

- [ ] Lock the file exclusively, type, and wait: the **Couldn't save** bar appears and the text stays in the editor.
- [ ] Release the lock and click **Retry**: it saves and the bar clears.
- [ ] With the file still locked, close the note: a snapshot appears under `%LOCALAPPDATA%\StickyMD\recovery\` containing the text and its `originalPath`.
- [ ] Release the lock, restart, reopen: the **Unsaved changes were recovered** bar appears. **Restore** writes it; **Discard** removes the snapshot.
- [ ] A later successful save deletes the snapshot file.

## Resources and links

- [ ] A relative image in the note directory loads.
- [ ] A `data:` image loads.
- [ ] A remote `https:` image shows a placeholder and the **remote images blocked** bar. Loading them works; reopening the note blocks them again.
- [ ] An absolute local path image (`C:\...`) shows a placeholder.
- [ ] `../outside.png` shows a placeholder. Known v1 limitation.
- [ ] An `https:` link opens in the default browser and the note does not navigate.
- [ ] A relative `.md` link opens that file as a second note.
- [ ] A `#anchor` link scrolls within the note.
- [ ] A `javascript:` link does nothing, and `diagnostics.log` records the refusal.
- [ ] Right-click shows no context menu; `F12` does nothing; `Ctrl+scroll` does not zoom.

## The ⋯ menu

- [ ] It contains exactly: Rename…, Color ▸, Opacity ▸, Always on Top ☑, ─, Delete.
- [ ] All seven colours apply to **both the chrome and the content**, and they match.
- [ ] Opacity applies immediately and survives a restart.
- [ ] Always on Top toggles and survives a restart.
- [ ] Rename moves the file on disk, updates the title, and keeps images loading.
- [ ] The Rename dialog preselects the current name — typing immediately replaces it rather than inserting into it.
- [ ] Clearing the Rename box's text disables the Rename button rather than doing nothing silently on click.
- [ ] Renaming onto an existing name is refused and **both files survive intact**.
- [ ] With **Always on Top enabled**, click Delete: the confirmation dialog is reachable and clickable, not hidden or blocked behind the pinned note.
- [ ] Delete asks first, then sends the file to the **Recycle Bin** — confirm it is listed there and restorable.
- [ ] **Do not run this against a network share or removable drive unless you mean to.** Delete a note whose notes root is on a removable drive or a network share: confirm the file is **gone, not recycled** — this is the known limitation from `RecycleBinService` (a drive with no Recycle Bin makes delete permanent even though the confirmation says "Send to the Recycle Bin"), not a bug to fix.

## Geometry and monitors

- [ ] **Close-and-reopen geometry.** Drag a note somewhere distinctive and
      resize it, close it with `✕`, then exit and relaunch. **It comes back at
      the place and size it had when you closed it**, not where it was when it
      opened. This catches the close-glyph geometry harvest
      (`WindowManager.CloseNote`); pinned headlessly by
      `WindowManagerTests.The_close_glyph_persists_where_the_note_actually_was`.
      A relative `[b](b.md)` link reopens a note within one session, if you
      want the faster loop.
- [ ] Drag a note to DISPLAY2, restart: it comes back on DISPLAY2 at the same place.
- [ ] Unplug or disable DISPLAY2 with a note on it: the note is clamped onto the remaining display **without a restart**.
- [ ] Change resolution with notes open: any off-screen note is clamped.
- [ ] **Mixed DPI — UNVERIFIED on this hardware.** If a display at a different scale is ever available, drag a note between them, restart, and check the placement. Spike 0 could not exercise this; it remains an assumption.

## The three states

The headline hazard. Get any of these wrong and the desktop empties.

- [ ] Open three notes. Exit from the **tray menu**. Restart. **All three come back.**
- [ ] Open three notes. Close one with `✕`. Restart. **All three come back** —
      `✕` means "off my screen", not "off my desktop set". Nothing in the app
      clears `isOpen` any more.
- [ ] Open three notes. `⋯ → Delete` one. Restart. **Two come back, and the
      third is in the Recycle Bin.** Deletion is now the only way a note leaves
      the restore set, which is what makes the item above safe.
- [ ] Hide All / Show All — **N/A until Plan C's tray.** There is no temporary
      Hide key (adding one would strand the app: with every window hidden
      there is no window left to press the exit key on, and no Show All until
      the tray). Covered headlessly in the meantime by
      `WindowManagerTests.HideAll_hides_every_window_and_leaves_isOpen_alone`
      and `.ShowAll_brings_hidden_windows_back`
      (`tests/StickyMD.App.Tests/Services/WindowManagerTests.cs`). Plan C must
      run this by hand once Hide All and Show All are reachable from the tray.
      **Show All alone is now reachable**: a bare relaunch of `StickyMD.exe`
      activates the running instance, which is Show All — see Single instance
      below.
- [ ] Log off and back on with notes open, then **launch StickyMD by hand** —
      there is no startup registry entry until Plan C, so it is not already
      running after logon. **All of the notes that were open come back.**
- [ ] Open the same note twice — by path and via a relative `.md` link. **One window, focused.**
- [ ] Add a `.md` to the notes root from Explorer. **No window appears.**

## Single instance

Pulled forward out of Plan C, and run BEFORE the rest of this list rather than
after it: every geometry, colour, opacity and three-states item above verifies
itself by reading `notes.json`, and a second process writes the whole snapshot
over the first one's. Four instances were running during the first manual
session and `2026-09-04-untitled.md` lost its index entry entirely, which in
Plan B makes a note unreachable — there is no Open command. With two writers a
real failure and a race look identical.

- [ ] With the app running, launch `StickyMD.exe` again. **Task Manager shows
      exactly one `StickyMD.exe`**, no new note is created, and the notes that
      were open come to the front.
- [ ] With the app running, `Start-Process StickyMD.exe -ArgumentList '<path to
      an OPEN note>'`. **One window, focused** — not a second window on the
      same file.
- [ ] Same again with a note that is **not** open: it opens in the running
      instance, and Task Manager still shows one process.
- [ ] `Start-Process StickyMD.exe -ArgumentList '--new'` with the app running:
      a new note appears in the running instance, in edit mode.
- [ ] Exit from the **tray menu**, then launch again. **It starts.** A guard
      that outlives the process it guards locks the user out of their own app.
- [ ] End `StickyMD.exe` from Task Manager, then launch again. **It starts**,
      and `%LOCALAPPDATA%\StickyMD\StickyMD.lock` is gone. This is the crash
      path: the lock is `FileOptions.DeleteOnClose`, so the kernel releases it
      whether the process exited or was killed.
- [ ] With the app **not** running, `Start-Process StickyMD.exe -ArgumentList
      '<path to a note that is not in the restore set>'`: it starts, restores
      the open notes **and** opens that one. A launch must not behave one way
      with the app up and another way with it down.

## Degradation

- [ ] A 3MB note opens in edit mode with the size bar and does not hang.
- [ ] An ANSI-encoded note opens read-only with the not-UTF-8 bar, and its bytes are unchanged afterwards.
- [ ] **A note whose file is locked BEFORE it opens cannot be truncated.** Put
      real content in a note, close it, then hold an exclusive lock on the file
      (`$f = [System.IO.File]::Open('C:\path\note.md','Open','Read','None')` in
      a separate PowerShell window) and open the note — through a relative link
      from another note, or by restarting with it marked open. The
      **couldn't be read** bar appears and the editor is read-only. **Now try
      to break it:** press `Ctrl+E`, double-click empty space, and type; wait
      several seconds. Then release the lock (`$f.Close()`) and open the file
      in Notepad. **Its full original content must still be there.** Before
      this was fixed the note rendered empty with no explanation and the first
      keystroke replaced the whole file with one character. Finally click
      **Retry**: the note loads, renders, and becomes editable again.
- [ ] **Nothing app-owned is left in the notes root.** After working through
      this list, look at the notes folder: only `.md` files the user created,
      no `.stickymd-tmp` leftovers, no HTML, no logs, no `notes.json`. A
      `note.md.stickymd-tmp` surviving means an atomic write was interrupted
      (killing the process mid-save does it) — record it rather than deleting
      it silently, because there is no cleanup for it and OneDrive will sync
      it. See STATUS.md's known limitations.
- [ ] **The WebView2 environment failing to build falls back to plain text
      rather than killing the app.** Exit StickyMD, delete
      `%LOCALAPPDATA%\StickyMD\WebView2`, and create a _file_ with that exact
      name in its place, then start the app: the note opens as an editable
      plain-text pane with the **viewer could not start** bar, `diagnostics.log`
      names the failure, and **the app stays up**. This is the reachable half
      of the last-resort error handling — the runtime is present, only the
      environment folder is unusable. Delete the file afterwards.
- [ ] **`App.DispatcherUnhandledException` is UNVERIFIED by hand, deliberately.**
      Two of the three paths that reached it — WebView2 environment creation
      and the `notes.json` write — now have local guards, and the item above
      exercises the first. The third does **not**:
      `NoteWindow.OnTaskToggleRequested` is still an unguarded `async void`,
      and a checkbox click racing a renderer teardown is answered by this
      handler rather than by a local catch. There is no way to stage that race
      by hand, and manufacturing a fourth trigger would mean shipping a
      deliberate crash. What the
      handler does if it ever fires — log to `diagnostics.log`, flush every
      dirty buffer through `ShutdownWithoutClosingNotes` (so `isOpen` survives
      and unsaveable buffers get recovery snapshots), name the log file in a
      dialog, then exit — is reviewed code, not tested behaviour. If a crash
      dialog with no `diagnostics.log` entry ever appears, that is the handler
      failing and it belongs in a bug report.
- [ ] Kill the WebView2 renderer from Task Manager: the note recreates **and
      its rendered content is visible again**. A blank-but-alive note is a
      FAILURE, not a pass — the recreated shell has to be handed the last
      render, and nothing else in the app will repaint it (the viewer-stall
      timer was stopped by the first successful render, so there is no bar and
      no timeout either). Kill it twice: the note falls back to a plain-text
      pane and stays readable.
- [ ] Corrupt `notes.json` by hand: the app starts, the file is kept as `notes.json.corrupt-1`, **every `.md` is intact**, and `diagnostics.log` says only geometry was lost.
- [ ] Put `{"color": 99}` in a `notes.json` entry: the note opens in the default colour and `diagnostics.log` names the correction.
- [ ] Set `"opacity": 0.0`: the note opens at 20%, visible and clickable.
- [ ] Point `settings.json`'s `notesRoot` at a nonexistent drive (e.g. `"Z:\\notes"`) and start the app: a warning dialog names the path, `diagnostics.log` records why, and **the app exits** rather than staying up with nothing to show.
- [ ] Write random garbage into `settings.json`, then start the app: it is kept as `settings.json.corrupt-1`, defaults are used, and `diagnostics.log` says so.
- [ ] Put `{"defaultOpacity": 5}` in an otherwise-valid `settings.json`, then start the app: `diagnostics.log` names the correction (clamped into `0.20`-`1.0`) and the note that opens uses the corrected default.
- [ ] **Not testable on this machine:** the WebView2-runtime-missing dialog (`App.ShowRuntimeMissingDialog`). The runtime is installed here and uninstalling it to exercise this path is not a reasonable ask — recorded as a known gap, not an omission.

## Verify at the end

- [ ] `dotnet test` — record the total and confirm `failed: 0`.
- [ ] `dotnet build` — zero warnings.
- [ ] `grep -rn --include=*.cs "System.Windows\|System.Drawing\|DllImport" src/StickyMD.Core/` must return nothing. (Without `--include=*.cs` this also matches built `.pdb` binaries and a NuGet version-constraint string in `obj/` — noise, not a violation — so the flag is not optional.)
