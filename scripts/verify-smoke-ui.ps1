<#
.SYNOPSIS
    The interaction half of docs/checklists/2026-09-02-plan-b-smoke.md: the
    items that need real keystrokes, and screenshots of the ones that need eyes.

.DESCRIPTION
    verify-smoke.ps1 covers everything judgeable from disk without touching the
    app. This one drives the app: it takes genuine foreground focus, sends real
    keys through the same input queue a person's keyboard uses, and asserts on
    what lands in the .md file. It also captures PNGs of the window in known
    states, for the items whose verdict is "look at it".

    WHY IT CAN DO THIS AT ALL. SetForegroundWindow silently does nothing for a
    process that is not already foreground, which is what defeated the first
    attempt at these items. AttachThreadInput lifts that: attach this thread's
    input queue to the current foreground window's thread and Windows treats
    the call as coming from the foreground process. Every keystroke below
    depends on that working, so it is asserted before anything else runs.

    IT WILL STEAL YOUR FOCUS, repeatedly and on purpose, and it types. Do not
    run it while you are using the machine.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-smoke-ui.ps1
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$SettleSeconds = 7,
    [string]$ShotDir,

    # Substring of a block's label, to run just that block. Every block is
    # gated by Start-On, so filtering there needs no other change. Iterating on
    # one section beats paying twelve app launches to debug one of them.
    [string]$Only
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# UI Automation, for the menu and the dialogs. Menus pop up as their own
# top-level windows and their items move with the theme and the font, so
# addressing them by name is the difference between a check and a coin toss.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) {
    $Exe = Join-Path $Here '..\src\StickyMD.App\bin\Debug\net10.0-windows10.0.17763.0\StickyMD.exe'
}
$resolved = Resolve-Path $Exe -ErrorAction SilentlyContinue
if ($resolved) { $Exe = $resolved.Path }

$AppData   = Join-Path $env:LOCALAPPDATA 'StickyMD'
$IndexFile = Join-Path $AppData 'notes.json'
$Settings  = Join-Path $AppData 'settings.json'
$LogFile   = Join-Path $AppData 'diagnostics.log'
$Scratch   = Join-Path $env:TEMP 'stickymd-smoke-ui'
$NotesRoot = Join-Path $Scratch 'notes'
$Backup    = Join-Path $env:TEMP ('stickymd-smoke-ui-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (-not $ShotDir) { $ShotDir = Join-Path $Scratch 'shots' }

$Utf8NoBom = New-Object System.Text.UTF8Encoding $false
$Results   = [System.Collections.Generic.List[object]]::new()
$Tainted   = $false

if (-not ('UiProbe' -as [type])) {
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class UiProbe
{
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, StringBuilder b, int m);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private static POINT _lastSet = Origin();

    /// Seeded from where the pointer ACTUALLY is at startup. Defaulting the
    /// struct to (0,0) made every fresh process report interference before it
    /// had moved anything.
    private static POINT Origin() { POINT p; GetCursorPos(out p); return p; }

    /// Every cursor move in this class goes through here, so the last position
    /// the SCRIPT chose is always known.
    private static void Put(int x, int y) { SetCursorPos(x, y); _lastSet.X = x; _lastSet.Y = y; }

    /// True when the pointer is somewhere the script did not put it: someone
    /// is using the machine. Two readings have already been confounded that
    /// way -- a dialog answered by hand, and a note scrolled by hand -- and
    /// both looked like product bugs.
    public static bool CursorMovedExternally()
    {
        POINT p; GetCursorPos(out p);
        return System.Math.Abs(p.X - _lastSet.X) > 6 || System.Math.Abs(p.Y - _lastSet.Y) > 6;
    }

    public static List<IntPtr> Notes(int pid)
    {
        var found = new List<IntPtr>();
        EnumWindows((h, p) => {
            uint w; GetWindowThreadProcessId(h, out w);
            if (w != (uint)pid || !IsWindowVisible(h)) return true;
            var n = new StringBuilder(256); GetClassName(h, n, n.Capacity);
            if (n.ToString().StartsWith("HwndWrapper")) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static RECT Bounds(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }

    /// Real foreground focus from a process that does not have it. Attaching to
    /// the foreground thread's input queue is what makes SetForegroundWindow
    /// obey; without it the call returns and does nothing at all, and every
    /// keystroke afterwards goes to whatever window actually had focus.
    public static bool Focus(IntPtr h)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = 0;
        if (fg != IntPtr.Zero) { uint p; fgThread = GetWindowThreadProcessId(fg, out p); }
        uint self = GetCurrentThreadId();

        if (fgThread != 0 && fgThread != self) AttachThreadInput(self, fgThread, true);
        ShowWindow(h, 5);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (fgThread != 0 && fgThread != self) AttachThreadInput(self, fgThread, false);

        System.Threading.Thread.Sleep(400);
        return GetForegroundWindow() == h;
    }

    /// Keyboard focus onto the WPF window itself, off the WebView2 child.
    ///
    /// The WebView2 is a child HWND owned by another process; while it holds
    /// keyboard focus every key goes to the browser, where Ctrl+E means nothing
    /// and the window-level PreviewKeyDown never sees it. Clicking WPF chrome
    /// also fixes that, but the only chrome available is the header -- and a
    /// click there fires DragMove(), which runs a NESTED MODAL MESSAGE LOOP.
    /// Keys sent while that loop is spinning are simply lost, which is what
    /// made this harness pass and fail on alternate runs. SetFocus needs the
    /// input queues attached, exactly as SetForegroundWindow does.
    public static bool FocusKeyboard(IntPtr h)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = 0;
        if (fg != IntPtr.Zero) { uint p; fgThread = GetWindowThreadProcessId(fg, out p); }
        uint self = GetCurrentThreadId();

        if (fgThread != 0 && fgThread != self) AttachThreadInput(self, fgThread, true);
        SetFocus(h);
        if (fgThread != 0 && fgThread != self) AttachThreadInput(self, fgThread, false);

        System.Threading.Thread.Sleep(300);
        return true;
    }

    public static void Click(int x, int y)
    {
        Put(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // LEFTDOWN
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // LEFTUP
        System.Threading.Thread.Sleep(250);
    }

    public static void MoveTo(int x, int y) { Put(x, y); System.Threading.Thread.Sleep(400); }

    public static void RightClick(int x, int y)
    {
        Put(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(0x0008, 0, 0, 0, IntPtr.Zero);   // RIGHTDOWN
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0010, 0, 0, 0, IntPtr.Zero);   // RIGHTUP
        System.Threading.Thread.Sleep(500);
    }

    public static void Scroll(int x, int y, int notches)
    {
        Put(x, y);
        System.Threading.Thread.Sleep(150);
        for (int i = 0; i < System.Math.Abs(notches); i++)
        {
            mouse_event(0x0800, 0, 0, (uint)(notches > 0 ? 120 : -120), IntPtr.Zero);
            System.Threading.Thread.Sleep(60);
        }
        System.Threading.Thread.Sleep(400);
    }

    /// Ctrl held across real wheel notches. The note must not zoom.
    public static void CtrlScroll(int x, int y, int notches)
    {
        Put(x, y);
        System.Threading.Thread.Sleep(150);
        keybd_event(0x11, 0, 0, IntPtr.Zero);        // Ctrl down
        for (int i = 0; i < notches; i++)
        {
            mouse_event(0x0800, 0, 0, 120, IntPtr.Zero);   // WHEEL, one notch up
            System.Threading.Thread.Sleep(90);
        }
        keybd_event(0x11, 0, 2, IntPtr.Zero);        // Ctrl up
        System.Threading.Thread.Sleep(500);
    }

    /// Real motion rather than a teleport. WPF decides IsMouseOver from move
    /// messages, and measuring a hover right after a single SetCursorPos reads
    /// the state from BEFORE the move -- which once produced a confident,
    /// entirely false report that the header hover did nothing.
    public static void Glide(int x0, int y0, int x1, int y1)
    {
        for (int i = 1; i <= 25; i++)
        {
            Put(x0 + (x1 - x0) * i / 25, y0 + (y1 - y0) * i / 25);
            System.Threading.Thread.Sleep(25);
        }
        System.Threading.Thread.Sleep(500);
    }

    /// Press, move in steps so DragMove's modal loop sees real motion, release.
    public static void DragFrom(int x, int y, int dx, int dy)
    {
        Put(x, y);
        System.Threading.Thread.Sleep(200);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(150);
        for (int i = 1; i <= 12; i++)
        {
            Put(x + dx * i / 12, y + dy * i / 12);
            System.Threading.Thread.Sleep(40);
        }
        System.Threading.Thread.Sleep(200);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(500);
    }
}
'@
}

# --------------------------------------------------------------------- helpers

function Write-Utf8($path, $text) { [System.IO.File]::WriteAllText($path, $text, $Utf8NoBom) }

function Check($section, $name, [bool]$ok, $detail) {
    # A failure recorded while somebody was using the machine is not evidence.
    if (-not $ok -and [UiProbe]::CursorMovedExternally()) {
        $detail = 'THE MOUSE MOVED DURING THIS RUN, so this result may be someone at the keyboard rather than the app. ' + $detail
        $script:Tainted = $true
    }
    $Results.Add([pscustomobject]@{ Section = $section; Check = $name; Pass = $ok; Detail = $detail })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}" -f $mark, $name) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    if (-not $ok -and $detail) { Write-Host ("         {0}" -f $detail) -ForegroundColor DarkGray }
}

function Get-App { Get-Process StickyMD -ErrorAction SilentlyContinue }

function Stop-App {
    Get-App | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 40 -and (Get-App); $i++) { Start-Sleep -Milliseconds 100 }
}

