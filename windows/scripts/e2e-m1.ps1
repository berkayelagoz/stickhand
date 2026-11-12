# TinyHand M1 end-to-end interaction harness.
# Verifies the M1 interaction matrix by injecting real input (SendInput-level
# mouse_event/keybd_event) and observing whether a "logger" window underneath
# the overlay receives clicks:
#
#  A. drawing-mode click is swallowed (below app gets nothing)
#  B. down swallowed, double-Shift mid-drag, up swallowed too (down/up same target)
#  C. click-through click reaches the app below, over the hand's opaque pixels
#  D. down passes in click-through, double-Shift back to draw, up still reaches below
#  E. toolbar buttons route from the hook (click-through toggle + exit)
#  F. Ctrl+Alt+H toggle cycles re-install the hooks (swallow observed again)
#  G. Esc closes and clicks reach below again
#
# Cursor visibility is visual and remains a manual check (see PROGRESS.md).
# Run: powershell -STA -ExecutionPolicy Bypass -File e2e-m1.ps1

$ErrorActionPreference = 'Stop'
$TinyHandExe = Join-Path $PSScriptRoot '..\TinyHand\bin\Debug\net10.0-windows\TinyHand.exe'
$ResultFile  = Join-Path $PSScriptRoot '..\e2e-m1-result.txt'

Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
[DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr h, int i);
[DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr h);
[DllImport("user32.dll")] public static extern bool ReleaseCapture();
[DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint b, IntPtr c, uint d);
'@ -Name Win -Namespace E2E

$null = [E2E.Win]::SetProcessDpiAwarenessContext([IntPtr](-4)) # per-monitor v2: physical coords
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$results = [System.Collections.Generic.List[string]]::new()
function Test([string]$name, [bool]$cond, [string]$detail) {
    $line = ("{0}: {1} ({2})" -f ($(if ($cond) { 'PASS' } else { 'FAIL' })), $name, $detail)
    $script:results.Add($line)
    Write-Host $line
}

# ---------------------------------------------------------------- logger window
$events = [System.Collections.Generic.List[string]]::new()
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Bounds = New-Object System.Drawing.Rectangle(200, 300, 800, 500)
$form.TopMost = $true
$form.BackColor = [System.Drawing.Color]::DarkSlateGray
$label = New-Object System.Windows.Forms.Label
$label.Text = "TinyHand M1 E2E logger - lutfen dokunmayin"
$label.ForeColor = [System.Drawing.Color]::White
$label.AutoSize = $true
$label.Location = New-Object System.Drawing.Point(20, 20)
[void]$form.Controls.Add($label)
$form.Add_MouseDown({ param($s, $e)
    [void][E2E.Win]::SetCapture($form.Handle)
    $p = [System.Windows.Forms.Cursor]::Position
    $script:events.Add("D $($p.X),$($p.Y)")
})
$form.Add_MouseUp({ param($s, $e)
    [void][E2E.Win]::ReleaseCapture()
    $p = [System.Windows.Forms.Cursor]::Position
    $script:events.Add("U $($p.X),$($p.Y)")
})

function Pump([int]$ms) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 10
    }
}
function ClickAt([int]$x, [int]$y) {
    [void][E2E.Win]::SetCursorPos($x, $y); Pump 150
    [E2E.Win]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Pump 100  # LEFTDOWN
    [E2E.Win]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Pump 200  # LEFTUP
}
function KeyPress([byte]$vk) {
    [E2E.Win]::keybd_event($vk, 0, 0, [UIntPtr]::Zero); Pump 40
    [E2E.Win]::keybd_event($vk, 0, 2, [UIntPtr]::Zero); Pump 40
}
function DoubleShift() { KeyPress 0x10; Pump 150; KeyPress 0x10; Pump 350 }
function Hotkey() { # Ctrl+Alt+H
    [E2E.Win]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    [E2E.Win]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [E2E.Win]::keybd_event(0x48, 0, 0, [UIntPtr]::Zero); Pump 80
    [E2E.Win]::keybd_event(0x48, 0, 2, [UIntPtr]::Zero)
    [E2E.Win]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [E2E.Win]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    Pump 600
}
function Esc() { KeyPress 0x1B; Pump 600 }

