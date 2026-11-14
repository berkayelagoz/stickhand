# Creates appicon.ico (if missing) and a desktop shortcut to the TinyHand build.
#
# 1. appicon.ico: 256x256 PNG-in-ICO (Vista+), the stylized hand glyph from the
#    tray icon on the macOS app's dark-purple background.
# 2. TinyHand.lnk on the user's Desktop pointing at the Debug build (portable
#    prototype; the installer in M5 will create its own shortcuts).
#
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File create-desktop-shortcut.ps1

$ErrorActionPreference = 'Stop'
$RepoDir = Split-Path $PSScriptRoot -Parent   # windows/
$ExePath = Join-Path $RepoDir 'StickHand\bin\Debug\net10.0-windows\StickHand.exe'
$IconPath = Join-Path $RepoDir 'StickHand\assets\appicon.ico'

Add-Type -AssemblyName System.Drawing

function New-AppIcon([string]$path) {
    $size = 256
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    try {
        # Rounded-square background, macOS app icon colors (dark purple, display-p3 ~0.096,0,0.111).
        $bgPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $r = 56
        $rect = New-Object System.Drawing.Rectangle(6, 6, ($size - 12), ($size - 12))
        $bgPath.AddArc($rect.X, $rect.Y, $r, $r, 180, 90)
        $bgPath.AddArc(($rect.Right - $r), $rect.Y, $r, $r, 270, 90)
        $bgPath.AddArc(($rect.Right - $r), ($rect.Bottom - $r), $r, $r, 0, 90)
        $bgPath.AddArc($rect.X, ($rect.Bottom - $r), $r, $r, 90, 90)
        $bgPath.CloseFigure()
        $bgBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 25, 3, 28))
        $g.FillPath($bgBrush, $bgPath)

        # Hand glyph: cream fingers (matches the tray icon design).
        $fg = [System.Drawing.Color]::FromArgb(255, 241, 233, 214) # ~display-p3 0.945,0.917,0.847
        $pen = New-Object System.Drawing.Pen ($fg, 20)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

        $g.DrawLine($pen, ($(85 - 20)), 176, ($(85 - 20)), 110)   # outer finger
        $g.DrawLine($pen, 128, 176, 128, 82)                      # middle finger (tallest)
        $g.DrawLine($pen, 176, 176, 176, 110)                     # outer finger
        # wrist
        $penW = New-Object System.Drawing.Pen ($fg, 20)
        $penW.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $penW.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($penW, 72, 200, 190, 200)

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)

        # ICO container with a single PNG entry.
        $png = $ms.ToArray()
        $out = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($out)
        $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)   # ICONDIR: type=icon, count=1
        $bw.Write([byte]0); $bw.Write([byte]0)                             # width/height 0 = 256
        $bw.Write([byte]0); $bw.Write([byte]0)                             # colors, reserved
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)                       # planes, bpp
        $bw.Write([UInt32]$png.Length)                                     # size
        $bw.Write([UInt32]22)                                              # offset (6 + 16)
        $bw.Write($png)
        $bw.Flush()
        [System.IO.File]::WriteAllBytes($path, $out.ToArray())
        $bw.Dispose(); $out.Dispose(); $ms.Dispose()
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

if (-not (Test-Path $ExePath)) {
    Write-Error "StickHand.exe bulunamadi: $ExePath (once dotnet build StickHand.sln)"
}

if (-not (Test-Path $IconPath)) {
    New-AppIcon $IconPath
    Write-Host "appicon.ico created: $IconPath"
}

$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)
$lnkPath = Join-Path $desktop 'StickHand.lnk'
$wsh = New-Object -ComObject WScript.Shell
$lnk = $wsh.CreateShortcut($lnkPath)
$lnk.TargetPath = $ExePath
$lnk.WorkingDirectory = Split-Path $ExePath -Parent
$lnk.IconLocation = "$IconPath,0"
$lnk.Description = 'StickHand drawing overlay (M1 prototype)'
$lnk.Save()
Write-Host "shortcut created: $lnkPath"
Write-Host "target: $ExePath"