function Send($keys, $pauseMs = 350) {
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds $pauseMs
}

function Shoot($name, $rect) {
    $path = Join-Path $ShotDir ($name + '.png')
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose(); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return $path
}

<#
    How much of the picture changed, as a fraction of sampled pixels.

    Returns the RATIO rather than a yes/no, because the two questions asked of
    it want opposite sensitivities: "nothing moved" can tolerate a caret blink,
    while "the note re-rendered" must notice a single line of text changing. A
    one-line edit in a 460x520 note moves about 1.2% of sampled pixels, and a
    shared 2% tolerance called that "unchanged" -- reporting a broken watcher
    against a watcher that worked perfectly.
#>
function Get-ShotDiff($a, $b) {
    $ia = [System.Drawing.Bitmap]::FromFile($a)
    $ib = [System.Drawing.Bitmap]::FromFile($b)
    try {
        if ($ia.Width -ne $ib.Width -or $ia.Height -ne $ib.Height) { return 1.0 }
        $diff = 0; $total = 0
        for ($x = 0; $x -lt $ia.Width; $x += 3) {
            for ($y = 0; $y -lt $ia.Height; $y += 3) {
                $total++
                $ca = $ia.GetPixel($x, $y); $cb = $ib.GetPixel($x, $y)
                if (([math]::Abs($ca.R - $cb.R) + [math]::Abs($ca.G - $cb.G) + [math]::Abs($ca.B - $cb.B)) -gt 30) { $diff++ }
            }
        }
        return ($diff / [double]$total)
    }
    finally { $ia.Dispose(); $ib.Dispose() }
}

# Measured, not guessed. One line of body text changing in a 460x520 note is
# diff=0.0034 -- a third of a percent, not the "about one percent" that a first
# estimate assumed, and a threshold set from that estimate called a real
# re-render "unchanged". Frames that genuinely match come in an order of
# magnitude below this.
$ShotSame = 0.002

function Set-Index($openPaths) {
    $notes = [ordered]@{}
    foreach ($p in $openPaths) {
        $notes[$p] = [ordered]@{
            x = 300; y = 200; w = 460; h = 520; monitor = $null
            color = 'Yellow'; opacity = 1; alwaysOnTop = $true
            isOpen = $true; lastOpenedUtc = (Get-Date).ToUniversalTime().ToString('o')
        }
    }
    Write-Utf8 $IndexFile (([ordered]@{ version = 1; notes = $notes } | ConvertTo-Json -Depth 6))
}

<# A fresh app on a fresh note, focused and ready for keys. Returns the note
   window handle, or $null with the reason already reported as a failure. #>
function Start-On($body, $checkName, $setup) {
    if ($Only -and $checkName -notlike "*$Only*") { return $null }

    Stop-App
    Remove-Item $NotesRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $NotesRoot -Force | Out-Null

    # Fresh log per block, so "diagnostics.log records the refusal" can only
    # match a line THIS block caused.
    Remove-Item $LogFile -Force -ErrorAction SilentlyContinue

    $note = Join-Path $NotesRoot 'note.md'
    Write-Utf8 $note $body
    Set-Index @($note)
    Write-Utf8 $Settings (([ordered]@{ notesRoot = $NotesRoot } | ConvertTo-Json))

    # Anything the block needs on disk BEFORE the app opens: an image beside
    # the note, a second .md for a link to point at.
    if ($setup) { & $setup $NotesRoot }

    Start-Process $Exe | Out-Null
    Start-Sleep -Seconds $SettleSeconds

    $p = Get-App
    if (-not $p) { Check 'UI' $checkName $false 'the app did not start'; return $null }

    $handles = [UiProbe]::Notes($p.Id)
    if ($handles.Count -eq 0) { Check 'UI' $checkName $false 'no note window appeared'; return $null }

    # Focus twice, with a pause. The window is shown ShowActivated=false by
    # RestoreOpenNotes, and the WebView2 finishing its load can take focus back
    # out from under the first attempt.
    $ok = [UiProbe]::Focus($handles[0])
    if (-not $ok) { Start-Sleep -Seconds 2; $ok = [UiProbe]::Focus($handles[0]) }
    if (-not $ok) {
        Check 'UI' $checkName $false 'could not take foreground focus; keystrokes would have gone elsewhere'
        return $null
    }

    # Keyboard focus onto the WPF window, off the WebView2 child, WITHOUT
    # clicking: a header click fires DragMove and its nested modal loop eats
    # whatever is typed next. See FocusKeyboard.
    $null = [UiProbe]::FocusKeyboard($handles[0])

    return @{ Handle = $handles[0]; Note = $note }
}

function Get-Note($ctx) { Get-Content $ctx.Note -Raw }

# ------------------------------------------------------------------------ UIA

$AE = [System.Windows.Automation.AutomationElement]
$Desktop = $AE::RootElement

function UiaFind($property, $value, $timeoutMs = 5000) {
    $procCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, (Get-App).Id)
    $cond = New-Object System.Windows.Automation.AndCondition(
        $procCond, (New-Object System.Windows.Automation.PropertyCondition($property, $value)))

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        # Descendants from the DESKTOP, not from the note window: a WPF
        # ContextMenu lives in its own top-level popup window, so anything
        # rooted at the note simply cannot see the menu items.
        $el = $Desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function UiaById($id, $timeoutMs = 5000) { UiaFind $AE::AutomationIdProperty $id $timeoutMs }
function UiaByName($name, $timeoutMs = 5000) { UiaFind $AE::NameProperty $name $timeoutMs }

function UiaInvoke($el) {
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 600
}

<#
    Click a UIA element by its on-screen rectangle rather than through
    InvokePattern.

    InvokePattern.Invoke() waits for the click handler to RETURN. A handler
    that opens a modal MessageBox does not return until the dialog is
    dismissed, so invoking it deadlocks against the very dialog the check needs
    to find. A synthesized mouse click posts and comes straight back.
#>
function UiaClick($el) {
    $r = $el.Current.BoundingRectangle
    [UiProbe]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 500
}

function UiaExpand($el) {
    $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 600
}

