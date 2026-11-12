using System.Runtime.InteropServices;
using TinyHand.Interop;

namespace TinyHand;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--spike"))
            return SpikeRunner.Run(Rest(args));

        if (args.Contains("--hookcheck"))
            return HookCheck.Run(Rest(args));

        // .NET WinForms bootstrap (source-generated; HighDpiMode comes from
        // ApplicationHighDpiMode in the csproj, the manifest pins PerMonitorV2).
        ApplicationConfiguration.Initialize();

        using var host = new AppHost();
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
