# TinyHand M1 cursor + toolbar visual check.
#
# Deterministic setup: a probe form with a known default (arrow) cursor sits at a
# fixed spot; the system cursor over it is inspected via GetCursorInfo, and the
# system scheme cursor (LoadImage OCR_NORMAL) via alpha statistics.
#
#  0. baseline (no app): probe form shows the real arrow (opaque pixels).
#  1. DRAW mode: cursor over the form is suppressed (NULL) - overlay takes the hit.
#  2. toolbar strip renders top-center (screen pixel diff vs baseline).
#  3. CLICK-THROUGH (double Shift): scheme cursor is fully transparent (alpha 0,
#     no black square); the global cursor over the form is the transparent blank.
#  4. Deactivate (Ctrl+Alt+H): scheme cursor restored, pixel-perfect (registry reload).
#  5. Repeated double-Shift cycles + deactivate still restore perfectly (regression
#     for the "saved blank instead of original" poisoning bug).
#
# Run: powershell -STA -ExecutionPolicy Bypass -File cursorcheck-m1.ps1

$ErrorActionPreference = 'Stop'
$TinyHandExe = Join-Path $PSScriptRoot '..\TinyHand\bin\Debug\net10.0-windows\TinyHand.exe'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct POINT { public int X; public int Y; }
public struct CURSORINFO { public int cbSize; public uint flags; public IntPtr hCursor; public POINT pt; }
public struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
public struct BITMAP { public int bmType; public int bmWidth; public int bmHeight; public int bmWidthBytes; public ushort bmPlanes; public ushort bmBitsPixel; public IntPtr bmBits; }
public struct BITMAPINFOHEADER { public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount; public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant; }
public static class W {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO piconinfo);
    [DllImport("user32.dll")] public static extern IntPtr LoadImage(IntPtr h, uint n, uint t, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint b, IntPtr c, uint d);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int c, ref BITMAP o);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
}
'@
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Pump([int]$ms) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.ElapsedMilliseconds -lt $ms) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 10 } }
function KeyPress([byte]$vk) { [W]::keybd_event($vk,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 40; [W]::keybd_event($vk,0,2,[UIntPtr]::Zero); Start-Sleep -Milliseconds 40 }
function DoubleShift() { KeyPress 0x10; Start-Sleep -Milliseconds 150; KeyPress 0x10; Pump 500 }
function Hotkey() {
    [W]::keybd_event(0x11,0,0,[UIntPtr]::Zero); [W]::keybd_event(0x12,0,0,[UIntPtr]::Zero); [W]::keybd_event(0x48,0,0,[UIntPtr]::Zero); Pump 80
    [W]::keybd_event(0x48,0,2,[UIntPtr]::Zero); [W]::keybd_event(0x12,0,2,[UIntPtr]::Zero); [W]::keybd_event(0x11,0,2,[UIntPtr]::Zero); Pump 900
}
# Alpha statistics of a cursor's 32bpp color bitmap.
function CursorAlphaStats([IntPtr]$hCursor) {
    if ($hCursor -eq [IntPtr]::Zero) { return @{ Total = 0; Opaque = 0; AlphaSum = -1; Mono = $false } }
    $ii = New-Object ICONINFO
    if (-not [W]::GetIconInfo($hCursor, [ref]$ii)) { return @{ Total = 0; Opaque = 0; AlphaSum = -2; Mono = $false } }
    if ($ii.hbmColor -eq [IntPtr]::Zero) { return @{ Total = 0; Opaque = -1; AlphaSum = -1; Mono = $true } }
    $bm = New-Object BITMAP
    [void][W]::GetObject($ii.hbmColor, [Runtime.InteropServices.Marshal]::SizeOf([type][BITMAP]), [ref]$bm)
    $w = $bm.bmWidth; $h = $bm.bmHeight
    $bits = New-Object byte[] ($w * $h * 4)
    $bmi = New-Object BITMAPINFOHEADER
    $bmi.biSize = [Runtime.InteropServices.Marshal]::SizeOf([type][BITMAPINFOHEADER])
    $bmi.biWidth = $w; $bmi.biHeight = -$h; $bmi.biPlanes = 1; $bmi.biBitCount = 32
    $hdc = [W]::GetDC([IntPtr]::Zero)
    $null = [W]::GetDIBits($hdc, $ii.hbmColor, 0, [uint32]$h, $bits, [ref]$bmi, 0)
    $opaque = 0; $alphaSum = 0
    for ($p = 3; $p -lt $bits.Length; $p += 4) {
        if ($bits[$p] -gt 0) { $opaque++ }
        $alphaSum += $bits[$p]
    }
    return @{ Total = ($w * $h); Opaque = $opaque; AlphaSum = $alphaSum; Mono = $false }
}
function SchemeStats() {
    $h = [W]::LoadImage([IntPtr]::Zero, 32512, 2, 0, 0, 0x8000) # OCR_NORMAL, IMAGE_CURSOR, LR_SHARED
    return CursorAlphaStats $h
}
function GlobalStats() {
    $ci = New-Object CURSORINFO
    $ci.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][CURSORINFO])
    if (-not [W]::GetCursorInfo([ref]$ci)) { return @{ Flags = -1 } }
    $s = CursorAlphaStats $ci.hCursor
    $s['Flags'] = $ci.flags
    return $s
}
function MoveOverForm() {
    [void][W]::SetCursorPos(1600, 520) # probe form center
    [W]::mouse_event(1, 2, 0, 0, [UIntPtr]::Zero)
    Pump 500
}

