using TinyHand.Interop;
using TinyHand.Logic;
using System.Drawing;

namespace TinyHand.Tests;

public class ShiftTapDetectorTests
{
    private long _now;

    private ShiftTapDetector NewDetector() => new(() => _now);

    private static void Tap(ShiftTapDetector detector)
    {
        detector.RegisterKey(ShiftTapDetector.VkShift, true);
        detector.RegisterKey(ShiftTapDetector.VkShift, false);
    }

    [Fact]
    public void TwoCleanTapsWithinWindow_TriggerOnce()
    {
        var detector = NewDetector();
        Tap(detector); // tap 1 at t=0
        _now += 200;
        detector.RegisterKey(ShiftTapDetector.VkShift, true);
        Assert.True(detector.RegisterKey(ShiftTapDetector.VkShift, false)); // tap 2 → double
        _now += 100;
        detector.RegisterKey(ShiftTapDetector.VkLeftShift, true);
        Assert.False(detector.RegisterKey(ShiftTapDetector.VkLeftShift, false)); // fresh sequence
    }

    [Fact]
    public void SlowTap_DoesNotCount()
    {
        var detector = NewDetector();
        detector.RegisterKey(ShiftTapDetector.VkShift, true);
        _now += ShiftTapDetector.TapMaxMs + 50; // held too long: typing a capital letter
        Assert.False(detector.RegisterKey(ShiftTapDetector.VkShift, false));
        _now += 100;
        detector.RegisterKey(ShiftTapDetector.VkShift, true);
        _now += 80;
        Assert.False(detector.RegisterKey(ShiftTapDetector.VkShift, false));
    }

    [Fact]
    public void KeyPressedWhileShiftHeld_DisqualifiesTheTap()
    {
        var detector = NewDetector();
        detector.RegisterKey(ShiftTapDetector.VkShift, true);
        _now += 50;
        detector.RegisterKey(0x41, true); // 'A' while Shift is held
        detector.RegisterKey(0x41, false);
        _now += 20;
        Assert.False(detector.RegisterKey(ShiftTapDetector.VkShift, false));
        _now += 100;
        detector.RegisterKey(ShiftTapDetector.VkShift, true);
        _now += 80;
        Assert.False(detector.RegisterKey(ShiftTapDetector.VkShift, false));
    }

    [Fact]
    public void OtherModifierHeld_DisqualifiesTheTap()
    {
        var detector = NewDetector();
        detector.RegisterKey(ShiftTapDetector.VkControl, true);
        detector.RegisterKey(ShiftTapDetector.VkShift, true);
        _now += 80;
        Assert.False(detector.RegisterKey(ShiftTapDetector.VkShift, false));
        detector.RegisterKey(ShiftTapDetector.VkControl, false);
        _now += 100;
        detector.RegisterKey(ShiftTapDetector.VkShift, true);
        _now += 80;
        Assert.False(detector.RegisterKey(ShiftTapDetector.VkShift, false));
    }

    [Fact]
    public void TapsTooFarApart_DoNotTrigger()
    {
        var detector = NewDetector();
        Tap(detector);
        _now += ShiftTapDetector.DoubleMaxMs + 50;
        detector.RegisterKey(ShiftTapDetector.VkShift, true);
        _now += 80;
        Assert.False(detector.RegisterKey(ShiftTapDetector.VkShift, false));
    }

    [Fact]
    public void MouseDownBetweenTaps_DisqualifiesThePair()
    {
        var detector = NewDetector();
        Tap(detector);
        _now += 100;
        detector.RegisterMouseDown();
        _now += 50;
        detector.RegisterKey(ShiftTapDetector.VkShift, true);
        _now += 80;
        Assert.False(detector.RegisterKey(ShiftTapDetector.VkShift, false));
    }
}

public class ToolbarModelTests
{
    [Fact]
    public void Layout_ExposesEveryStylePlusSoundAndExit()
    {
        var layout = ToolbarModel.Build(monitorWidth: 3840, dpi: 96, selectedStyle: "lavender", soundOn: true);
        Assert.Equal(PointerStyle.All.Count + 2, layout.Buttons.Count);
        Assert.Equal(37, layout.Buttons.Count(b => b.Kind == ToolbarButtonKind.Style)); // 36 hands + laser
        Assert.Single(layout.Buttons, b => b.Kind == ToolbarButtonKind.Sound);
        Assert.Single(layout.Buttons, b => b.Kind == ToolbarButtonKind.Exit);
        Assert.Equal("lavender", layout.Buttons[0].Id);
        Assert.Equal("laser", layout.Buttons[^3].Id);
        Assert.Equal("sound", layout.Buttons[^2].Id);
        Assert.Equal("exit", layout.Buttons[^1].Id);
        Assert.True(layout.Buttons[0].Selected);
        Assert.False(layout.Buttons[1].Selected);
    }

