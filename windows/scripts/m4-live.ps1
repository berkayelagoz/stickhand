# TinyHand M4 live integration check: protocol -> second instance -> pipe -> running app.
# Verifies state.json (pid/active/style/sound/styles), the stickhand:// commands against a real
# process, and that a normal exit (Ctrl+Alt+Q) removes state.json.
# Run: powershell -STA -ExecutionPolicy Bypass -File scripts/m4-live.ps1
$ErrorActionPreference = 'Stop'
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
'@ -Name K -Namespace Live
$dir = Join-Path $env:TEMP 'tinyhand-m4-live'
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
$env:TH_SETTINGS_DIR = $dir
$env:TH_WELCOME_SHOWN = '1'
$exe = Join-Path $PSScriptRoot '..\StickHand\bin\Debug\net10.0-windows\StickHand.exe'
Get-Process -Name StickHand -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$app = Start-Process -FilePath $exe -PassThru
Start-Sleep 4
$state = Get-Content (Join-Path $dir 'state.json') -Raw | ConvertFrom-Json
"launch: pid=$($state.pid) active=$($state.active) style=$($state.style) styles=$($state.styles.Count) sound=$($state.soundOn)"
"processes: $((Get-Process -Name StickHand -ErrorAction SilentlyContinue).Count)"

Start-Process 'stickhand://off'; Start-Sleep 2
$state = Get-Content (Join-Path $dir 'state.json') -Raw | ConvertFrom-Json
"off: active=$($state.active)"

Start-Process 'stickhand://style/coral'; Start-Sleep 2
$state = Get-Content (Join-Path $dir 'state.json') -Raw | ConvertFrom-Json
"style: style=$($state.style) active=$($state.active)"

Start-Process 'stickhand://sound/off'; Start-Sleep 2
$state = Get-Content (Join-Path $dir 'state.json') -Raw | ConvertFrom-Json
"sound: soundOn=$($state.soundOn)"

Start-Process 'stickhand://toggle'; Start-Sleep 2
$state = Get-Content (Join-Path $dir 'state.json') -Raw | ConvertFrom-Json
"toggle: active=$($state.active)"

# Ctrl+Alt+Q: normal exit must delete state.json.
[Live.K]::keybd_event(0x11,0,0,[UIntPtr]::Zero)
[Live.K]::keybd_event(0x12,0,0,[UIntPtr]::Zero)
[Live.K]::keybd_event(0x51,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 120
[Live.K]::keybd_event(0x51,0,2,[UIntPtr]::Zero)
[Live.K]::keybd_event(0x12,0,2,[UIntPtr]::Zero)
[Live.K]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
Start-Sleep 6
"after quit: processes=$((Get-Process -Name StickHand -ErrorAction SilentlyContinue).Count) stateExists=$(Test-Path (Join-Path $dir 'state.json'))"
"settingsExists=$(Test-Path (Join-Path $dir 'settings.json'))"