$results = [System.Collections.Generic.List[string]]::new()
function Test([string]$name, [bool]$cond, [string]$detail) {
    $line = ("{0}: {1} ({2})" -f ($(if ($cond) { 'PASS' } else { 'FAIL' })), $name, $detail)
    $script:results.Add($line)
    Write-Host $line
}

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Bounds = New-Object System.Drawing.Rectangle(1300, 320, 600, 400)
$form.TopMost = $true
$form.BackColor = [System.Drawing.Color]::DarkSlateGray
$form.Cursor = [System.Windows.Forms.Cursors]::Default # known: plain arrow

$app = $null
try {
    Get-Process -Name TinyHand -ErrorAction SilentlyContinue | Stop-Process -Force
    [void][W]::SystemParametersInfo(0x57, 0, [IntPtr]::Zero, 0) # heal from earlier runs
    Pump 300
    $form.Show()
    Pump 300

    # 0. baseline over the probe form: real arrow, no app.
    MoveOverForm
    $g = GlobalStats
    Test '0 baseline arrow over probe form' (($g.Flags -band 1) -and (-not $g.Mono) -and ($g.Opaque -gt 50)) "flags=$($g.Flags) mono=$($g.Mono) opaque=$($g.Opaque)/$($g.Total)"

    # Toolbar strip baseline capture (before overlay exists).
    $screenW = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
    $totalW = 280 + 12 + 120 # keep in sync with ToolbarModel constants
    $x0 = [int](($screenW - $totalW) / 2)
    $strip = New-Object System.Drawing.Rectangle(($x0 - 100), 4, ($totalW + 200), 90)
    $baseline = New-Object System.Drawing.Bitmap($strip.Width, $strip.Height)
    $gfx = [System.Drawing.Graphics]::FromImage($baseline)
    $gfx.CopyFromScreen($strip.X, $strip.Y, 0, 0, $baseline.Size)
    $gfx.Dispose()

    # Keep the real profile clean and skip the first-run card (M4).
    $env:TH_WELCOME_SHOWN = '1'
    $env:TH_SETTINGS_DIR = Join-Path $env:TEMP 'tinyhand-e2e'
    Remove-Item -Recurse -Force $env:TH_SETTINGS_DIR -ErrorAction SilentlyContinue
    $app = Start-Process -FilePath $TinyHandExe -PassThru
    Pump 3500 # auto-activate (draw mode)

    # 1. draw mode suppresses the real cursor over the form (overlay takes the hit).
    MoveOverForm
    $g = GlobalStats
    $hidden = (($g.Flags -band 1) -eq 0) -or ($g.Opaque -eq 0 -and $g.AlphaSum -eq 0)
    Test '1 draw mode hides real cursor' $hidden "flags=$($g.Flags) mono=$($g.Mono) opaque=$($g.Opaque)/$($g.Total)"

    # 2. toolbar renders top-center.
    $now = New-Object System.Drawing.Bitmap($strip.Width, $strip.Height)
    $gfx = [System.Drawing.Graphics]::FromImage($now)
    $gfx.CopyFromScreen($strip.X, $strip.Y, 0, 0, $now.Size)
    $gfx.Dispose()
    $diff = 0
    $rect1 = New-Object System.Drawing.Rectangle(0, 0, $baseline.Width, $baseline.Height)
    $rect2 = New-Object System.Drawing.Rectangle(0, 0, $now.Width, $now.Height)
    $bd1 = $baseline.LockBits($rect1, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bd2 = $now.LockBits($rect2, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    for ($i = 0; $i -lt ($bd1.Stride * $bd1.Height); $i += 4) {
        if ([Math]::Abs([Runtime.InteropServices.Marshal]::ReadByte($bd1.Scan0, $i) - [Runtime.InteropServices.Marshal]::ReadByte($bd2.Scan0, $i)) -gt 24) { $diff++ }
    }
    $baseline.UnlockBits($bd1); $now.UnlockBits($bd2)
    $baseline.Dispose(); $now.Dispose()
    Test '2 toolbar renders top-center' ($diff -gt 2000) "diffPixels=$diff"

    # 3. click-through: fully transparent scheme cursor (regression: black square).
    DoubleShift
    Pump 300
    $s = SchemeStats
    MoveOverForm
    $g = GlobalStats
    Test '3a click-through scheme cursor transparent' (-not $s.Mono -and ($s.Opaque -eq 0) -and ($s.AlphaSum -eq 0)) "mono=$($s.Mono) opaque=$($s.Opaque)/$($s.Total) alphaSum=$($s.AlphaSum)"
    Test '3b click-through global cursor transparent' (($g.Flags -band 1) -and (-not $g.Mono) -and ($g.Opaque -eq 0)) "flags=$($g.Flags) mono=$($g.Mono) opaque=$($g.Opaque)/$($g.Total)"

    # 4. deactivate: real arrow fully restored.
    DoubleShift # back to draw (restores from saved copies)
    Hotkey      # deactivate
    MoveOverForm
    $s2 = SchemeStats
    $g = GlobalStats
    $restored = (($g.Flags -band 1) -and (-not $g.Mono) -and ($g.Opaque -gt 50)) -and (-not $s2.Mono) -and ($s2.Opaque -gt 50)
    Test '4 deactivate restores real cursor' $restored "global: flags=$($g.Flags) opaque=$($g.Opaque)/$($g.Total); scheme: opaque=$($s2.Opaque)/$($s2.Total) alphaSum=$($s2.AlphaSum)"

    # 5. repeated cycles do not poison the restore (regression for save-once fix).
    Hotkey; Pump 300      # activate
    DoubleShift; Pump 300 # click-through
    DoubleShift; Pump 300 # draw
    DoubleShift; Pump 300 # click-through
    DoubleShift; Pump 300 # draw
    Hotkey; Pump 300      # deactivate
    MoveOverForm
    $s3 = SchemeStats
    $g = GlobalStats
    $stable = (($g.Flags -band 1) -and (-not $g.Mono) -and ($g.Opaque -gt 50)) -and (-not $s3.Mono) -and ($s3.Opaque -gt 50)
    Test '5 restore survives repeated toggles' $stable "global: opaque=$($g.Opaque)/$($g.Total); scheme: opaque=$($s3.Opaque)/$($s3.Total) alphaSum=$($s3.AlphaSum)"
}
finally {
    try { $form.Close() } catch {}
    Pump 200
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    Get-Process -Name TinyHand -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    [void][W]::SystemParametersInfo(0x57, 0, [IntPtr]::Zero, 0) # safety
}

$failed = ($results | Where-Object { $_ -like 'FAIL*' }).Count
Write-Host ""
Write-Host ("{0}/{1} passed" -f ($results.Count - $failed), $results.Count)
exit $(if ($failed -gt 0) { 1 } else { 0 })
