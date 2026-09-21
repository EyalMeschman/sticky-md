# Plan C manual smoke checklist

The tray, the global hotkeys, the startup registry entry and the Settings
window. Run this at the end of Plan C and again after any change to
`TrayIconService`, `HotkeyManager`, `StartupManager` or `SettingsWindow`.

Spec §9 puts the tray and hotkeys outside unit testing. Most of what it meant by
that turns out to be scriptable after all, so this list is mostly a record of
what the harness already covers — and a short list of what it genuinely cannot.

**Environment this was written against:** Windows 11 10.0.26200, two 2560×1440
displays both at 100%, DISPLAY2 at x=2560, WebView2 runtime 151.0.4129.101,
.NET SDK 10.0.400.

## Run the script first

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-smoke-ui.ps1 -Only tray
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-smoke-ui.ps1 -Only hotkey
```

Or the whole pass with no `-Only`. **It takes real keyboard and mouse control
and it types**, so give it an unattended machine.

### 33 automated checks cover most of the list below

`verify-smoke-ui.ps1` runs 32 of them, across the `tray-menu`,
`tray-new-note`, `tray-recent`, `tray-toggle`, `tray-startup`, `tray-settings`,
`tray-exit`, `hotkey-global`, `hotkey-conflict` and `settings-root-failure`
blocks, and `verify-smoke.ps1` adds one more from disk:

- the icon appears, and the menu carries every item spec §7 asks for, in order
- New Note writes a `.md` and opens a window for it
- Recent Notes labels a note by its **heading** and check-marks the open one
- left-click hides everything and clicking again brings it back, `isOpen` intact
- Launch at Startup writes `"<exe>" --startup` to `HKCU\...\Run`, reads the tick
  back **out of the registry** on the next open, and removes the value when
  unticked
- Settings opens, shows the notes root actually in use, and its Save reaches
  `settings.json` without dropping the fields it did not display
- Exit shuts the app down through `OnExit`, and `isOpen` survives it
- both hotkeys fire **with no StickyMD window on screen**, which is the whole
  point of a global hotkey
- a hotkey that cannot be registered is named in `diagnostics.log`, flagged on
  the tray menu, and does not stop the app
- a notes root that cannot be created leaves the app **running**, opens Settings
  by itself on a banner naming the folder, and recovers **without a restart**
  when pointed somewhere writable — spec §8's behaviour, which Plan B could not
  do and answered with a dialog and a shutdown
- **New Note while that root is still unusable does not take the app down**, and
  the refusal is recorded rather than swallowed. That was a real defect Plan C
  introduced by staying alive, and it is the one thing here that would have
  destroyed unsaved text

Three things about that harness are worth knowing before you trust or distrust
a result from it, because each cost a confident wrong answer:

1. **The tray icon starts in Windows 11's hidden-icons overflow**, behind the
   `^` chevron. `Find-TrayIcon` opens the flyout and looks again. Nothing can
   promote the icon out of there but the user, by dragging it.
2. **A conflict balloon's Windows toast lands on the notification area** and
   covers the overflow flyout, swallowing the very next tray click. Two tray
   checks failed against a tray that worked perfectly. Both scripts now write
   throwaway hotkeys so no balloon fires, and the one block that wants a
   conflict waits the toast out.
3. **`IsChecked` alone is invisible to UI Automation.** WPF exposes
   `TogglePattern` only for a _checkable_ menu item, so the Recent Notes tick
   was on screen and unreadable by both the harness and a screen reader until
   `IsCheckable` was set alongside it.

## Tray

- [ ] The icon is visible in the notification area (open the `^` overflow if it
      is not on the taskbar) and its tooltip says **StickyMD**. _(automated)_
- [ ] The icon is the same yellow as a default note, and legible at 100% —
      **and at 150%**, which needs a scaled display.
- [ ] Right-click shows exactly: New Note, Recent Notes ▸, Open Note…,
      Show Notes ☑, ─, Settings, Launch at Startup ☑, ─, Exit. _(automated)_
- [ ] **New Note** creates a note in the notes root and opens it in edit mode
      with the caret in it. _(the file and the window are automated; the caret
      is not)_
- [ ] **Recent Notes** lists at most ten, newest-opened first, labelled by
      heading where a note has one and by filename where it does not.
      _(automated)_
- [ ] Recent Notes check-marks exactly the notes that have a window right now,
      not every note you have ever opened. _(automated)_
- [ ] Clicking a **ticked** Recent entry brings that window forward rather than
      opening a second one.
- [ ] Hovering a Recent entry shows the note's full path as a tooltip.
- [ ] **Open Note…** opens a file dialog **defaulted to the notes root**,
      accepts a `.md`, and opens it.
- [ ] `Open Note…` reaches a note with **no `notes.json` entry** — delete the
      index while the app is closed, restart, and open the note by path. This is
      the reason the item exists.
- [ ] `Open Note…` reaches a note **outside** the notes root, and it opens.
- [ ] **Show Notes** is ticked while any note is visible, unticked after hiding,
      and clicking it toggles; neither direction touches `isOpen`. _(the
      toggle and `isOpen` are automated)_
- [ ] **Left-click** toggles Show All / Hide All, single click, no double-click
      delay. _(automated)_
- [ ] Left-click with **zero** notes open does nothing — no new note, no error.
- [ ] **Exit** saves every dirty buffer and leaves `isOpen` set on every note.
      _(automated)_
- [ ] The icon **disappears from the notification area** on Exit, with no dead
      icon left behind until the shell re-polls.
- [ ] Closing the last note leaves the app running and reachable from the tray.
      This is the hazard Plan B carried and Plan C removes.

## Global hotkeys

- [ ] `Ctrl+Alt+N` creates and opens a note **from another application**, with
      no StickyMD window focused. _(automated, with a throwaway combination)_
- [ ] `Ctrl+Alt+S` hides every note, and again shows them. _(automated, ditto)_
- [ ] **Holding** `Ctrl+Alt+N` down creates **one** note, not one per keyboard
      repeat. That is `MOD_NOREPEAT`, and getting it wrong fills the notes root
      with real files.
- [ ] A combination another application already holds raises a balloon **naming
      the combination**, flags it on the tray menu, and the app keeps running
      with the other hotkey still working. _(automated)_
- [ ] Changing a hotkey in Settings takes effect **without a restart**, and the
      old combination stops working.
- [ ] Setting both hotkeys to the same combination flags the second, not the
      first. _(automated)_
- [ ] Unticking **Use global hotkeys** and saving releases both combinations at
      once (another app can now register them), greys out the two boxes but
      keeps their text, and removes the tray's ⚠ item if there was one.
      Re-ticking brings the same combinations back without a restart.

## Launch at Startup

- [ ] Ticking it writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
      value `StickyMD` = `"<exe>" --startup`, **quoted**. _(automated)_
- [ ] Unticking **removes the value** rather than writing a false. _(automated)_
- [ ] The tick reflects the registry, not a cached flag: delete the value by
      hand with the app running, reopen the menu, and it is unticked.
      _(automated)_
- [ ] Settings' checkbox and the tray item agree, both ways.
- [ ] Cancelling Settings after unticking leaves the registry **as it was** —
      startup is applied on Save, not on toggle.
- [ ] **It survives a reboot**: tick it, reboot, and StickyMD comes back with
      the notes it had. Needs an actual reboot.
- [ ] A startup launch does not steal focus from the logon sequence — notes come
      back `ShowActivated=false`.
- [ ] With **keep the notes hidden** ticked, launching with `--startup` puts no
      note on screen; Show Notes is unticked; one left-click brings back every
      note a normal launch shows. A launch **without** `--startup` still shows
      them.

## Settings

- [ ] Settings opens from the tray, and a second request **focuses the existing
      window** rather than opening a second one.
- [ ] Settings is **non-modal**: notes still scroll, edit and autosave while it
      is open.
- [ ] The notes-folder box shows the folder actually in use. _(automated)_
- [ ] **Browse…** opens a folder picker and fills the box in.
- [ ] Changing the notes folder to a new one: **New Note lands in the new
      folder** _(automated)_, an already-open note stays open and keeps saving
      to its own path, and an external edit **in the new folder** is still
      noticed. That last one is the watcher being repointed, and it is the half
      no script checks.
- [ ] A notes folder that cannot be created is refused **with the reason on the
      banner**, and nothing is saved — `settings.json` still holds the old one.
- [ ] The colour swatches show the seven real palette colours, the selected one
      is ringed, and a new note comes up in the chosen colour.
- [ ] Default opacity and default size apply to the **next** note created and
      leave existing notes alone.
- [ ] Theme Light / Dark / System re-themes **every open note immediately**, and
      System follows an OS light/dark flip.
- [ ] Clicking a hotkey box and pressing a combination fills it in canonically
      (`Ctrl+Alt+J`, whatever order you pressed it in).
- [ ] A combination with **no modifier** is refused: the box does not change.
- [ ] `Tab` still moves focus out of a hotkey box, and `Escape` still closes the
      window from inside one.
- [ ] A hotkey that failed to register is flagged **in red beside its box**.
- [ ] **Allow remote images** on: every open note re-renders and the remote
      image loads. Off again: a note that never opted in blocks it once more,
      and a note whose own bar you clicked **keeps** showing it for this
      session. That asymmetry is deliberate.
- [ ] Cancel and Escape both discard every change, including the colour swatch.
- [ ] Width or height typed as something that is not a number is refused on the
      banner rather than saved or silently clamped.

## Per-note text size

- [ ] `⋯ → Text size` shows a slider, and dragging it resizes the note's text
      live. _(automated)_
- [ ] The new size is written to that note's entry in `notes.json`, and only
      that note changes. _(automated)_
- [ ] A note that has never had a size takes the Settings default **without** a
      correction line in `diagnostics.log`. _(automated)_
- [ ] `Ctrl+E` shows the editor at the **same** size as the preview.
- [ ] Settings' _Text_ slider changes what a **new** note starts at and leaves
      existing notes alone.
- [ ] The size survives a close and reopen, and a restart. _(the reopen half is
      covered by a unit test; the restart is not)_
- [ ] At the maximum size a default-width note still lays out and scrolls.
- [ ] Headings, code blocks and blockquotes scale **with** the body text rather
      than staying put — they are `em` multiples of the one rule.

## Degradation

- [ ] A corrupt `settings.json` raises a **balloon** as well as a log line, and
      the app starts on defaults.
- [ ] A corrupt `notes.json` raises a balloon that says the `.md` files are
      untouched.
- [ ] A notes root that cannot be created: the app **stays alive in the tray**
      and opens Settings on a banner. Point it somewhere writable from there and
      it recovers without a restart. This replaced Plan B's dialog-then-exit.
      _(automated)_
- [ ] The tray icon still works after `explorer.exe` restarts —
      `TaskbarCreated` re-adds it.

## What is genuinely left for a human

Everything above without an _(automated)_ tag, and these in particular because
they need hardware or time the harness has not got:

- **The startup entry surviving a reboot.**
- **The tray icon at 150% DPI**, which needs a scaled display.
- **`explorer.exe` restarting** under the app.
- **Holding a hotkey down** — the harness sends discrete keystrokes and cannot
  produce a real auto-repeat.
- **The icon leaving the notification area on Exit**, which is a judgement about
  what the shell is showing rather than something readable from a process.
