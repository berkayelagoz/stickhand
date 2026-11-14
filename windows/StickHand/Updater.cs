using System.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace StickHand;

/// <summary>
/// Velopack self-update: the Windows counterpart of the macOS Sparkle updater.
///
/// The Windows channel is <c>win</c>, so the feed is <c>releases.win.json</c> plus the
/// full and delta <c>.nupkg</c> packages published next to it (see
/// <c>installer/velopack.json</c> and <c>scripts/release-windows.ps1</c>).
///
/// The repository is private, so Velopack's token-less <c>GithubSource</c> cannot read
/// its releases and the app offers no update check. The class stays for the
/// install/uninstall hooks and for the <c>--updatecheck</c> diagnostic; a public
/// repository makes it work again unchanged.
///
/// A development build (run from <c>bin/</c>) is not installed, so Velopack cannot
/// update it: <see cref="IsInstalled"/> is false.
/// </summary>
public static class Updater
{
    /// <summary>Velopack channel; the feed asset is <c>releases.win.json</c>.</summary>
    public const string Channel = "win";

    /// <summary>Repository the release feed is read from.</summary>
    public const string RepositoryUrl = "https://github.com/berkayelagoz/stickhand";

    /// <summary>Assembly version (from the repository VERSION file), e.g. <c>1.1.0</c>.</summary>
    public static string Version =>
        typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>True when running from a Velopack install (Setup.exe), false in a dev build.</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return CreateManager().IsInstalled; }
            catch (Exception ex)
            {
                Debug.WriteLine($"update manager unavailable: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>True for the portable (unpacked) bundle: it cannot self-update either.</summary>
    public static bool IsPortable
    {
        get
        {
            try { return CreateManager().IsPortable; }
            catch { return false; }
        }
    }

    /// <summary>An update that was downloaded earlier and applies on the next restart.</summary>
    public static VelopackAsset? PendingRestart
    {
        get
        {
            try { return CreateManager().UpdatePendingRestart; }
            catch { return null; }
        }
    }

    /// <summary>
    /// Check the channel for a newer release. Blocking (network): call it from a
    /// background thread. Returns null when there is nothing newer, when the build is
    /// not installed, or when the check fails.
    /// </summary>
    public static UpdateInfo? Check()
    {
        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled) return null;
            return manager.CheckForUpdates();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download the release and restart into it. Velopack exits the process, so nothing
    /// after this call runs. <paramref name="progress"/> receives 0–100.
    /// </summary>
    public static void DownloadAndRestart(UpdateInfo update, Action<int>? progress = null)
    {
        var manager = CreateManager();
        manager.DownloadUpdates(update, progress);
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }

    /// <summary>Apply an update downloaded on an earlier run (no network needed).</summary>
    public static void RestartPending()
    {
        var manager = CreateManager();
        var pending = manager.UpdatePendingRestart;
        if (pending is not null) manager.ApplyUpdatesAndRestart(pending);
    }

    private static UpdateManager CreateManager()
    {
        // TH_UPDATE_SOURCE points the updater at a local folder or URL instead of GitHub:
        // the release harness uses it to verify check → download → apply without publishing.
        string? source = Environment.GetEnvironmentVariable("TH_UPDATE_SOURCE");
        if (!string.IsNullOrEmpty(source))
            return new UpdateManager(source, new UpdateOptions { ExplicitChannel = Channel });

        return new UpdateManager(
            new GithubSource(RepositoryUrl, accessToken: null, prerelease: false),
            new UpdateOptions { ExplicitChannel = Channel });
    }

    /// <summary>Human-readable version of a release asset (Velopack's SemVer).</summary>
    public static string Describe(VelopackAsset asset) => asset.Version.ToString();
}
