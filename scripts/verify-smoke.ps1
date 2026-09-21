<#
.SYNOPSIS
    The scriptable third of docs/checklists/2026-09-02-plan-b-smoke.md.

.DESCRIPTION
    Every item on the smoke checklist whose verdict lives on DISK rather than on
    screen: the startup config-corruption handling, the single-instance guard,
    the index and three-states bookkeeping, and the file-safety sweeps. It sets
    up a state, launches the app, waits, kills it, and asserts against
    notes.json, diagnostics.log and the notes root.

    It does NOT replace the checklist. Roughly 25 items are genuinely visual
    (translucency, rounded corners, colour matching, white flash) and another
    25 need clicks and keystrokes; those stay human. This script exists so the
    human pass is 50 items of judgement rather than 92 items of clerical work.

    SAFETY. The whole of %LOCALAPPDATA%\StickyMD is copied aside before
    anything runs and restored in a finally block, and the app is pointed at a
    scratch notes root under %TEMP% so the real notes folder is never opened,
    written, or deleted from. It refuses to start if StickyMD is already
    running, because a second instance is the exact thing under test.

    EXPECT WINDOWS. This launches the real app around fifteen times. Notes will
    appear and vanish on your desktop for a minute or two, and one check
    deliberately raises a modal dialog, which the script dismisses by ending
    the process. Do not run it in the middle of something.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-smoke.ps1
#>
[CmdletBinding()]
param(
    # Resolved in the body, not here: $PSScriptRoot is not reliably populated
    # while a param block's defaults are being evaluated under PowerShell 5.1.
    [string]$Exe,

    # WebView2 initialisation is the slow part of a launch. Raise it if checks
    # start failing on a loaded machine; a check that fails because the app was
    # still starting is worse than a slow run.
    [int]$SettleSeconds = 6
)

$ErrorActionPreference = 'Stop'

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) {
    $Exe = Join-Path $Here '..\src\StickyMD.App\bin\Debug\net10.0-windows10.0.17763.0\StickyMD.exe'
}
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if ($resolved) { $Exe = $resolved.Path }
$AppData    = Join-Path $env:LOCALAPPDATA 'StickyMD'
$IndexFile  = Join-Path $AppData 'notes.json'
$Settings   = Join-Path $AppData 'settings.json'
$LogFile    = Join-Path $AppData 'diagnostics.log'
$LockFile   = Join-Path $AppData 'StickyMD.lock'
$WebViewDir = Join-Path $AppData 'WebView2'
$Scratch    = Join-Path $env:TEMP 'stickymd-smoke'
$NotesRoot  = Join-Path $Scratch 'notes'
$Backup     = Join-Path $env:TEMP ('stickymd-smoke-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

$Utf8NoBom  = New-Object System.Text.UTF8Encoding $false
$Results    = [System.Collections.Generic.List[object]]::new()

# ---------------------------------------------------------------- window count

# Note windows set ShowInTaskbar=False, so MainWindowHandle is not reliable.
# EnumWindows filtered to visible WPF top-levels owned by the process is. The
# count is always printed, so a heuristic that miscounts shows up as a wrong
# number rather than as a silently wrong verdict.
if (-not ('Win32Windows' -as [type])) {
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class Win32Windows
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);

    public static List<IntPtr> HandlesFor(int processId)
    {
        var found = new List<IntPtr>();
        EnumWindows((h, p) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != (uint)processId || !IsWindowVisible(h)) return true;

            var name = new StringBuilder(256);
            GetClassName(h, name, name.Capacity);

            // WPF names every top-level HwndWrapper[...]; the dispatcher's own
            // message window is never visible, so visibility already excludes it.
            if (name.ToString().StartsWith("HwndWrapper")) found.Add(h);
            return true;
        }, IntPtr.Zero);

        return found;
    }

    public static int CountFor(int processId) { return HandlesFor(processId).Count; }

    /// Every top-level window of the process, INVISIBLE ONES INCLUDED. WPF
    /// raises Application.SessionEnding from its own hidden message window, not
    /// from any note, so the visible-only list above cannot reach it.
    public static List<IntPtr> AllHandlesFor(int processId)
    {
        var found = new List<IntPtr>();
        EnumWindows((h, p) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == (uint)processId) found.Add(h);
            return true;
        }, IntPtr.Zero);

        return found;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    /// Drives the app out through WM_QUERYENDSESSION / WM_ENDSESSION, which is
    /// exactly what Windows sends at logoff and what WPF turns into
    /// Application.SessionEnding. Needs no keyboard focus, so it works from a
    /// script with no foreground rights -- and it is a real product path, not a
    /// test hook: it is the one the checklist's log-off item is about.
    public static void EndSession(IntPtr hWnd)
    {
        IntPtr r;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        SendMessageTimeout(hWnd, 0x0011, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 5000, out r);
        SendMessageTimeout(hWnd, 0x0016, (IntPtr)1, IntPtr.Zero, SMTO_ABORTIFHUNG, 5000, out r);
    }
}
'@
}

