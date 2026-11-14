using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace StickHand.Tests;

/// <summary>
/// Test isolation: preferences and custom hands are process-wide statics, so the suite runs
/// serially and points both stores at a throwaway directory instead of the real profile.
/// </summary>
internal static class TestSetup
{
    public static readonly string Root = Path.Combine(
        Path.GetTempPath(), "stickhand-tests-" + Guid.NewGuid().ToString("N")[..8]);

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("TH_SETTINGS_DIR", Root);
        Environment.SetEnvironmentVariable("TH_HANDS_DIR", Path.Combine(Root, "Hands"));
    }
}
