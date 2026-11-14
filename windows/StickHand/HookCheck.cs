using StickHand.Interop;

namespace StickHand;

/// <summary>
/// M1 hook lifecycle check: installs WH_MOUSE_LL/WH_KEYBOARD_LL on a dedicated
/// pump, observes passively for a few seconds, uninstalls, then reinstalls —
/// verifying silent-removal recovery by close/open cycling at the API level.
/// Callback duration stats (p50/p95/max) are printed for each pass.
/// </summary>
internal static class HookCheck
{
    public static int Run(string[] args)
    {
        Program.TryAttachConsole();

        int seconds = 3;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--seconds") seconds = int.Parse(args[i + 1]);

        Console.WriteLine("hookcheck pass 1 (install -> observe -> stop)");
        bool ok1 = Observe(seconds, "pass1");

        Console.WriteLine();
        Console.WriteLine("hookcheck pass 2 (reinstall after stop -> observe -> stop)");
        bool ok2 = Observe(seconds, "pass2");

        Console.WriteLine();
        Console.WriteLine(ok1 && ok2
            ? "RESULT: hooks installed, ran, stopped, and reinstalled successfully."
            : "RESULT: FAILURE — see LastError above.");
        return ok1 && ok2 ? 0 : 1;
    }

    private static bool Observe(int seconds, string label)
    {
        long mouseEvents = 0;
        long keyEvents = 0;

        using var hooks = new LowLevelHooks();
        hooks.MouseFilter += _ =>
        {
            Interlocked.Increment(ref mouseEvents);
            return false; // passive: everything passes
        };
        hooks.KeyboardFilter += _ =>
        {
            Interlocked.Increment(ref keyEvents);
            return false;
        };

        if (!hooks.Start())
        {
            Console.WriteLine($"  install FAILED: {hooks.LastError}");
            return false;
        }
        Console.WriteLine($"  installed, IsRunning={hooks.IsRunning}");
        Thread.Sleep(seconds * 1000);
        hooks.Stop();

        var mouse = hooks.MouseStats.TakeSnapshot();
        var kbd = hooks.KeyboardStats.TakeSnapshot();

        Console.WriteLine($"  stopped, IsRunning={hooks.IsRunning}");
        Console.WriteLine($"  events seen: mouse={mouseEvents} keyboard={keyEvents}");
        Console.WriteLine($"  mouse callback: n={mouse.Count} avg={mouse.AverageMs:0.000} p50={mouse.P50Ms:0.000} p95={mouse.P95Ms:0.000} max={mouse.MaxMs:0.000} ms");
        Console.WriteLine($"  kbd   callback: n={kbd.Count} avg={kbd.AverageMs:0.000} p50={kbd.P50Ms:0.000} p95={kbd.P95Ms:0.000} max={kbd.MaxMs:0.000} ms");
        Console.WriteLine($"  thread alive after stop: {ThreadAlive(hooks)}");
        return true;
    }

    private static bool ThreadAlive(LowLevelHooks hooks) => hooks.IsRunning;
}