# --------------------------------------------------------------------- helpers

function Write-Utf8($path, $text) {
    [System.IO.File]::WriteAllText($path, $text, $Utf8NoBom)
}

function Check($section, $name, [bool]$ok, $detail) {
    $Results.Add([pscustomobject]@{ Section = $section; Check = $name; Pass = $ok; Detail = $detail })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}" -f $mark, $name) -ForegroundColor $colour
    if (-not $ok -and $detail) { Write-Host ("         {0}" -f $detail) -ForegroundColor DarkGray }
}

function Get-App { Get-Process StickyMD -ErrorAction SilentlyContinue }

function Stop-App {
    Get-App | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 40 -and (Get-App); $i++) { Start-Sleep -Milliseconds 100 }
}

function Start-App {
    param([string[]]$Arguments = @())
    if ($Arguments.Count -gt 0) { Start-Process $Exe -ArgumentList $Arguments | Out-Null }
    else                        { Start-Process $Exe | Out-Null }
    Start-Sleep -Seconds $SettleSeconds
}

<#
    Drives the app through Application.SessionEnding, the logoff path.

    It does NOT expect the process to exit, and asserting that it does would be
    wrong: App.OnSessionEnding flushes and deliberately does not call Shutdown,
    because at a real logoff Windows is what ends the process. What this path
    promises is that every dirty buffer is written and every note's geometry
    persisted WITHOUT isOpen being cleared -- so the evidence is notes.json
    moving on disk, not the process disappearing.

    The messages go to every top-level window of the process because WPF raises
    SessionEnding from its own hidden message window, not from a note.
#>
function Invoke-SessionEnding {
    $p = Get-App
    if (-not $p) { return $false }

    foreach ($h in [Win32Windows]::AllHandlesFor($p.Id)) { [Win32Windows]::EndSession($h) }
    Start-Sleep -Seconds 3
    return $true
}

function Get-Log {
    if (Test-Path $LogFile) { Get-Content $LogFile -Raw } else { '' }
}

function Get-Index {
    if (Test-Path $IndexFile) { Get-Content $IndexFile -Raw | ConvertFrom-Json } else { $null }
}

function New-Note($name, $body) {
    $path = Join-Path $NotesRoot $name
    Write-Utf8 $path $body
    return $path
}

function Set-Index($openPaths) {
    $notes = [ordered]@{}
    foreach ($p in $openPaths) {
        $notes[$p] = [ordered]@{
            x = 200; y = 200; w = 300; h = 340; monitor = $null
            color = 'Yellow'; opacity = 1; alwaysOnTop = $false
            isOpen = $true; lastOpenedUtc = (Get-Date).ToUniversalTime().ToString('o')
        }
    }
    Write-Utf8 $IndexFile (([ordered]@{ version = 1; notes = $notes } | ConvertTo-Json -Depth 6))
}

<#
    Rewrites notes.json through a regex and REFUSES to continue if the pattern
    matched nothing. PowerShell 5.1's ConvertTo-Json emits "key":  value with
    two spaces, so a pattern written against the app's own formatting silently
    matches nothing -- and a check that then asserts "the bad value was
    corrected" passes without ever having written a bad value. Vacuous green is
    worse than red.
