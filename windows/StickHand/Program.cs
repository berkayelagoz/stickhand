using System.Runtime.InteropServices;
using StickHand.Interop;
using StickHand.Logic;
using Velopack;

namespace StickHand;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [STAThread]
    private static int Main(string[] args)
    {
        // Velopack install/update/uninstall hooks run before anything else: the setup
        // launches the app with --veloapp-* arguments and expects it to handle them and
        // exit. A dev build (not installed) falls straight through.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(true)
            .SetAppUserModelId("com.kamranahmed.tinyhand")
            .Run();

        if (args.Contains("--spike"))
            return SpikeRunner.Run(Rest(args));

        if (args.Contains("--hookcheck"))
            return HookCheck.Run(Rest(args));

        if (args.Contains("--fpscheck"))
            return FpsCheck.Run(Rest(args));

        if (args.Contains("--inkcheck"))
            return InkCheck.Run(Rest(args));

        if (args.Contains("--m3check"))
            return M3Check.Run(Rest(args));

        if (args.Contains("--m4check"))
            return M4Check.Run(Rest(args));

        if (args.Contains("--m5check"))
            return M5Check.Run(Rest(args));

        if (args.Contains("--updatecheck"))
            return UpdateCheck.Run(args);

        if (args.Contains("--toolbargeom"))
            return M3Check.PrintGeometry();

        // Diagnostic: open the settings window on its own (screenshots, layout checks).
        if (args.Contains("--settings"))
        {
            SettingsStore.Enable();
            ApplicationConfiguration.Initialize();
            using var controller = new OverlayController();
            using var window = new SettingsWindow(controller, _ => true, _ => true, _ => { });
            window.ShowDialog();
            return 0;
        }

        // Preferences persist to %APPDATA%\StickHand\settings.json; load them before anything
        // reads a preference (the overlay controller is created inside AppHost).
        SettingsStore.Enable();

        // One StickHand per session: a second launch (a stickhand:// link, or the user starting
        // the app again) forwards its command to the running instance and exits.
        using var single = new SingleInstance();
        if (!single.TryAcquire())
        {
            string? url = args.FirstOrDefault(arg =>
                arg.StartsWith(Automation.Scheme + "://", StringComparison.OrdinalIgnoreCase));
            if (url is not null) SingleInstance.Send(url);
            return 0;
        }

        string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        if (!Automation.IsProtocolRegistered(exePath))
            Automation.RegisterProtocol(exePath);

        // .NET WinForms bootstrap (source-generated; HighDpiMode comes from
        // ApplicationHighDpiMode in the csproj, the manifest pins PerMonitorV2).
        ApplicationConfiguration.Initialize();

        using var host = new AppHost(single);
        Application.Run(host);
        return 0;
    }

    private static string[] Rest(string[] args) => args.Where(a => !a.StartsWith("--spike") && !a.StartsWith("--hookcheck")).ToArray();

    /// <summary>Attach to the parent console when launched from a terminal (WinExe subsystem).</summary>
    public static void TryAttachConsole()
    {
        AttachConsole(AttachParentProcess);
    }
}