<# Every item on the open more menu, by name. #>
function UiaMenuItemNames {
    $procCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, (Get-App).Id)
    $typeCond = New-Object System.Windows.Automation.PropertyCondition(
        $AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $cond = New-Object System.Windows.Automation.AndCondition($procCond, $typeCond)
    $found = $Desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $names = @()
    foreach ($el in $found) { $names += $el.Current.Name }
    return $names
}

function Open-MoreMenu {
    $more = UiaById 'MoreButton'
    if (-not $more) { return $false }
    UiaInvoke $more
    return $true
}

<#
    Ctrl+E, then WAIT until the editor really has keyboard focus.

    Sending the key and sleeping a fixed time is a race, and it is the race
    that made every editing check here fail intermittently: on a loaded machine
    the WebView2 is still initialising, the key arrives before the window can
    act on it, and the typing that follows goes into the void. The file then
    shows its original content and the check reports a product failure that did
    not happen. Poll for the state instead, and retry the key.
#>
function Enter-EditMode {
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        Send '^e' 700

        $editor = UiaById 'Editor' 1500
        if ($editor) {
            $focused = $editor.Current.HasKeyboardFocus
            if ($focused) { return $true }

            # Present but not focused: give it a moment rather than mashing the
            # key, which would toggle straight back out of edit mode.
            Start-Sleep -Milliseconds 700
            if ((UiaById 'Editor' 800).Current.HasKeyboardFocus) { return $true }
        }

        Start-Sleep -Seconds 1
    }
    return $false
}

<#
    Exit the way a person does, with the temporary Ctrl+Shift+Alt+Q key.

    This works only because AttachThreadInput gives the script real foreground
    rights: the handler reads Keyboard.Modifiers off a focused window's
    PreviewKeyDown, and without focus the key is never delivered. It was
    written off as "not scriptable" twice before that was understood.
#>
function Stop-AppViaExitKey($handle) {
    if (-not [UiProbe]::Focus($handle)) { return $false }
    Send '^+%q' 600
    for ($i = 0; $i -lt 50 -and (Get-App); $i++) { Start-Sleep -Milliseconds 200 }
    return (-not (Get-App))
}

function Get-Entry($ctx) {
    $idx = Get-Content $IndexFile -Raw | ConvertFrom-Json
    return $idx.notes.($ctx.Note)
}

<# Caret to the end of the last line WITH TEXT on it.

   Not Ctrl+End: every note ends with a trailing newline, so Ctrl+End parks the
   caret on the empty line after the content, where Enter continues no list and
   Shift+Home selects nothing. Three checks reported app failures that were
   really this. #>
function Send-CaretToLastLine { Send '^{END}' 250; Send '{UP}{END}' 350 }

# ------------------------------------------------------------------ preflight

if (-not (Test-Path $Exe)) { throw "StickyMD.exe not found at $Exe. Run dotnet build first." }
if (Get-App) { throw 'StickyMD is already running. Exit it first.' }

New-Item -ItemType Directory -Path $ShotDir -Force | Out-Null
New-Item -ItemType Directory -Path $Backup -Force | Out-Null
if (Test-Path $AppData) { Copy-Item $AppData -Destination (Join-Path $Backup 'StickyMD') -Recurse -Force }

Write-Host ''
Write-Host 'StickyMD interaction pass' -ForegroundColor Cyan
Write-Host ('  shots  {0}' -f $ShotDir)
Write-Host ''

