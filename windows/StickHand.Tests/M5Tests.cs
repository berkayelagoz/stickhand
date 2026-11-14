using System.Drawing;
using System.Text.Json;
using StickHand.Interop;
using StickHand.Logic;
using StickHand.Overlay;

namespace StickHand.Tests;

/// <summary>
/// M5 distribution contracts: one version for both apps, the Velopack channel and package
/// names the release script and the updater share, and the DPI scale math the multi-monitor
/// pass relies on.
/// </summary>
public class DistributionTests
{
    /// <summary>Walk up from the test binaries to the repository root (the folder with VERSION).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Version_ComesFromTheRepositoryVersionFile()
    {
        string expected = File.ReadAllText(Path.Combine(RepoRoot(), "VERSION")).Trim();
        Assert.Equal(expected, Updater.Version);
    }

    [Fact]
    public void InstallerConfig_MatchesTheUpdater()
    {
        string path = Path.Combine(RepoRoot(), "windows", "installer", "velopack.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal("StickHand", root.GetProperty("packId").GetString());
        Assert.Equal("StickHand.exe", root.GetProperty("mainExe").GetString());
        Assert.Equal(Updater.Channel, root.GetProperty("channel").GetString());
        Assert.Equal("win-x64", root.GetProperty("runtime").GetString());
        Assert.Equal("StickHand-WindowsSetup.exe", root.GetProperty("setupName").GetString());
        Assert.Equal("StickHand-Windows-portable.zip", root.GetProperty("portableName").GetString());
    }

    [Fact]
    public void ReleaseScript_PacksTheChannelFeed()
    {
        string path = Path.Combine(RepoRoot(), "windows", "scripts", "release-windows.ps1");
        Assert.True(File.Exists(path), path);
        string script = File.ReadAllText(path);
        Assert.Contains("vpk", script);
        Assert.Contains("--channel", script);
        Assert.Contains("--delta", script);
        Assert.Contains("velopack.json", script);
    }

    [Fact]
    public void ReleaseWorkflow_OpensTheReleaseWithTheWindowsAssets()
    {
        string path = Path.Combine(RepoRoot(), ".github", "workflows", "release.yml");
        string workflow = File.ReadAllText(path);
        Assert.Contains("release-windows", workflow);
        // M6: the Windows job is the only release job, so it opens the release itself.
        Assert.DoesNotContain("needs: release", workflow);
        Assert.Contains("gh release create", workflow);
        Assert.Contains("releases.win.json", workflow);
        Assert.Contains("StickHand-WindowsSetup.exe", workflow);
    }

    [Fact]
    public void Workflows_AreWindowsOnly()
    {
        // M6: the macOS app is gone, so no job may run on a macOS runner.
        foreach (string name in new[] { "build.yml", "release.yml" })
        {
            string path = Path.Combine(RepoRoot(), ".github", "workflows", name);
            string workflow = File.ReadAllText(path);
            Assert.DoesNotContain("macos-latest", workflow);
            Assert.DoesNotContain("swift", workflow);
            Assert.Contains("windows-latest", workflow);
        }
    }
}

/// <summary>DPI scale math for the 100/125/150/175/200 % pass.</summary>
public class DpiScaleTests
{
    [Theory]
    [InlineData(96, 1)]
    [InlineData(120, 1)]
    [InlineData(144, 2)]
    [InlineData(168, 2)]
    [InlineData(192, 2)]
    public void Scale_IsTheRoundedDpiOver96(int dpi, int expected) =>
        Assert.Equal(expected, ToolbarModel.Scale(dpi));

    [Theory]
    [InlineData(1920, 96)]
    [InlineData(1920, 144)]
    [InlineData(2560, 144)]
    [InlineData(3840, 192)]
    public void Toolbar_StaysInsideTheMonitor(int width, int dpi)
    {
        var layout = ToolbarModel.Build(width, dpi, PointerStyle.Default.Id, true);
        Assert.True(layout.Panel.X >= 0, $"{layout.Panel}");
        Assert.True(layout.Panel.Right <= width, $"{layout.Panel} > {width}");
        Assert.All(layout.Buttons, button =>
        {
            Assert.True(button.Rect.X >= layout.Panel.X);
            Assert.True(button.Rect.Right <= layout.Panel.Right);
            Assert.True(button.Rect.Bottom <= layout.Panel.Bottom);
        });
    }

    [Fact]
    public void InkWidth_UsesTheScaledMinimum()
    {
        // 1080p: height × 0.0055 = 5.94 px wins at 100 %; the 3 pt minimum × 2 wins at 150 %.
        Assert.Equal(5.94f, InkMetrics.Width(1080, 1), 3);
        Assert.Equal(6f, InkMetrics.Width(1080, 2), 3);
        Assert.Equal(11.88f, InkMetrics.Width(2160, 1), 3);
    }

    [Fact]
    public void HudRect_FitsA150PercentMonitor()
    {
        var monitor = new MonitorInfo(IntPtr.Zero, new Rectangle(0, 0, 1920, 1080), true, 144, 144);
        var rect = HandScene.HudRect(monitor, ToolbarModel.Scale(144));
        Assert.True(rect.X >= 0);
        Assert.True(rect.Right <= monitor.Bounds.Width);
        Assert.True(rect.Y >= 0);
    }
}

/// <summary>Multi-monitor pointer placement (M5 pass).</summary>
public class MultiMonitorTests
{
    private static readonly Rectangle Primary = new(0, 0, 3840, 2160);

    [Fact]
    public void Pointer_IsDrawnOnlyOnTheCursorMonitor()
    {
        var right = new Rectangle(3840, 496, 1920, 1080);
        var left = new Rectangle(-1920, 0, 1920, 1080);
        var above = new Rectangle(0, -1080, 1920, 1080);

        Assert.True(HandScene.DrawsPointerOn(Primary, new Point(100, 100)));
        Assert.False(HandScene.DrawsPointerOn(right, new Point(100, 100)));
        Assert.False(HandScene.DrawsPointerOn(left, new Point(100, 100)));
        Assert.False(HandScene.DrawsPointerOn(above, new Point(100, 100)));

        Assert.True(HandScene.DrawsPointerOn(right, new Point(4000, 800)));
        Assert.True(HandScene.DrawsPointerOn(left, new Point(-1000, 500)));
        Assert.True(HandScene.DrawsPointerOn(above, new Point(900, -500)));
    }

    [Fact]
    public void GripTarget_StaysOnTheCursorMonitor()
    {
        // A monitor to the left of the primary: the grip lean must be measured against that
        // monitor, not the primary (the pre-M5 code clamped the grip to the primary's edge).
        var left = new Rectangle(-1920, 0, 1920, 1080);
        float grip = HandPoseSolver.GripTargetX(-1000, left);
        Assert.InRange(grip, left.X + 0.15f * left.Width, left.X + 1.05f * left.Width);
        Assert.True(grip < 0, $"grip {grip} should be on the left monitor");
    }

    [Fact]
    public void GripTarget_LeansRightOfTheCursor()
    {
        float grip = HandPoseSolver.GripTargetX(1000, Primary);
        Assert.True(grip > 1000, $"{grip}");
        Assert.InRange(grip, Primary.X + 0.15f * Primary.Width, Primary.X + 1.05f * Primary.Width);
    }
}