$app = $null
try {
    # ---------------------------------------------------------------- launch
    Get-Process -Name TinyHand -ErrorAction SilentlyContinue | Stop-Process -Force
    [void][E2E.Win]::SystemParametersInfo(0x57, 0, [IntPtr]::Zero, 0) # SPI_SETCURSORS: reset cursor state
    Pump 300

    $form.Show()
    Pump 300

    $app = Start-Process -FilePath $TinyHandExe -PassThru
    Pump 3500 # app auto-activates: overlay + hooks up

    Test 'P0 baseline' ($events.Count -eq 0) "baseline events=$($events.Count)"

    # A: draw-mode click swallowed by the hook
    ClickAt 600 550
    Test 'A drawing click swallowed' ($events.Count -eq 0) "events=$($events.Count)"

    # B: down swallowed, double-shift mid-drag, up swallowed too
    [void][E2E.Win]::SetCursorPos(500, 600); Pump 150
    [E2E.Win]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Pump 250
    DoubleShift
    [E2E.Win]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Pump 250
    Test 'B down/up same target (swallow shift)' ($events.Count -eq 0) "events=$($events.Count)"
    # mode now: click-through

    # C: click-through click reaches the logger (over the hand's opaque pixels)
    ClickAt 500 600
    Test 'C click-through reaches app below' ($events.Count -eq 2) "events=$($events.Count) [$($events -join '; ')]"

    # D: down reaches below in click-through, mode flips to draw, up still reaches below
    [void][E2E.Win]::SetCursorPos(550, 650); Pump 150
    [E2E.Win]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Pump 250 # D logged (capture held)
    DoubleShift # back to draw
    [E2E.Win]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Pump 250 # up passes, capture owner gets it
    Test 'D down/up same target (pass shift)' ($events.Count -eq 4) "events=$($events.Count) [$($events -join '; ')]"
    # mode now: draw

    # E: toolbar buttons (primary monitor, top-center, ToolbarModel geometry)
    # Toolbar geometry — keep in sync with TinyHand/Logic/ToolbarModel.cs constants:
    # ButtonHeightPx=56, TopMarginPx=16, GapPx=12, ToggleWidthPx=280, ExitWidthPx=120.
    $hdc = [E2E.Win]::GetDC([IntPtr]::Zero)
    $dpi = [E2E.Win]::GetDeviceCaps($hdc, 88)
    if ($dpi -lt 96) { $dpi = 96 }
    $s = [Math]::Max(1, [int][Math]::Round($dpi / 96.0, [System.MidpointRounding]::AwayFromZero))
    $screenW = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
    $totalW = (280 + 12 + 120) * $s
    $x0 = [Math]::Max(0, [int](($screenW - $totalW) / 2))
    $ty = [int](16 * $s) + [int](($s * 56) / 2)
    $cx = $x0 + [int](($s * 280) / 2)                  # click_through button center
    $ex = $x0 + (280 + 12) * $s + [int](($s * 120) / 2) # exit button center

    ClickAt $cx $ty  # toolbar: toggle -> click-through
    ClickAt 500 600  # must be delivered now
    Test 'E1 toolbar click-through toggles' ($events.Count -eq 6) "events=$($events.Count) toolbar=($cx,$ty) dpi=$dpi"

    DoubleShift # back to draw mode so toolbar routes again
    ClickAt $ex $ty # toolbar: exit -> deactivate
    ClickAt 500 600  # inactive: delivered
    Test 'E2 toolbar exit deactivates' ($events.Count -eq 8) "events=$($events.Count) exit=($ex,$ty)"

    # F: Ctrl+Alt+H cycles (hook re-install between deactivations)
    Hotkey # activate
    ClickAt 500 600 # swallowed again: hooks reinstalled by the toggle
    Test 'F1 hotkey re-activate swallows' ($events.Count -eq 8) "events=$($events.Count)"
    Hotkey # deactivate
    ClickAt 500 600
    Test 'F2 hotkey deactivate passes' ($events.Count -eq 10) "events=$($events.Count)"

    # G: Esc closes from active; clicks pass below
    Hotkey # activate
    Esc
    ClickAt 500 600
    Test 'G Esc closes, clicks pass' ($events.Count -eq 12) "events=$($events.Count)"
}
finally {
    try { $form.Close() } catch {}
    Pump 300
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    Get-Process -Name TinyHand -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    [void][E2E.Win]::SystemParametersInfo(0x57, 0, [IntPtr]::Zero, 0) # safety: restore cursor schemes
}

$failed = ($results | Where-Object { $_ -like 'FAIL*' }).Count
$results | Set-Content -Path $ResultFile -Encoding UTF8
Write-Host ""
Write-Host "results -> $ResultFile"
Write-Host ("{0}/{1} passed" -f ($results.Count - $failed), $results.Count)
exit $(if ($failed -gt 0) { 1 } else { 0 })