#>
function Edit-Index($pattern, $replacement) {
    $before = Get-Content $IndexFile -Raw
    $after = $before -replace $pattern, $replacement
    if ($after -eq $before) { throw "Edit-Index pattern matched nothing: $pattern" }
    Write-Utf8 $IndexFile $after
}

function Set-Settings($extra) {
    # Hotkeys that nothing is likely to hold, rather than the product defaults.
    # This script does not exercise hotkeys at all, and registering Ctrl+Alt+N
    # globally would take it away from whoever is at the keyboard for the two
    # minutes of a run. Ctrl+Alt+S is also already taken on this machine, which
    # would raise a conflict balloon on every one of the launches below.
    $s = [ordered]@{
        notesRoot      = $NotesRoot
        newNoteHotkey  = 'Ctrl+Alt+Shift+F9'
        showHideHotkey = 'Ctrl+Alt+Shift+F10'
    }
    if ($extra) { foreach ($k in $extra.Keys) { $s[$k] = $extra[$k] } }
    Write-Utf8 $Settings (($s | ConvertTo-Json -Depth 4))
}

<#
    A clean slate for one check: no index, no log, no recovery snapshots, a
    fresh scratch notes root, and settings pointing at it. Every check starts
    here so a diagnostics.log assertion can only match a line THIS check
    produced.
#>
function Reset-State {
    Stop-App
    Remove-Item $IndexFile, $Settings, $LogFile -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $AppData 'notes.json.corrupt-*') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $AppData 'settings.json.corrupt-*') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $AppData 'recovery') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $NotesRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $NotesRoot -Force | Out-Null
    Set-Settings $null
}

# ------------------------------------------------------------------ preflight

if (-not (Test-Path $Exe)) { throw "StickyMD.exe not found at $Exe. Run dotnet build first." }

if (Get-App) {
    throw 'StickyMD is already running. Exit it first: a second instance is the thing under test.'
}

Write-Host ''
Write-Host 'StickyMD scripted smoke pass' -ForegroundColor Cyan
Write-Host ('  exe        {0}' -f $Exe)
Write-Host ('  scratch    {0}' -f $NotesRoot)
Write-Host ('  state kept {0}' -f $Backup)
Write-Host ''

New-Item -ItemType Directory -Path $Backup -Force | Out-Null
if (Test-Path $AppData) {
    Copy-Item $AppData -Destination (Join-Path $Backup 'StickyMD') -Recurse -Force
}

# The real notes folder, listed so the one check that cannot avoid it (the
# settings.json fallback, which by definition uses the DEFAULT root) can report
# what it left rather than the user finding it next week.
$RealRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'StickyMD Notes'
$RealBefore = @(Get-ChildItem $RealRoot -Filter *.md -ErrorAction SilentlyContinue | ForEach-Object FullName)