    [Fact]
    public void Layout_IsCenteredAndScaled()
    {
        var layout = ToolbarModel.Build(1920, 96, "lavender", true);
        Assert.Equal((1920 - layout.Panel.Width) / 2, layout.Panel.X);
        Assert.Equal(ToolbarModel.TopMarginPx, layout.Panel.Y);
        Assert.Equal(ToolbarModel.ButtonSizePx, layout.Buttons[0].Rect.Width);

        // DPI scaling doubles geometry at 200%.
        var scaled = ToolbarModel.Build(3840, 192, "lavender", true);
        Assert.Equal(2 * ToolbarModel.ButtonSizePx, scaled.Buttons[0].Rect.Width);
        Assert.Equal(2 * ToolbarModel.TopMarginPx, scaled.Panel.Y);
    }

    [Fact]
    public void Layout_WrapsWhenTheMonitorIsNarrow()
    {
        var wide = ToolbarModel.Build(3840, 96, "lavender", true);
        var narrow = ToolbarModel.Build(1280, 96, "lavender", true);
        Assert.True(narrow.Panel.Height > wide.Panel.Height, $"narrow={narrow.Panel.Height} wide={wide.Panel.Height}");
        Assert.True(narrow.Panel.Width <= 1280);
        // Every button stays inside the panel.
        foreach (var button in narrow.Buttons)
            Assert.True(narrow.Panel.Contains(button.Rect), $"{button.Id} outside the panel");
    }

    [Fact]
    public void HitTest_FindsButtonsAndRejectsGaps()
    {
        var layout = ToolbarModel.Build(1920, 96, "lavender", true);
        var first = layout.Buttons[0];

        var hit = ToolbarModel.HitTest(layout, first.Rect.X + 5, first.Rect.Y + 5);
        Assert.NotNull(hit);
        Assert.Equal("lavender", hit!.Id);

        // Gap between buttons: no button, but still inside the panel (swallowed).
        var between = new Point(first.Rect.Right + 2, first.Rect.Y + 5);
        Assert.Null(ToolbarModel.HitTest(layout, between.X, between.Y));
        Assert.True(ToolbarModel.PanelContains(layout, between.X, between.Y));

        // Far away.
        Assert.Null(ToolbarModel.HitTest(layout, 500, 900));
        Assert.False(ToolbarModel.PanelContains(layout, 500, 900));
    }

    [Fact]
    public void SoundLabel_FollowsTheToggle()
    {
        Assert.Equal("Sound on", ToolbarModel.SoundLabel(true));
        Assert.Equal("Sound off", ToolbarModel.SoundLabel(false));
    }
}

public class HookStatsTests
{
    [Fact]
    public void Snapshot_ComputesPercentiles()
    {
        var stats = new HookStats();
        for (int i = 1; i <= 100; i++)
            stats.RecordMs(i / 1000.0);

        var snapshot = stats.TakeSnapshot();
        Assert.Equal(100, snapshot.Count);
        Assert.InRange(snapshot.AverageMs, 0.049, 0.052);
        Assert.True(snapshot.P95Ms >= 0.094, $"p95={snapshot.P95Ms}");
        Assert.True(snapshot.P95Ms <= 0.101, $"p95={snapshot.P95Ms}");
        Assert.InRange(snapshot.MaxMs, 0.099, 0.101);
        Assert.True(snapshot.P50Ms >= snapshot.AverageMs - 0.05);
        Assert.True(snapshot.P50Ms <= snapshot.AverageMs + 0.05);
    }

    [Fact]
    public void Snapshot_EmptyStatsAreZero()
    {
        var snapshot = new HookStats().TakeSnapshot();
        Assert.Equal(0, snapshot.Count);
        Assert.Equal(0, snapshot.AverageMs);
    }
}

public class Win32ContractTests
{
    [Fact]
    public void ExtraInfoSignature_IsStable()
    {
        // The signature byte pattern must match what TinyHand injects via SendInput
        // (contract fixed for both sides, see PLAN.md input mapping).
        Assert.Equal(0x54494E59_48414E44UL, Win32.ExtraInfoSignature);
    }
}
