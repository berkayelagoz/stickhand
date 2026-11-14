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
$TinyHandExe = Join-Path $PSScriptRoot '..\StickHand\bin\Debug\net10.0-windows\StickHand.exe'
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
function PixelAt([int]$x, [int]$y) {
    $bmp = New-Object System.Drawing.Bitmap 1, 1
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size 1, 1))
    $c = $bmp.GetPixel(0, 0)
    $g.Dispose(); $bmp.Dispose()
    return $c
}

$app = $null
try {
    # ---------------------------------------------------------------- launch
    Get-Process -Name StickHand -ErrorAction SilentlyContinue | Stop-Process -Force
    [void][E2E.Win]::SystemParametersInfo(0x57, 0, [IntPtr]::Zero, 0) # SPI_SETCURSORS: reset cursor state
    Pump 300

    $form.Show()
    Pump 300

    # Keep the real profile clean and skip the first-run card (M4).
    $env:TH_WELCOME_SHOWN = '1'
    $env:TH_SETTINGS_DIR = Join-Path $env:TEMP 'stickhand-e2e'
    Remove-Item -Recurse -Force $env:TH_SETTINGS_DIR -ErrorAction SilentlyContinue
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

    # E: toolbar buttons (primary monitor, top-center). The geometry comes from the app
    # itself (--toolbargeom) so the script never drifts from ToolbarModel.
    $geomFile = Join-Path $env:TEMP 'tinyhand-toolbargeom.txt'
    Start-Process -FilePath $TinyHandExe -ArgumentList '--toolbargeom' -Wait -NoNewWindow -RedirectStandardOutput $geomFile
    $buttons = @{}
    foreach ($line in Get-Content $geomFile) {
        $p = $line -split '\s+'
        if ($p[0] -eq 'panel') { continue }
        $buttons[$p[0]] = @{ x = [int]$p[2]; y = [int]$p[3]; w = [int]$p[4]; h = [int]$p[5] }
    }
    function Center([string]$id) {
        $b = $buttons[$id]
        return @(($b.x + [int]($b.w / 2)), ($b.y + [int]($b.h / 2)))
    }
    $coral = Center 'coral'
    $exit = Center 'exit'
    # Selected style gets a white 18 % highlight: sample the button's top edge (the thumbnail
    # is inset by 4 px, so the highlight is visible there).
    $probeX = $buttons['coral'].x + [int]($buttons['coral'].w / 2)
    $probeY = $buttons['coral'].y + 2
    $before = PixelAt $probeX $probeY
    ClickAt $coral[0] $coral[1]   # toolbar: pick the coral style (swallowed, routed)
    Pump 300
    $after = PixelAt $probeX $probeY
    Test 'E1 toolbar style pick routed' ($events.Count -eq 4 -and $after.GetBrightness() -gt $before.GetBrightness()) `
        "events=$($events.Count) before=$before after=$after"

    ClickAt $exit[0] $exit[1]     # toolbar: exit -> deactivate
    ClickAt 500 600               # inactive: delivered
    Test 'E2 toolbar exit deactivates' ($events.Count -eq 6) "events=$($events.Count) exit=($($exit[0]),$($exit[1]))"

    # F: Ctrl+Alt+H cycles (hook re-install between deactivations)
    Hotkey # activate
    ClickAt 500 600 # swallowed again: hooks reinstalled by the toggle
    Test 'F1 hotkey re-activate swallows' ($events.Count -eq 6) "events=$($events.Count)"
    Hotkey # deactivate
    ClickAt 500 600
    Test 'F2 hotkey deactivate passes' ($events.Count -eq 8) "events=$($events.Count)"

    # G: Esc closes from active; clicks pass below
    Hotkey # activate
    Esc
    ClickAt 500 600
    Test 'G Esc closes, clicks pass' ($events.Count -eq 10) "events=$($events.Count)"
}
finally {
    try { $form.Close() } catch {}
    Pump 300
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    Get-Process -Name StickHand -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    [void][E2E.Win]::SystemParametersInfo(0x57, 0, [IntPtr]::Zero, 0) # safety: restore cursor schemes
}

$failed = ($results | Where-Object { $_ -like 'FAIL*' }).Count
$results | Set-Content -Path $ResultFile -Encoding UTF8
Write-Host ""
Write-Host "results -> $ResultFile"
Write-Host ("{0}/{1} passed" -f ($results.Count - $failed), $results.Count)
exit $(if ($failed -gt 0) { 1 } else { 0 })