try {
    # ============================================================ Degradation
    Write-Host 'Degradation: startup state handling' -ForegroundColor Cyan

    # --- corrupt notes.json
    Reset-State
    $note = New-Note 'alpha.md' "# Alpha`n`nreal content that must survive.`n"
    Write-Utf8 $IndexFile '{ this is not json at all'
    Start-App
    $log = Get-Log
    Check 'Degradation' 'Corrupt notes.json is kept as notes.json.corrupt-1' `
        ((Test-Path (Join-Path $AppData 'notes.json.corrupt-1')) -and $log -match 'notes\.json was unusable') $log
    Check 'Degradation' 'Corrupt notes.json leaves every .md intact' `
        ((Get-Content $note -Raw) -match 'real content that must survive') 'alpha.md was modified'
    Check 'Degradation' 'diagnostics.log says only geometry was lost' `
        ($log -match 'only window positions were lost') $log

    # --- an unusable colour in an entry
    Reset-State
    $note = New-Note 'colour.md' "# Colour`n"
    Set-Index @($note)
    Edit-Index '"color":\s*"Yellow"' '"color": 99'
    Start-App
    Stop-App
    $log = Get-Log
    $entry = (Get-Index).notes.$note
    Check 'Degradation' 'An unusable colour falls back to a real colour rather than being rejected' `
        ($null -ne $entry -and $entry.color -is [string] -and $entry.color.Length -gt 0) `
        ('colour persisted as: ' + $entry.color)
    Check 'Degradation' 'The colour correction is named in diagnostics.log' `
        ($log -match '(?i)colou?r') $log

    # --- opacity 0.0 clamps to the floor
    Reset-State
    $note = New-Note 'opacity.md' "# Opacity`n"
    Set-Index @($note)
    Edit-Index '"opacity":\s*1' '"opacity": 0.0'
    Start-App
    Stop-App
    $entry = (Get-Index).notes.$note
    Check 'Degradation' 'opacity 0.0 is clamped to the 0.20 floor, not left invisible' `
        ($null -ne $entry -and [double]$entry.opacity -ge 0.2) ("opacity persisted as " + $entry.opacity)

    # --- an unreachable notes root keeps the app alive in the tray
    #
    # CHANGED 2026-09-08, when Plan C landed. This check used to be "exits
    # rather than staying up empty", and it asserted on the log rather than on
    # the exit code because the warning was a MODAL dialog that owned the
    # process until something ended it. Spec 8 always said "Settings opens with
    # a banner and the app stays alive in tray"; Plan B could not do that half,
    # having neither, so it showed the dialog and shut down. Both are gone. The
    # app now stays up, and verify-smoke-ui.ps1's settings-root-failure block
    # checks that Settings is really on screen with the banner.
    Reset-State
    Set-Settings @{ notesRoot = 'Z:\notes' }
    Start-Process $Exe | Out-Null
    Start-Sleep -Seconds $SettleSeconds
    $log = Get-Log
    Check 'Degradation' 'An unreachable notesRoot is recorded in diagnostics.log' `
        ($log -match 'could not be created') $log
    Check 'Degradation' 'An unreachable notesRoot leaves the app ALIVE rather than exiting' `
        ($null -ne (Get-App)) 'the app exited instead of staying up in the tray'
    Stop-App

    # --- garbage settings.json
    Reset-State
    New-Note 'settings-garbage.md' "# Settings`n" | Out-Null
    Write-Utf8 $Settings 'not json, just words'
    Start-App
    $log = Get-Log
    Check 'Degradation' 'Garbage settings.json is kept as settings.json.corrupt-1 and defaults are used' `
        ((Test-Path (Join-Path $AppData 'settings.json.corrupt-1')) -and $log -match 'settings\.json was unusable') $log
    Stop-App
    # That run had NO usable settings.json, so by design it fell back to the
    # DEFAULT notes root, which is the user's real one. It is the only check
    # here that can touch it, and with no index either the app will have
    # created a note there. Reported, never deleted: this script does not
    # remove files from the real notes folder under any circumstances.
    $realAfter = @(Get-ChildItem $RealRoot -Filter *.md -ErrorAction SilentlyContinue | ForEach-Object FullName)
    $strays = @($realAfter | Where-Object { $RealBefore -notcontains $_ })
    $missing = @($RealBefore | Where-Object { $realAfter -notcontains $_ })

    # The new untitled note is the PROOF the fallback took effect, not a
    # failure: with no settings and no index the app is supposed to land in the
    # default root and give the user something. Anything else appearing, or
    # anything disappearing, is not.
    $expected = @($strays | Where-Object { $_ -match '\d{4}-\d{2}-\d{2}-untitled.*\.md$' })
    Check 'Degradation' 'The settings fallback really used the DEFAULT notes root' `
        ($expected.Count -eq 1) ('new files: ' + (($strays -join '; ') -replace '^$', 'none'))
    Check 'Degradation' 'The fallback run added nothing else and removed nothing' `
        ($strays.Count -eq $expected.Count -and $missing.Count -eq 0) `
        ('unexpected: ' + ($strays | Where-Object { $expected -notcontains $_ }) + ' missing: ' + ($missing -join '; '))
    if ($expected.Count -gt 0) {
        Write-Host ('         note left in the real root by design: ' + ($expected -join '; ')) -ForegroundColor DarkGray
    }

    # --- defaultOpacity out of range
    Reset-State
    New-Note 'default-opacity.md' "# Default`n" | Out-Null
    Set-Settings @{ defaultOpacity = 5 }
    Start-App
    $log = Get-Log
    Check 'Degradation' 'defaultOpacity 5 is clamped and the correction is named' `
        ($log -match '(?i)opacity') $log

    # --- WebView2 folder replaced by a file
    Reset-State
    New-Note 'webview.md' "# WebView`n" | Out-Null

    # The renderer's handles under this folder outlive the process by a moment,
    # so a single Remove-Item right after the kill loses a race and the write
    # below then fails with "access denied" -- which is a script bug wearing a
    # product bug's clothes. Retry, and say so plainly if it never clears.
    for ($i = 0; $i -lt 20 -and (Test-Path $WebViewDir); $i++) {
        Remove-Item $WebViewDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $WebViewDir) { Start-Sleep -Milliseconds 250 }
    }

    if (Test-Path $WebViewDir) {
        Check 'Degradation' 'An unusable WebView2 folder is logged and the app STAYS UP' $false `
            'SKIPPED: the WebView2 folder could not be cleared, so the check never ran. Run this item by hand.'
    } else {
        Write-Utf8 $WebViewDir 'not a directory'
        Start-App
        $alive = [bool](Get-App)
        $log = Get-Log
        Stop-App
        Remove-Item $WebViewDir -Force -ErrorAction SilentlyContinue
        Check 'Degradation' 'An unusable WebView2 folder is logged and the app STAYS UP' `
            ($alive -and $log.Length -gt 0) ("alive=$alive; log=$log")
    }

    # ======================================================== Single instance
    Write-Host ''
    Write-Host 'Single instance' -ForegroundColor Cyan

    Reset-State
    $a = New-Note 'one.md'   "# One`n"
    $b = New-Note 'two.md'   "# Two`n"
    $c = New-Note 'three.md' "# Three`n"
    Set-Index @($a)
    Start-App

    Check 'Single instance' 'The lock file exists while the app is running' `
        (Test-Path $LockFile) $LockFile

    $before = (Get-ChildItem $NotesRoot -Filter *.md).Count
    Start-App
    Check 'Single instance' 'A bare relaunch leaves exactly one process' `
        ((Get-App | Measure-Object).Count -eq 1) ('processes: ' + (Get-App | Measure-Object).Count)
    Check 'Single instance' 'A bare relaunch creates no new note' `
        ((Get-ChildItem $NotesRoot -Filter *.md).Count -eq $before) 'a note appeared on a bare relaunch'

    $windows = [Win32Windows]::CountFor((Get-App).Id)
    Start-App @($a)
    $after = [Win32Windows]::CountFor((Get-App).Id)
    # The -gt 0 is not decoration. Without it, a window counter that returned
    # zero for everything would make this check pass by seeing nothing twice.
    Check 'Single instance' 'Relaunching with an OPEN note gives one window, not two' `
        ($windows -gt 0 -and $after -eq $windows) `
        ('windows before=' + $windows + ' after=' + $after)

    Start-App @($b)
    Stop-App
    Check 'Single instance' 'Relaunching with a NOT-open note opens it in the live instance' `
        ($null -ne (Get-Index).notes.$b) 'two.md never reached the index'

    Reset-State
    $a = New-Note 'one.md' "# One`n"
    Set-Index @($a)
    Start-App
    $before = (Get-ChildItem $NotesRoot -Filter *.md).Count
    Start-App @('--new')
    Check 'Single instance' '--new creates exactly one note in the live instance' `
        ((Get-ChildItem $NotesRoot -Filter *.md).Count -eq $before + 1) `
        ('notes before=' + $before + ' after=' + (Get-ChildItem $NotesRoot -Filter *.md).Count)
    Check 'Single instance' '--new does not start a second process' `
        ((Get-App | Measure-Object).Count -eq 1) ('processes: ' + (Get-App | Measure-Object).Count)

    # The crash path. Ending the process skips OnExit entirely, which is the
    # point: DeleteOnClose has to release the lock without the app's help.
    Stop-App
    Check 'Single instance' 'Killing the process releases the lock file' `
        (-not (Test-Path $LockFile)) 'the lock survived the process that held it'
    Start-App
    Check 'Single instance' 'The app starts again after a kill' `
        ([bool](Get-App)) 'the app refused to start after a kill'
    Stop-App

    Reset-State
    $a = New-Note 'one.md' "# One`n"
    $d = New-Note 'cold.md' "# Cold`n"
    Set-Index @($a)
    Start-App @($d)
    Stop-App
    $idx = Get-Index
    Check 'Single instance' 'A cold launch with a path restores the open notes AND opens that one' `
        (($null -ne $idx.notes.$a) -and ($null -ne $idx.notes.$d)) 'cold.md or one.md missing from the index'

    # =========================================================== Three states
    Write-Host ''
    Write-Host 'Three states and the index' -ForegroundColor Cyan

    Reset-State
    $a = New-Note 'a.md' "# A`n"
    $b = New-Note 'b.md' "# B`n"
    $c = New-Note 'c.md' "# C`n"
    Set-Index @($a, $b, $c)
    Start-App
    Check 'Three states' 'Three notes marked isOpen come back as three windows' `
        ([Win32Windows]::CountFor((Get-App).Id) -eq 3) `
        ('windows: ' + [Win32Windows]::CountFor((Get-App).Id))

    $stranger = New-Note 'stranger.md' "# Stranger`n"
    Start-Sleep -Seconds 2
    Check 'Three states' 'A .md merely PRESENT in the root spawns no window' `
        ([Win32Windows]::CountFor((Get-App).Id) -eq 3) `
        ('windows: ' + [Win32Windows]::CountFor((Get-App).Id))

    # Through the app's OWN shutdown code, not a kill. Killing skips it
    # entirely, so "isOpen survived the shutdown" would be an assertion about
    # the file this script wrote itself a minute earlier.
    $stampBefore = (Get-Item $IndexFile).LastWriteTimeUtc
    $null = Invoke-SessionEnding
    Stop-App

    $rewritten = (Get-Item $IndexFile).LastWriteTimeUtc -gt $stampBefore
    Check 'Three states' 'A logoff (SessionEnding) flushes and rewrites notes.json' `
        $rewritten 'notes.json was untouched by SessionEnding, so nothing below is evidence about shutdown'

    $idx = Get-Index
    Check 'Three states' 'A .md merely present gets no index entry either' `
        ($null -eq $idx.notes.$stranger) 'stranger.md was indexed'
    Check 'Three states' 'A logoff leaves isOpen set on all three' `
        ($rewritten -and @($idx.notes.PSObject.Properties | Where-Object { $_.Value.isOpen }).Count -ge 3) `
        'isOpen was cleared by the shutdown path (or the file was never rewritten to prove otherwise)'

    # ========================================================== File handling
    Write-Host ''
    Write-Host 'File safety' -ForegroundColor Cyan

    Reset-State
    $ansi = Join-Path $NotesRoot 'ansi.md'
    [System.IO.File]::WriteAllBytes($ansi, [byte[]](0x23,0x20,0x43,0x61,0x66,0xE9,0x0D,0x0A))
    Set-Index @($ansi)
    $bytesBefore = [System.IO.File]::ReadAllBytes($ansi)
    Start-App
    Stop-App
    $bytesAfter = [System.IO.File]::ReadAllBytes($ansi)
    Check 'Degradation' 'An ANSI note opens without its bytes being rewritten' `
        (-not (Compare-Object $bytesBefore $bytesAfter)) 'the file was re-encoded on open'

    Reset-State
    $locked = New-Note 'locked.md' "# Locked`n`nthis content must survive being opened under a lock.`n"
    $originalBytes = [System.IO.File]::ReadAllBytes($locked)
    Set-Index @($locked)
    $handle = [System.IO.File]::Open($locked, 'Open', 'Read', 'None')
    try {
        Start-App
        Stop-App
    } finally {
        $handle.Close()
    }
    Check 'Degradation' 'A note locked BEFORE it opens is not truncated' `
        (-not (Compare-Object $originalBytes ([System.IO.File]::ReadAllBytes($locked)))) `
        'the locked file was modified (the typing half of this item is still a human check)'

    $stray = Get-ChildItem $NotesRoot -File | Where-Object { $_.Extension -ne '.md' }
    Check 'Degradation' 'Nothing app-owned is left in the notes root' `
        ($stray.Count -eq 0) (($stray | ForEach-Object Name) -join ', ')
}
catch {
    # A throw mid-run must not swallow the checks that already ran. Record it
    # as its own failure and fall through to the verdict.
    Check 'Script' 'The scripted pass ran to completion' $false $_.Exception.Message
    Write-Host ''
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
}
finally {
    Stop-App

    # The restore is not optional and not conditional. Everything above
    # deliberately corrupts the files the real app runs on.
    #
    # It copies the CONTENTS of the backup rather than the folder, and it never
    # relies on wiping $AppData first. Wiping cannot be relied on: the WebView2
    # renderer's handles outlive the process by a moment, so Remove-Item leaves
    # the folder standing -- and Copy-Item onto a folder that still exists nests
    # the backup INSIDE it as StickyMD\StickyMD. That happened, and it puts the
    # user's whole state one level too deep where the app cannot see it, which
    # reads exactly like the run having eaten their notes.
    $saved = Join-Path $Backup 'StickyMD'

    # WebView2 is a regenerable browser cache, not state. Left alone on both
    # sides: it is the one thing here that locks, and copying it is slow.
    Get-ChildItem $AppData -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'WebView2' } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    New-Item -ItemType Directory -Path $AppData -Force | Out-Null

    if (Test-Path $saved) {
        Get-ChildItem $saved -Force |
            Where-Object { $_.Name -ne 'WebView2' } |
            ForEach-Object { Copy-Item $_.FullName -Destination $AppData -Recurse -Force }
    }

    Remove-Item $Scratch -Recurse -Force -ErrorAction SilentlyContinue

    # Asserted, not assumed. A restore that quietly did nothing is the worst
    # outcome this script can produce, so it is the last thing it checks.
    $savedFiles = @(Get-ChildItem $saved -File -ErrorAction SilentlyContinue | ForEach-Object Name)
    $backFiles = @(Get-ChildItem $AppData -File -ErrorAction SilentlyContinue | ForEach-Object Name)
    $lost = @($savedFiles | Where-Object { $backFiles -notcontains $_ })

    Check 'Script' 'Your real %LOCALAPPDATA%\StickyMD was restored intact' `
        ($lost.Count -eq 0 -and -not (Test-Path (Join-Path $AppData 'StickyMD'))) `
        ('missing: ' + ($lost -join ', ') + '. The backup is still at ' + $Backup)

    Write-Host ''
    Write-Host ('Your %LOCALAPPDATA%\StickyMD was restored from ' + $Backup) -ForegroundColor DarkGray
}

# ------------------------------------------------------------------- verdict

$failed = @($Results | Where-Object { -not $_.Pass })

Write-Host ''
Write-Host ('{0} checks, {1} failed' -f $Results.Count, $failed.Count) `
    -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'Green' })

