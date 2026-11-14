<#
.SYNOPSIS
    Builds the Windows release: a self-contained publish, the Velopack installer and
    portable bundle, and the channel feed the app updates from.

.DESCRIPTION
    Output (windows/dist/release by default):
      TinyHand-WindowsSetup.exe        Velopack installer (first install + updates)
      TinyHand-Windows-portable.zip    unpacked bundle, no install, no self-update
      releases.<channel>.json          channel feed the installed app polls
      TinyHand-<version>-<channel>-full.nupkg    full package
      TinyHand-<version>-<channel>-delta.nupkg   delta package (when a previous
                                                 release is present in the output dir)
      *.sha256                         checksums for the two user-facing files

    The version comes from the repository VERSION file (the same number the macOS DMG
    ships with) unless -Version is given. The channel and package names come from
    installer/velopack.json so CI and local runs cannot drift apart.

    Deltas: Velopack builds a delta against the previous package only when that package
    is in the output directory. Pass -FetchPrevious to download the latest release's
    .nupkg files with the GitHub CLI first (CI does this).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File windows/scripts/release-windows.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File windows/scripts/release-windows.ps1 -Version 1.2.0 -FetchPrevious
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$Channel = "",
    [string]$OutputDir = "",
    [switch]$NoPublish,
    [switch]$FetchPrevious
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$windowsRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $windowsRoot
$config = Get-Content (Join-Path $windowsRoot "installer/velopack.json") -Raw | ConvertFrom-Json

if (-not $Version) {
    $Version = (Get-Content (Join-Path $repoRoot "VERSION") -Raw).Trim()
}
if (-not $Channel) { $Channel = $config.channel }
if (-not $OutputDir) { $OutputDir = Join-Path $windowsRoot "dist/release" }

$publishDir = Join-Path $windowsRoot "dist/publish"
$outputPath = [System.IO.Path]::GetFullPath($OutputDir)

Write-Host "==> TinyHand $Version ($Channel) -> $outputPath"

if (-not $NoPublish) {
    Write-Host "==> Publishing self-contained $($config.runtime)"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish (Join-Path $windowsRoot "TinyHand/TinyHand.csproj") `
        -c Release `
        -r $config.runtime `
        --self-contained true `
        -p:Version=$Version `
        -p:DebugType=none `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

if (-not (Test-Path (Join-Path $publishDir $config.mainExe))) {
    throw "$publishDir does not contain $($config.mainExe); run without -NoPublish first"
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

if ($FetchPrevious) {
    # Delta packages need the previous release's nupkg in the output directory. The macOS
    # job creates the release for this tag before this job runs, so skip the current tag
    # and take the newest release that is not it.
    Write-Host "==> Fetching the previous release packages for deltas"
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Host "    gh CLI not found; skipping (no delta packages)"
    } else {
        $currentTag = "v$Version"
        $releases = gh release list --limit 10 --json tagName 2>$null | ConvertFrom-Json
        $previous = ($releases | Where-Object { $_.tagName -ne $currentTag } | Select-Object -First 1).tagName
        if ($previous) {
            Write-Host "    previous release: $previous"
            gh release download $previous --pattern "*.nupkg" --dir $outputPath --clobber 2>$null
            if ($LASTEXITCODE -ne 0) { Write-Host "    no packages in $previous (first Windows release)" }
        } else {
            Write-Host "    no previous release found"
        }
    }
}

Write-Host "==> Packing with Velopack"
$vpkArgs = @(
    "pack",
    "--packId", $config.packId,
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--packTitle", $config.packTitle,
    "--packAuthors", $config.packAuthors,
    "--mainExe", $config.mainExe,
    "--channel", $Channel,
    "--runtime", $config.runtime,
    "--icon", (Join-Path $windowsRoot $config.icon),
    "--outputDir", $outputPath,
    "--delta", "BestSpeed"
)
& vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

# Velopack names the artifacts after the pack id and channel; the release uses the
# names the README and the download links point at. The setup exe is self-contained,
# so renaming it does not affect installs or updates (the feed references the nupkg).
$setup = Join-Path $outputPath "$($config.packId)-$Channel-Setup.exe"
$portable = Join-Path $outputPath "$($config.packId)-$Channel-Portable.zip"
if (Test-Path $setup) {
    Move-Item $setup (Join-Path $outputPath $config.setupName) -Force
}
if (Test-Path $portable) {
    Move-Item $portable (Join-Path $outputPath $config.portableName) -Force
}

foreach ($name in @($config.setupName, $config.portableName)) {
    $file = Join-Path $outputPath $name
    if (Test-Path $file) {
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name" | Set-Content "$file.sha256" -Encoding ascii
    }
}

Write-Host ""
Write-Host "==> Release assets in $outputPath"
Get-ChildItem $outputPath -File |
    Sort-Object Name |
    ForEach-Object { "    {0,-52} {1,10:N0} bytes" -f $_.Name, $_.Length }
