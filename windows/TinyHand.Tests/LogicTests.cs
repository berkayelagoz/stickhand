using TinyHand.Interop;
using TinyHand.Logic;
using System.Drawing;

namespace TinyHand.Tests;

public class DoublePressDetectorTests
{
    private long _now;

    private DoublePressDetector NewDetector() => new(() => _now);

    [Fact]
    public void TwoShiftPressesWithinWindow_TriggersOnce()
    {
        var detector = NewDetector();
        Assert.False(detector.RegisterKeyDown(0x10));
        _now += 200;
        Assert.True(detector.RegisterKeyDown(0xA0)); // second press (right shift) inside window
    }

    [Fact]
    public void TwoShiftPressesOutsideWindow_DoNotTrigger()
    {
        var detector = NewDetector();
        Assert.False(detector.RegisterKeyDown(0x10));
        _now += 400; // > 350ms window
        Assert.False(detector.RegisterKeyDown(0x10));
    }

    [Fact]
    public void NonShiftKeyPressBetweenShifts_Resets()
    {
        var detector = NewDetector();
        Assert.False(detector.RegisterKeyDown(0x10));
        _now += 100;
        Assert.False(detector.RegisterKeyDown(0x41)); // 'A'
        _now += 100;
        Assert.False(detector.RegisterKeyDown(0x10));
    }

    [Fact]
    public void TriplePress_TriggersOnEachExactPair()
    {
        var detector = NewDetector();
        Assert.False(detector.RegisterKeyDown(0x10));
        _now += 100;
        Assert.True(detector.RegisterKeyDown(0x10));
        _now += 100;
        Assert.False(detector.RegisterKeyDown(0x10)); // fresh sequence after a trigger
        _now += 100;
        Assert.True(detector.RegisterKeyDown(0x10));
    }
}

public class ToolbarModelTests
{
    [Fact]
    public void Layout_IsCenteredAndScaled()
    {
        var buttons = ToolbarModel.Build(monitorWidth: 1920, dpi: 96, clickThrough: false);
        Assert.Equal(2, buttons.Count);

        int totalWidth = buttons.Sum(b => b.Rect.Width) + ToolbarModel.GapPx;
        int left = buttons[0].Rect.X;
        Assert.Equal((1920 - totalWidth) / 2, left);
        Assert.Equal(ToolbarModel.TopMarginPx, buttons[0].Rect.Y);

        // DPI scaling doubles geometry at 200%.
        var scaled = ToolbarModel.Build(3840, 192, true);
        Assert.Equal(2 * ToolbarModel.ButtonHeightPx, scaled[0].Rect.Height);
        Assert.Equal("Click-through: ON", scaled[0].Label);
        Assert.Equal("Click-through: OFF", buttons[0].Label);
    }

    [Fact]
    public void HitTest_FindsButtonsAndRejectsGaps()
    {
        var buttons = ToolbarModel.Build(1920, 96, false);
        var toggle = buttons[0];

        var hit = ToolbarModel.HitTest(buttons, toggle.Rect.X + 5, toggle.Rect.Y + 5);
        Assert.NotNull(hit);
        Assert.Equal("click_through", hit!.Id);

        // Gap between buttons.
        var between = new Point(buttons[0].Rect.Right + 4, toggle.Rect.Y + 5);
        Assert.Null(ToolbarModel.HitTest(buttons, between.X, between.Y));

        // Far away.
        Assert.Null(ToolbarModel.HitTest(buttons, 500, 900));
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