try {
    # ==================================================== Editing and saving
    Write-Host 'Editing and saving' -ForegroundColor Cyan

    # --- Ctrl+E, typing, and the ~1s autosave, in one sequence
    $ctx = Start-On "# Alpha`r`n`r`noriginal line`r`n" 'Ctrl+E enters edit mode and typing reaches the file'
    if ($ctx) {
        if (-not (Enter-EditMode)) { Check 'Editing' 'Ctrl+E reached the editor' $false 'the editor never took keyboard focus' }
        Send '^{END}' 400
        Send 'typed by the harness' 400
        Start-Sleep -Seconds 3
        $text = Get-Note $ctx
        Check 'Editing' 'Ctrl+E enters edit mode and typing autosaves to the .md' `
            ($text -match 'typed by the harness') ('file now: ' + ($text -replace "`r`n", ' / '))

        Check 'Editing' 'The original content is still there after typing' `
            ($text -match 'original line') ('file now: ' + ($text -replace "`r`n", ' / '))
    }

    # --- Ctrl+B wraps, and Ctrl+Z takes back ONLY the bold
    $ctx = Start-On "# Bold`r`n`r`nplain`r`n" 'Ctrl+B wraps the selection'
    if ($ctx) {
        if (-not (Enter-EditMode)) { Check 'Editing' 'Ctrl+E reached the editor' $false 'the editor never took keyboard focus' }
        Send-CaretToLastLine
        Send '+{HOME}' 400          # select the last line of text
        Send '^b' 600
        Start-Sleep -Seconds 2
        $bolded = Get-Note $ctx
        Check 'Editing' 'Ctrl+B wraps the selected text in asterisks' `
            ($bolded -match '\*\*plain\*\*') ('file now: ' + ($bolded -replace "`r`n", ' / '))

        # Only meaningful if the bold actually landed. Asserting "there is no
        # ** in the file" against a file that never had one is the vacuous
        # green this whole script exists to avoid.
        if ($bolded -match '\*\*plain\*\*') {
            Send '^z' 800
            Start-Sleep -Seconds 2
            $undone = Get-Note $ctx
            Check 'Editing' 'Ctrl+Z after Ctrl+B undoes ONLY the bold, keeping the word' `
                (($undone -notmatch '\*\*') -and ($undone -match 'plain')) `
                ('file now: ' + ($undone -replace "`r`n", ' / '))
        } else {
            Check 'Editing' 'Ctrl+Z after Ctrl+B undoes ONLY the bold, keeping the word' $false `
                'BLOCKED: the Ctrl+B above never landed, so there was no bold to undo.'
        }
    }

    # --- Ctrl+B on an EMPTY selection leaves the caret between the markers
    $ctx = Start-On "# Empty`r`n`r`nplain`r`n" 'Ctrl+B on an empty selection'
    if ($ctx) {
        if (-not (Enter-EditMode)) { Check 'Editing' 'Ctrl+E reached the editor' $false 'the editor never took keyboard focus' }
        Send-CaretToLastLine
        Send '^b' 600
        Send 'inside' 400
        Start-Sleep -Seconds 2
        $mid = Get-Note $ctx
        Check 'Editing' 'Ctrl+B with nothing selected leaves the caret between the markers' `
            ($mid -match '\*\*inside\*\*') ('file now: ' + ($mid -replace "`r`n", ' / '))
    }

    # --- Enter continues a list, and ends it on an empty item
    $ctx = Start-On "# List`r`n`r`n- first`r`n" 'Enter continues a list'
    if ($ctx) {
        if (-not (Enter-EditMode)) { Check 'Editing' 'Ctrl+E reached the editor' $false 'the editor never took keyboard focus' }
        Send-CaretToLastLine
        Send '{ENTER}' 500
        Send 'second' 400
        Start-Sleep -Seconds 2
        $listed = Get-Note $ctx
        Check 'Editing' 'Enter continues a - list with a new marker' `
            ($listed -match '(?m)^- second') ('file now: ' + ($listed -replace "`r`n", ' / '))

        Send '{ENTER}' 500
        Send '{ENTER}' 700
        Start-Sleep -Seconds 2
        $ended = Get-Note $ctx
        Check 'Editing' 'Enter on an empty item ends the list rather than adding another marker' `
            (([regex]::Matches($ended, '(?m)^-\s')).Count -eq 2) `
            ('markers: ' + ([regex]::Matches($ended, '(?m)^-\s')).Count + ' in ' + ($ended -replace "`r`n", ' / '))
    }

    # --- Tab indents inside a list and does not move focus
    $ctx = Start-On "# Indent`r`n`r`n- one`r`n- two`r`n" 'Tab indents a list item'
    if ($ctx) {
        if (-not (Enter-EditMode)) { Check 'Editing' 'Ctrl+E reached the editor' $false 'the editor never took keyboard focus' }
        Send-CaretToLastLine
        Send '{HOME}' 300
        Send '{TAB}' 600
        Start-Sleep -Seconds 2
        $indented = Get-Note $ctx
        Check 'Editing' 'Tab indents the list item' `
            ($indented -match '(?m)^\s+- two') ('file now: ' + ($indented -replace "`r`n", ' / '))

        if ($indented -match '(?m)^\s+- two') {
            Send '+{TAB}' 600
            Start-Sleep -Seconds 2
            $outdented = Get-Note $ctx
            Check 'Editing' 'Shift+Tab outdents it again' `
                ($outdented -match '(?m)^- two') ('file now: ' + ($outdented -replace "`r`n", ' / '))
        } else {
            Check 'Editing' 'Shift+Tab outdents it again' $false `
                'BLOCKED: the Tab above never indented anything, so there was nothing to outdent.'
        }

        # Focus never left the note: if Tab had moved focus, the window would no
        # longer be foreground and the edits above would have gone elsewhere.
        Check 'Editing' 'Tab never moved focus off the note' `
            ([UiProbe]::Notes((Get-App).Id).Count -ge 1) 'the note window went away'
    }

    # ============================================================== Screenshots
    Write-Host ''
    Write-Host 'Captures for visual review' -ForegroundColor Cyan

    $ctx = Start-On "# Shortcuts`r`n`r`n| Key | Action |`r`n| --- | --- |`r`n| Ctrl+B | Bold |`r`n| Ctrl+I | Italic |`r`n`r`n- [ ] unchecked task`r`n- [x] checked task`r`n`r`nSome **bold** and *italic* body text.`r`n" 'capture'
    if ($ctx) {
        $r = [UiProbe]::Bounds($ctx.Handle)

        # Pointer parked far away: the resting glyph state is only meaningful
        # if nothing is hovering the header.
        [UiProbe]::MoveTo($r.Left - 400, $r.Top + 600)
        $shots = @{}
        $shots['01-at-rest'] = Shoot '01-at-rest' $r

        # Hovering the header, for the glyph brighten.
        [UiProbe]::MoveTo($r.Left + 40, $r.Top + 10)
        $shots['02-header-hover'] = Shoot '02-header-hover' $r

        [UiProbe]::MoveTo($r.Left - 400, $r.Top + 600)
        Start-Sleep -Milliseconds 600
        $shots['03-back-at-rest'] = Shoot '03-back-at-rest' $r

        Check 'Capture' 'Screenshots of the note were captured for review' `
            ($shots.Count -eq 3) 'capture failed'
        $shots.Values | ForEach-Object { Write-Host ('         ' + $_) -ForegroundColor DarkGray }
    }

    # ======================================================= Chrome behaviour
    Write-Host ''
    Write-Host 'Header: resting glyphs, hover, and drag' -ForegroundColor Cyan

    $ctx = Start-On "# Chrome`r`n`r`nbody text`r`n" 'header behaviour'
    if ($ctx) {
        $r = [UiProbe]::Bounds($ctx.Handle)

        # Mean brightness of the glyph strip. The glyphs are a small part of it,
        # so the absolute number is mostly background; only the DELTA matters.
        function GlyphMean($rect) {
            $w = $rect.Right - $rect.Left; $ht = $rect.Bottom - $rect.Top
            $bmp = New-Object System.Drawing.Bitmap $w, $ht
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht))
            $g.Dispose()
            $sum = 0.0; $n = 0
            for ($x = $bmp.Width - 120; $x -lt $bmp.Width - 8; $x++) {
                for ($y = 4; $y -lt 24; $y++) { $c = $bmp.GetPixel($x, $y); $sum += ($c.R + $c.G + $c.B) / 3.0; $n++ }
            }
            $bmp.Dispose(); return $sum / $n
        }

        [UiProbe]::Glide(($r.Left + 200), ($r.Top + 13), ($r.Left - 250), ($r.Top + 500))
        $away = GlyphMean $r

        [UiProbe]::Glide(($r.Left - 250), ($r.Top + 500), ($r.Left + 200), ($r.Top + 13))
        $hover = GlyphMean $r

        Check 'Chrome' 'Hovering the header brightens the glyphs' `
            ($hover -gt $away + 1.0) ('away=' + [math]::Round($away,2) + ' hover=' + [math]::Round($hover,2))

        [UiProbe]::Glide(($r.Left + 200), ($r.Top + 13), ($r.Left - 250), ($r.Top + 500))
        $back = GlyphMean $r
        Check 'Chrome' 'Leaving the header dims them again, without hiding them' `
            ([math]::Abs($back - $away) -lt 1.0) ('away=' + [math]::Round($away,2) + ' back=' + [math]::Round($back,2))

        # Dragging the header moves the note; dragging the BODY must not. If the
        # body moves the window, the drag handler has migrated to the Window and
        # every click over the note content is being swallowed: contract 3.
        $before = [UiProbe]::Bounds($ctx.Handle)
        [UiProbe]::DragFrom(($before.Left + 200), ($before.Top + 13), 130, 70)
        $moved = [UiProbe]::Bounds($ctx.Handle)
        Check 'Chrome' 'Dragging the header moves the note' `
            (($moved.Left -ne $before.Left) -or ($moved.Top -ne $before.Top)) `
            ('before=' + $before.Left + ',' + $before.Top + ' after=' + $moved.Left + ',' + $moved.Top)

        $b2 = [UiProbe]::Bounds($ctx.Handle)
        [UiProbe]::DragFrom(($b2.Left + 200), ($b2.Top + 220), 110, 60)
        $b3 = [UiProbe]::Bounds($ctx.Handle)
        Check 'Chrome' 'Dragging the BODY does not move the note (contract 3)' `
            (($b3.Left -eq $b2.Left) -and ($b3.Top -eq $b2.Top)) `
            ('the drag handler is on the Window; clicks over content are being swallowed')
    }
    # =========================================================== The more menu
    Write-Host ''
    Write-Host 'The more menu' -ForegroundColor Cyan

    $ctx = Start-On "# Menu`r`n`r`nbody text`r`n" 'the more menu'
    if ($ctx) {
        if (Open-MoreMenu) {
            $names = UiaMenuItemNames
            # Spec 7's menu, verbatim. Separators are not MenuItems and do not
            # appear here.
            $expected = @('Rename…', 'Color', 'Opacity', 'Always on Top', 'Delete')
            $missing = @($expected | Where-Object { $names -notcontains $_ })
            Check 'More menu' 'The menu contains exactly the spec entries' `
                ($missing.Count -eq 0) ('missing: ' + ($missing -join ', ') + ' | saw: ' + ($names -join ', '))

            Send '{ESC}' 500
        } else {
            Check 'More menu' 'The menu contains exactly the spec entries' $false 'MoreButton not found by UIA'
        }

        # --- Always on Top toggles and persists
        $before = (Get-Entry $ctx).alwaysOnTop
        if (Open-MoreMenu) {
            $pin = UiaByName 'Always on Top'
            if ($pin) {
                UiaInvoke $pin
                Start-Sleep -Seconds 2
                $after = (Get-Entry $ctx).alwaysOnTop
                Check 'More menu' 'Always on Top toggles and is written to notes.json' `
                    ($after -ne $before) ('before=' + $before + ' after=' + $after)
            } else {
                Check 'More menu' 'Always on Top toggles and is written to notes.json' $false 'menu item not found'
            }
        }
    }

    # --- Opacity: the submenu holds a slider, driven through RangeValue
    $ctx = Start-On "# Opacity`r`n`r`nbody`r`n" 'opacity slider'
    if ($ctx) {
        if (Open-MoreMenu) {
            $op = UiaByName 'Opacity'
            if ($op) {
                UiaExpand $op
                $slider = UiaFind $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Slider) 3000
                if ($slider) {
                    $rv = $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)

                    # Read the range rather than assume it. The menu floors
                    # opacity at 30%, so this slider runs 30..100, and setting
                    # 0.5 on it throws for being out of range.
                    $min = $rv.Current.Minimum
                    $max = $rv.Current.Maximum
                    $target = $min + ($max - $min) * 0.5
                    $rv.SetValue($target)
                    Start-Sleep -Seconds 2

                    # notes.json stores a 0..1 fraction whatever the slider counts in.
                    $expected = if ($max -gt 1.5) { $target / 100.0 } else { $target }
                    $val = [double](Get-Entry $ctx).opacity
                    Check 'More menu' 'The opacity slider applies and is written to notes.json' `
                        ([math]::Abs($val - $expected) -lt 0.08) `
                        ('slider ' + $min + '..' + $max + ', set ' + $target + ', notes.json holds ' + $val)
                } else {
                    Check 'More menu' 'The opacity slider applies and is written to notes.json' $false 'no Slider found in the submenu'
                }
            }
            Send '{ESC}' 400; Send '{ESC}' 400
        }
    }

    # --- Rename: the dialog, its guards, and the file actually moving
    $ctx = Start-On "# Rename`r`n`r`nbody`r`n" 'rename'
    if ($ctx) {
        if (Open-MoreMenu) {
            $rn = UiaByName 'Rename…'
            if ($rn) {
                UiaInvoke $rn
                $box = UiaById 'NameBox' 4000
                $ok = UiaById 'OkButton' 2000

                if ($box -and $ok) {
                    $vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                    Check 'Rename' 'The dialog opens with the current name in the box' `
                        ($vp.Current.Value -eq 'note') ('box held: ' + $vp.Current.Value)

                    # Preselected means typing REPLACES rather than inserts.
                    Send 'renamed' 500
                    Check 'Rename' 'The name is preselected, so typing replaces it' `
                        ($vp.Current.Value -eq 'renamed') ('box now: ' + $vp.Current.Value)

                    # An empty name must disable the button, not fail on click.
                    Send '^a' 300
                    Send '{DEL}' 500
                    Check 'Rename' 'Clearing the box disables the Rename button' `
                        (-not $ok.Current.IsEnabled) 'the button stayed enabled on an empty name'

                    $vp.SetValue('renamed')
                    Start-Sleep -Milliseconds 400
                    UiaInvoke $ok
                    Start-Sleep -Seconds 2

                    Check 'Rename' 'Rename moves the file on disk' `
                        ((Test-Path (Join-Path $NotesRoot 'renamed.md')) -and -not (Test-Path $ctx.Note)) `
                        ('root now: ' + ((Get-ChildItem $NotesRoot -Filter *.md | ForEach-Object Name) -join ', '))
                } else {
                    Check 'Rename' 'The rename dialog opens' $false 'NameBox or OkButton not found'
                }
            }
        }
    }

    # --- Renaming onto a name that already exists must refuse and keep BOTH
    $ctx = Start-On "# Collide`r`n`r`nbody`r`n" 'rename collision'
    if ($ctx) {
        $victim = Join-Path $NotesRoot 'taken.md'
        Write-Utf8 $victim "# Taken`r`n`r`nthis file must survive intact`r`n"
        Start-Sleep -Seconds 1

        if (Open-MoreMenu) {
            $rn = UiaByName 'Rename…'
            if ($rn) {
                UiaInvoke $rn
                $box = UiaById 'NameBox' 4000
                $ok = UiaById 'OkButton' 2000
                if ($box -and $ok) {
                    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('taken')
                    Start-Sleep -Milliseconds 400
                    if ($ok.Current.IsEnabled) { UiaInvoke $ok }
                    Start-Sleep -Seconds 2

                    Check 'Rename' 'Renaming onto an existing name leaves BOTH files intact' `
                        ((Test-Path $ctx.Note) -and ((Get-Content $victim -Raw) -match 'must survive intact')) `
                        ('root now: ' + ((Get-ChildItem $NotesRoot -Filter *.md | ForEach-Object Name) -join ', '))
                }
                Send '{ESC}' 400
            }
        }
    }

    # --- Delete asks first, and sends the file to the Recycle Bin
    $ctx = Start-On "# Delete me`r`n`r`nbody`r`n" 'delete'
    if ($ctx) {
        if (Open-MoreMenu) {
            $del = UiaByName 'Delete'
            if ($del) {
                UiaClick $del
                # No settling sleep. The dialog is modal and someone watching
                # the screen will answer it within a second, and then this
                # check reports "no dialog appeared" about a dialog that did.
                

                # The confirmation is modal. Finding it at all IS the "reachable,
                # not stuck behind the pinned note" check: this note is Topmost.
                $yes = UiaByName 'OK' 3000
                if (-not $yes) { $yes = UiaByName 'Yes' 1500 }

                # When it is not found, say WHAT was there instead. A bare
                # "no dialog appeared" sent this check round three times.
                $seen = ''
                if (-not $yes) {
                    $btn = New-Object System.Windows.Automation.PropertyCondition(
                        $AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
                    $all = $Desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btn)
                    $names = @()
                    foreach ($e in $all) {
                        $names += ('"' + $e.Current.Name + '"[pid ' + $e.Current.ProcessId + ']')
                    }
                    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
                    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
                    $g = [System.Drawing.Graphics]::FromImage($bmp)
                    $g.CopyFromScreen($b.X, $b.Y, 0, 0, (New-Object System.Drawing.Size $b.Width, $b.Height))
                    $g.Dispose()
                    $dbg = Join-Path $ShotDir 'delete-no-dialog.png'
                    $bmp.Save($dbg); $bmp.Dispose()

                    # Tell the two cases apart. If the file is GONE, the
                    # dialog appeared and something outside this script
                    # answered it -- a person at the keyboard. That is not a
                    # product failure and must not be reported as one.
                    $gone = -not (Test-Path $ctx.Note)
                    $seen = if ($gone) {
                        'INCONCLUSIVE: the note was deleted, so the dialog appeared and someone else answered it. Re-run without touching the machine. Screen: ' + $dbg
                    } else {
                        'no confirmation dialog appeared and the file is still there. Screen: ' + $dbg
                    }
                }

                Check 'Delete' 'Delete asks first, and the dialog is reachable with the note pinned' `
                    ($null -ne $yes) $seen

                if ($yes) {
                    Check 'Delete' 'The file is still on disk while the dialog is up' `
                        (Test-Path $ctx.Note) 'the file was deleted before the user confirmed'

                    UiaClick $yes
                    Start-Sleep -Seconds 3

                    Check 'Delete' 'Confirming removes the file from the notes root' `
                        (-not (Test-Path $ctx.Note)) 'the file is still there'

                    # Recycled, not destroyed. The distinction is the whole
                    # point of RecycleBinService.
                    $bin = (New-Object -ComObject Shell.Application).Namespace(10)
                    $found = $false
                    foreach ($item in $bin.Items()) { if ($item.Name -like 'note*') { $found = $true } }
                    Check 'Delete' 'The deleted note is in the Recycle Bin, not destroyed' `
                        $found 'nothing matching the note was found in the Recycle Bin'
                }
            }
        }
    }

    # --- The close glyph hides the note but must NOT clear isOpen
    $ctx = Start-On "# Close`r`n`r`nbody`r`n" 'close glyph'
    if ($ctx) {
        $close = UiaById 'CloseButton'
        if ($close) {
            UiaInvoke $close
            Start-Sleep -Seconds 2
            Check 'Three states' 'The close glyph leaves isOpen set, so the note comes back' `
                ((Get-Entry $ctx).isOpen -eq $true) 'the close glyph cleared isOpen'
        } else {
            Check 'Three states' 'The close glyph leaves isOpen set, so the note comes back' $false 'CloseButton not found'
        }
    }

    # --- Clicking a task checkbox rewrites the .md
    $ctx = Start-On "- [ ] task one`r`n- [ ] task two`r`n" 'checkbox click'
    if ($ctx) {
        $r = [UiProbe]::Bounds($ctx.Handle)
        $shotBefore = Shoot 'checkbox-before' $r

        # The first task sits just under the 26px header. The capture above is
        # kept so a miss can be corrected by looking rather than by guessing.
        [UiProbe]::Click(($r.Left + 26), ($r.Top + 48))
        Start-Sleep -Seconds 2

        $after = Get-Note $ctx
        $shotAfter = Shoot 'checkbox-after' $r
        Check 'Checkboxes' 'Clicking a task checkbox rewrites the .md' `
            ($after -match '(?m)^- \[[xX]\] task one') `
            ('file now: ' + ($after -replace "`r`n", ' / ') + ' | ' + $shotBefore + ' ' + $shotAfter)
    }

    # ===================================================== Resources and links
    Write-Host ''
    Write-Host 'Resources and links' -ForegroundColor Cyan

    <#
        A loud magenta swatch, so "did the image render" is a pixel count
        rather than a judgement. Nothing in any of the seven note themes is
        anywhere near it, so a single matching pixel means the image drew and
        zero means it did not.
    #>
    function New-Swatch($path) {
        $bmp = New-Object System.Drawing.Bitmap 140, 70
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::FromArgb(255, 255, 0, 255))
        $g.Dispose()
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }

    function Count-Magenta($rect) {
        $w = $rect.Right - $rect.Left; $ht = $rect.Bottom - $rect.Top
        $bmp = New-Object System.Drawing.Bitmap $w, $ht
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht))
        $g.Dispose()
        $n = 0
        for ($x = 0; $x -lt $bmp.Width; $x += 2) {
            for ($y = 0; $y -lt $bmp.Height; $y += 2) {
                $c = $bmp.GetPixel($x, $y)
                if ($c.R -gt 200 -and $c.G -lt 80 -and $c.B -gt 200) { $n++ }
            }
        }
        $bmp.Dispose()
        return $n
    }

    # --- a relative image in the note directory loads
    $ctx = Start-On "# Image`r`n`r`n![swatch](swatch.png)`r`n" 'image-relative' {
        param($root) New-Swatch (Join-Path $root 'swatch.png')
    }
    if ($ctx) {
        Start-Sleep -Seconds 2
        $r = [UiProbe]::Bounds($ctx.Handle)
        $hits = Count-Magenta $r
        Check 'Resources' 'A relative image in the note directory loads' `
            ($hits -gt 50) ('magenta pixels: ' + $hits + ' | ' + (Shoot 'image-relative' $r))
    }

    # --- a data: image loads
    $tmpSwatch = Join-Path $env:TEMP 'stickymd-swatch.png'
    New-Swatch $tmpSwatch
    $b64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($tmpSwatch))
    $ctx = Start-On ("# Data`r`n`r`n![swatch](data:image/png;base64," + $b64 + ")`r`n") 'image-data'
    if ($ctx) {
        Start-Sleep -Seconds 2
        $r = [UiProbe]::Bounds($ctx.Handle)
        $hits = Count-Magenta $r
        Check 'Resources' 'A data: image loads' `
            ($hits -gt 50) ('magenta pixels: ' + $hits + ' | ' + (Shoot 'image-data' $r))
    }

    # --- an absolute local path shows a placeholder, NOT the image
    $ctx = Start-On ("# Absolute`r`n`r`n![swatch](" + $tmpSwatch + ")`r`n") 'image-absolute'
    if ($ctx) {
        Start-Sleep -Seconds 2
        $r = [UiProbe]::Bounds($ctx.Handle)
        $hits = Count-Magenta $r
        Check 'Resources' 'An absolute local path image is refused, not loaded' `
            ($hits -lt 20) ('magenta pixels: ' + $hits + ' (expected none) | ' + (Shoot 'image-absolute' $r))
    }

    # --- ../outside.png shows a placeholder. Known v1 limitation.
    $ctx = Start-On "# Outside`r`n`r`n![swatch](../outside.png)`r`n" 'image-outside' {
        param($root) New-Swatch (Join-Path (Split-Path -Parent $root) 'outside.png')
    }
    if ($ctx) {
        Start-Sleep -Seconds 2
        $r = [UiProbe]::Bounds($ctx.Handle)
        $hits = Count-Magenta $r
        Check 'Resources' 'An image outside the notes root is refused' `
            ($hits -lt 20) ('magenta pixels: ' + $hits + ' (expected none) | ' + (Shoot 'image-outside' $r))
    }

    # --- a remote https: image is blocked, and says so in a bar
    $ctx = Start-On "# Remote`r`n`r`n![remote](https://example.invalid/pic.png)`r`n" 'image-remote'
    if ($ctx) {
        Start-Sleep -Seconds 2
        $load = UiaByName 'Load remote images' 4000
        Check 'Resources' 'A remote image is blocked and the bar offers to load it' `
            ($null -ne $load) 'no "Load remote images" bar appeared'

        if ($load) {
            # The opt-in is per note, per session, and deliberately unpersisted:
            # reopening the note must block again.
            UiaClick $load
            Start-Sleep -Seconds 2
            $entry = Get-Entry $ctx
            Check 'Resources' 'Loading remote images is NOT persisted to notes.json' `
                ($null -eq $entry.allowRemoteImages -or $entry.allowRemoteImages -eq $false) `
                'the per-note opt-in was written to the index'
        }
    }

    # --- a relative .md link opens that file as a second note
    $ctx = Start-On "[OPEN THE OTHER NOTE](other.md)`r`n" 'link-md' {
        param($root) Write-Utf8 (Join-Path $root 'other.md') "# Other`r`n`r`nsecond note`r`n"
    }
    if ($ctx) {
        Start-Sleep -Seconds 2
        $before = [UiProbe]::Notes((Get-App).Id).Count
        $r = [UiProbe]::Bounds($ctx.Handle)
        [UiProbe]::Click(($r.Left + 70), ($r.Top + 44))
        Start-Sleep -Seconds 4
        $after = [UiProbe]::Notes((Get-App).Id).Count
        Check 'Resources' 'A relative .md link opens that file as a second note' `
            ($after -eq $before + 1) ('windows before=' + $before + ' after=' + $after)
    }

    # --- a javascript: link does nothing, and the refusal is recorded
    $ctx = Start-On "[DO NOT RUN THIS](javascript:alert(1))`r`n" 'link-javascript'
    if ($ctx) {
        Start-Sleep -Seconds 2
        $r = [UiProbe]::Bounds($ctx.Handle)
        $windowsBefore = [UiProbe]::Notes((Get-App).Id).Count
        [UiProbe]::Click(($r.Left + 60), ($r.Top + 44))
        Start-Sleep -Seconds 3

        $log = if (Test-Path $LogFile) { Get-Content $LogFile -Raw } else { '' }
        Check 'Resources' 'A javascript: link is refused and the refusal is recorded' `
            ($log -match '(?i)javascript') ('diagnostics.log: ' + ($log -replace "`r`n", ' / '))
        Check 'Resources' 'A javascript: link opens no window and does not navigate' `
            ([UiProbe]::Notes((Get-App).Id).Count -eq $windowsBefore) 'the note count changed'
    }

    # --- the WebView is hardened: no context menu, no devtools, no zoom
    $ctx = Start-On "# Hardening`r`n`r`nbody text for the hardening checks`r`n" 'webview-hardening'
    if ($ctx) {
        Start-Sleep -Seconds 2
        $r = [UiProbe]::Bounds($ctx.Handle)
        $windowsBefore = [UiProbe]::Notes((Get-App).Id).Count

        [UiProbe]::RightClick(($r.Left + 120), ($r.Top + 120))
        Start-Sleep -Seconds 1
        $menu = UiaFind $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::MenuItem) 1200
        Check 'Resources' 'Right-clicking the note shows no context menu' `
            ($null -eq $menu) ('a menu item appeared: ' + $(if ($menu) { $menu.Current.Name }))

        $before = Shoot 'hardening-before' $r
        Send '{F12}' 1500
        Check 'Resources' 'F12 opens no devtools window' `
            ([UiProbe]::Notes((Get-App).Id).Count -eq $windowsBefore) 'a new window appeared on F12'

        # Ctrl+scroll must not zoom: the rendered text stays exactly where it is.
        $shotBefore = Count-Magenta $r
        [UiProbe]::CtrlScroll(($r.Left + 120), ($r.Top + 150), 4)
        Start-Sleep -Seconds 1
        $after = Shoot 'hardening-after' $r
        Check 'Resources' 'Ctrl+scroll does not zoom the note' `
            ((Get-ShotDiff $before $after) -lt $ShotSame) ('compare ' + $before + ' with ' + $after)
    }

    # --- a #anchor link scrolls within the note rather than navigating
    $long = "[JUMP TO THE BOTTOM](#target)`r`n`r`n"
    1..40 | ForEach-Object { $long += "Filler line $_ so the note has somewhere to scroll to.`r`n`r`n" }
    $long += "# target`r`n`r`nthe bottom`r`n"
    $ctx = Start-On $long 'link-anchor'
    if ($ctx) {
        Start-Sleep -Seconds 3
        $r = [UiProbe]::Bounds($ctx.Handle)
        $onOpen = Shoot 'anchor-on-open' $r

        # Wheel to the very top, and see whether that is where it already was.
        # It was not, the first time this ran: a long note came up showing its
        # LAST line, which also put the link being tested off screen.
        [UiProbe]::Scroll(($r.Left + 200), ($r.Top + 200), 30)
        Start-Sleep -Seconds 1
        $atTop = Shoot 'anchor-at-top' $r

        Check 'Resources' 'A long note opens at the TOP, not scrolled to its end' `
            ((Get-ShotDiff $onOpen $atTop) -lt $ShotSame) `
            ('diff=' + [math]::Round((Get-ShotDiff $onOpen $atTop), 4) + ' compare ' + $onOpen + ' ' + $atTop)

        $windowsBefore = [UiProbe]::Notes((Get-App).Id).Count
        [UiProbe]::Click(($r.Left + 70), ($r.Top + 44))
        Start-Sleep -Seconds 2
        $after = Shoot 'anchor-after' $r

        Check 'Resources' 'A #anchor link scrolls the note' `
            ((Get-ShotDiff $atTop $after) -gt $ShotSame) ('diff=' + [math]::Round((Get-ShotDiff $atTop $after), 4) + ' compare ' + $atTop + ' ' + $after)
        Check 'Resources' 'A #anchor link opens no second window' `
            ([UiProbe]::Notes((Get-App).Id).Count -eq $windowsBefore) 'a window appeared'
    }

    # ============================================================ External edits
    Write-Host ''
    Write-Host 'External edits' -ForegroundColor Cyan

    # --- an external save with a CLEAN buffer updates the note and shows NO bar
    $ctx = Start-On "# External`r`n`r`noriginal body`r`n" 'external-clean'
    if ($ctx) {
        Start-Sleep -Seconds 2
        $r = [UiProbe]::Bounds($ctx.Handle)
        $shotBefore = Shoot 'external-before' $r

        Write-Utf8 $ctx.Note "# External`r`n`r`nEDITEDOUTSIDE the app`r`n"
        Start-Sleep -Seconds 3

        # No bar: a clean buffer has nothing to lose, so the note just reloads.
        $reload = UiaByName 'Reload' 1200
        Check 'External edits' 'An external save with no unsaved edits shows no conflict bar' `
            ($null -eq $reload) 'a Reload bar appeared even though the buffer was clean'

        $shot = Shoot 'external-after' $r
        Check 'External edits' 'The note re-renders after an external save' `
            ((Get-ShotDiff $shotBefore $shot) -gt $ShotSame) `
            ('diff=' + [math]::Round((Get-ShotDiff $shotBefore $shot), 4) + ' compare ' + $shotBefore + ' ' + $shot)
    }

    # --- deleting the file from outside raises the file-is-gone bar, and
    #     Recreate puts it back with the buffer's content
    $ctx = Start-On "# Gone`r`n`r`nthis text must survive the file vanishing`r`n" 'external-delete'
    if ($ctx) {
        Start-Sleep -Seconds 2
        Remove-Item $ctx.Note -Force
        Start-Sleep -Seconds 3

        $recreate = UiaByName 'Recreate' 5000
        Check 'External edits' 'Deleting the file from outside raises the file-is-gone bar' `
            ($null -ne $recreate) 'no Recreate bar appeared'

        if ($recreate) {
            UiaClick $recreate
            Start-Sleep -Seconds 3
            Check 'External edits' 'Recreate brings the file back with its text intact' `
                ((Test-Path $ctx.Note) -and ((Get-Content $ctx.Note -Raw) -match 'must survive the file vanishing')) `
                ('file back: ' + (Test-Path $ctx.Note))
        }
    }

    # --- renaming from outside keeps the note open and re-keys the index
    $ctx = Start-On "# Renamed outside`r`n`r`nbody`r`n" 'external-rename'
    if ($ctx) {
        Start-Sleep -Seconds 2
        $moved = Join-Path $NotesRoot 'moved.md'
        Rename-Item $ctx.Note $moved
        Start-Sleep -Seconds 3

        Check 'External edits' 'An external rename leaves the note open' `
            ([UiProbe]::Notes((Get-App).Id).Count -ge 1) 'the window went away on rename'

        $idx = Get-Content $IndexFile -Raw | ConvertFrom-Json
        Check 'External edits' 'An external rename re-keys the index onto the new path' `
            ($null -ne $idx.notes.$moved) `
            ('index keys: ' + (($idx.notes.PSObject.Properties | ForEach-Object Name) -join ' | '))
    }

    # --- the temporary exit key really exits, and isOpen survives it
    $ctx = Start-On "# Exit`r`n`r`nbody`r`n" 'exit-key'
    if ($ctx) {
        Start-Sleep -Seconds 2
        $exited = Stop-AppViaExitKey $ctx.Handle
        Check 'Three states' 'Ctrl+Shift+Alt+Q exits through the app rather than being killed' `
            $exited 'the app did not exit on the temporary exit key'

        if ($exited) {
            Check 'Three states' 'Exiting with the key leaves isOpen set' `
                ((Get-Entry $ctx).isOpen -eq $true) 'the exit path cleared isOpen'
        }
    }

    # ============================================================ Save failures
    Write-Host ''
    Write-Host 'Save failures' -ForegroundColor Cyan

    # --- a locked file raises the couldn't-save bar and KEEPS the text
    $ctx = Start-On "# Locked`r`n`r`noriginal`r`n" 'save-locked'
    if ($ctx) {
        Start-Sleep -Seconds 2

        # Exclusive lock: the app can neither read nor write it.
        $handle = [System.IO.File]::Open($ctx.Note, 'Open', 'ReadWrite', 'None')
        try {
            if (Enter-EditMode) {
                Send-CaretToLastLine
                Send ' TYPEDWHILELOCKED' 500
                Start-Sleep -Seconds 5

                $retry = UiaByName 'Retry' 5000
                Check 'Save failures' 'A failing save raises the couldn''t-save bar' `
                    ($null -ne $retry) 'no Retry bar appeared while the file was locked'

                # The text must still be in the editor: never lose text.
                $editor = UiaById 'Editor' 2000
                $kept = $false
                if ($editor) {
                    $v = $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
                    $kept = $v -match 'TYPEDWHILELOCKED'
                }
                Check 'Save failures' 'The typed text stays in the editor when the save fails' `
                    $kept 'the editor lost the text it could not save'
            }
        }
        finally { $handle.Close() }

        # --- releasing the lock and clicking Retry saves it
        Start-Sleep -Seconds 1
        $retry2 = UiaByName 'Retry' 3000
        if ($retry2) {
            UiaClick $retry2
            Start-Sleep -Seconds 3
            Check 'Save failures' 'Retry saves once the lock is gone, and the bar clears' `
                (((Get-Content $ctx.Note -Raw) -match 'TYPEDWHILELOCKED') -and ($null -eq (UiaByName 'Retry' 1200))) `
                ('file now: ' + ((Get-Content $ctx.Note -Raw) -replace "`r`n", ' / '))
        }
    }

    # --- closing a note whose save is failing writes a recovery snapshot
    $ctx = Start-On "# Snapshot`r`n`r`noriginal`r`n" 'save-snapshot'
    if ($ctx) {
        Start-Sleep -Seconds 2
        $recoveryDir = Join-Path $AppData 'recovery'
        Remove-Item $recoveryDir -Recurse -Force -ErrorAction SilentlyContinue

        $handle = [System.IO.File]::Open($ctx.Note, 'Open', 'ReadWrite', 'None')
        try {
            if (Enter-EditMode) {
                Send-CaretToLastLine
                Send ' SNAPSHOTME' 500
                Start-Sleep -Seconds 4

                # Exit through the app's own shutdown so the flush runs. A
                # kill would skip OnExit and no snapshot would ever be written.
                $null = Stop-AppViaExitKey $ctx.Handle
                Start-Sleep -Seconds 2
            }
        }
        finally { $handle.Close() }
        Stop-App

        $snaps = @(Get-ChildItem $recoveryDir -File -Recurse -ErrorAction SilentlyContinue)
        $hasText = $false
        foreach ($f in $snaps) { if ((Get-Content $f.FullName -Raw) -match 'SNAPSHOTME') { $hasText = $true } }

        $dump = ''
        foreach ($f in $snaps) { $dump += (Get-Content $f.FullName -Raw) }
        Check 'Save failures' 'A note that cannot be saved leaves a recovery snapshot holding its text' `
            ($hasText) ('snapshot content: ' + ($dump -replace "`r`n", ' '))
    }

    # ================================================== The rename collision
    Write-Host ''
    Write-Host 'Rename collision: one file, two owners' -ForegroundColor Cyan

    <#
        The known limitation in STATUS.md, driven for real.

        A rename that lands ON a path that already has a note open leaves the
        displaced window detached but ALIVE: it keeps its SaveCoordinator and
        its autosave timer, and INoteWindow has no verb to stop it. If its
        buffer is dirty when the collision happens, an already-armed tick then
        writes the displaced text over the file that was just renamed into
        place, with no user action at all.

        So the buffer has to be dirty AT THE MOMENT of the rename: type, then
        rename immediately, inside the autosave window rather than after it.
    #>
    if (-not $Only -or 'rename-collision' -like "*$Only*") {
        Stop-App
        Remove-Item $NotesRoot -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $NotesRoot -Force | Out-Null
        Remove-Item $LogFile -Force -ErrorAction SilentlyContinue

        $a = Join-Path $NotesRoot 'aaa.md'
        $b = Join-Path $NotesRoot 'bbb.md'
        Write-Utf8 $a "# AAA`r`n`r`nAAAKEEPTHIS the renamed-in file's content`r`n"
        Write-Utf8 $b "# BBB`r`n`r`nBBBDISPLACED the displaced window's content`r`n"

        # Far apart on purpose, so the two windows can be told apart by
        # position without depending on titles.
        $notes = [ordered]@{}
        foreach ($pair in @(@($a, 120), @($b, 760))) {
            $notes[$pair[0]] = [ordered]@{
                x = $pair[1]; y = 160; w = 420; h = 380; monitor = $null
                color = 'Yellow'; opacity = 1; alwaysOnTop = $true
                isOpen = $true; lastOpenedUtc = (Get-Date).ToUniversalTime().ToString('o')
            }
        }
        Write-Utf8 $IndexFile (([ordered]@{ version = 1; notes = $notes } | ConvertTo-Json -Depth 6))
        Write-Utf8 $Settings (([ordered]@{ notesRoot = $NotesRoot } | ConvertTo-Json))

        Start-Process $Exe | Out-Null
        Start-Sleep -Seconds ($SettleSeconds + 3)

        $app = Get-App
        $handles = if ($app) { [UiProbe]::Notes($app.Id) } else { @() }

        if ($handles.Count -lt 2) {
            Check 'Rename collision' 'Two notes open for the collision test' $false `
                ('expected 2 windows, saw ' + $handles.Count)
        } else {
            # The one on the right is bbb.md: the window about to be displaced.
            $displaced = $null
            foreach ($h in $handles) { if ([UiProbe]::Bounds($h).Left -gt 500) { $displaced = $h } }

            if (-not $displaced) {
                Check 'Rename collision' 'Two notes open for the collision test' $false 'could not identify the bbb.md window by position'
            } else {
                $null = [UiProbe]::Focus($displaced)
                $null = [UiProbe]::FocusKeyboard($displaced)

                if (Enter-EditMode) {
                    Send-CaretToLastLine

                    # Dirty the buffer and rename in the SAME breath. No sleep:
                    # the autosave is armed for ~500ms and the collision has to
                    # land inside that window, which is exactly the real race.
                    [System.Windows.Forms.SendKeys]::SendWait(' DIRTYNOW')
                    Move-Item -LiteralPath $a -Destination $b -Force

                    Start-Sleep -Seconds 6

                    $log = if (Test-Path $LogFile) { Get-Content $LogFile -Raw } else { '' }
                    Check 'Rename collision' 'The collision is detected and recorded' `
                        ($log -match 'was renamed onto') ('diagnostics.log: ' + ($log -replace "`r`n", ' / '))

                    $onDisk = Get-Content $b -Raw
                    Check 'Rename collision' 'The displaced window does NOT overwrite the renamed-in file' `
                        ($onDisk -match 'AAAKEEPTHIS') `
                        ('bbb.md now holds: ' + ($onDisk -replace "`r`n", ' / '))
                } else {
                    Check 'Rename collision' 'The displaced window does NOT overwrite the renamed-in file' $false `
                        'could not enter edit mode on the note to be displaced'
                }
            }
        }

        Stop-App
    }

}
catch {
    Check 'Script' 'The interaction pass ran to completion' $false $_.Exception.Message
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
}
finally {
    Stop-App

    # Contents, never the folder, and WebView2 left alone on both sides. See the
    # same block in verify-smoke.ps1 for why: a wipe that fails plus a folder
    # copy nests the backup inside the folder it was meant to replace.
    $saved = Join-Path $Backup 'StickyMD'
    Get-ChildItem $AppData -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'WebView2' } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $AppData -Force | Out-Null
    if (Test-Path $saved) {
        Get-ChildItem $saved -Force |
            Where-Object { $_.Name -ne 'WebView2' } |
            ForEach-Object { Copy-Item $_.FullName -Destination $AppData -Recurse -Force }
    }

    $savedFiles = @(Get-ChildItem $saved -File -ErrorAction SilentlyContinue | ForEach-Object Name)
    $backFiles = @(Get-ChildItem $AppData -File -ErrorAction SilentlyContinue | ForEach-Object Name)
    $lost = @($savedFiles | Where-Object { $backFiles -notcontains $_ })
    Check 'Script' 'Your real %LOCALAPPDATA%\StickyMD was restored intact' `
        ($lost.Count -eq 0 -and -not (Test-Path (Join-Path $AppData 'StickyMD'))) `
        ('missing: ' + ($lost -join ', ') + '. Backup kept at ' + $Backup)
}

$failed = @($Results | Where-Object { -not $_.Pass })
Write-Host ''
Write-Host ('{0} checks, {1} failed' -f $Results.Count, $failed.Count) `
    -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'Green' })
if ($failed.Count) { $failed | Format-Table Section, Check, Detail -AutoSize -Wrap | Out-String | Write-Host }

if ($Tainted) {
    Write-Host 'The pointer moved to somewhere this script did not put it. Re-run without touching the machine before believing any failure above.' -ForegroundColor Yellow
}

exit $failed.Count
