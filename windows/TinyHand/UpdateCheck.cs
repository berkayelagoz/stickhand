using System.Diagnostics;

namespace TinyHand;

/// <summary>
/// Update diagnostic: prints the install state and the release the channel offers, and with
/// <c>--apply</c> downloads and applies it (Velopack restarts the app into the new version).
///
/// Run: TinyHand.exe --updatecheck [--apply]
/// The release harness points <c>TH_UPDATE_SOURCE</c> at a local feed to verify the whole
/// check → download → apply path without publishing a release.
/// </summary>
internal static class UpdateCheck
{
    public static int Run(string[] args)
    {
        Program.TryAttachConsole();
        bool apply = args.Contains("--apply");

        Console.WriteLine($"updatecheck: version={Updater.Version} installed={Updater.IsInstalled} " +
            $"portable={Updater.IsPortable} channel={Updater.Channel}");

        var pending = Updater.PendingRestart;
        if (pending is not null)
            Console.WriteLine($"updatecheck: pending restart {Updater.Describe(pending)}");

        var watch = Stopwatch.StartNew();
        var update = Updater.Check();
        watch.Stop();
        if (update is null)
        {
            Console.WriteLine($"updatecheck: no update ({watch.ElapsedMilliseconds} ms)");
            return 0;
        }

        var target = update.TargetFullRelease;
        Console.WriteLine($"updatecheck: update {Updater.Describe(target)} " +
            $"({target.Size / 1024 / 1024} MB, deltas={update.DeltasToTarget?.Length ?? 0}, " +
            $"{watch.ElapsedMilliseconds} ms)");

        if (!apply) return 0;

        Console.WriteLine("updatecheck: downloading");
        Updater.DownloadAndRestart(update, percent => Console.WriteLine($"  {percent}%"));
        Console.WriteLine("updatecheck: apply returned without restarting");
        return 1;
    }
}