# A green run here is not a passed gate, and saying so is part of the output.
# The items below are the ones this script deliberately cannot judge.
Write-Host ''
Write-Host 'NOT covered by this script -- still human checks:' -ForegroundColor Yellow
@(
    'Everything visual: translucency, rounded corners, no white flash, chrome-vs-content colour match, glyph hover reveal, pinned-above-fullscreen.'
    'Every bar: Changed on disk, Couldn''t save, file is gone, remote images blocked, recovered-snapshot. This script sees their effects on disk, never the bar itself.'
    'The tray icon and its menu, the global hotkeys, and the Settings window. Those need a real pointer and real keystrokes, and verify-smoke-ui.ps1 drives them. This script exercises the same SHUTDOWN CODE through WM_QUERYENDSESSION, which is a real product path, but never through the tray Exit item.'
    'Anything driven by clicks or keystrokes: editing, checkbox toggles, the entire more menu, drag-to-move and drag-to-resize.'
    'Geometry across monitors and DPI, and the close-and-reopen geometry harvest.'
    'The WebView2 renderer-crash recovery, and the runtime-missing dialog.'
) | ForEach-Object { Write-Host ('  - ' + $_) -ForegroundColor DarkGray }

if ($failed.Count) {
    Write-Host ''
    $failed | Format-Table Section, Check, Detail -AutoSize -Wrap | Out-String | Write-Host
}

exit $failed.Count
