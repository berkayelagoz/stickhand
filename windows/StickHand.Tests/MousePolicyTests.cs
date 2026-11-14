using StickHand.Logic;

namespace StickHand.Tests;

public class MousePolicyTests
{
    private static MousePolicyResult Decide(
        MouseEventKind kind, bool active, bool clickThrough, bool downSwallowed, bool inToolbar) =>
        MousePolicy.Decide(new MousePolicyInput(kind, active, clickThrough, downSwallowed, inToolbar));

    [Fact]
    public void Inactive_PassesEverything()
    {
        Assert.Equal(MousePolicyResult.Pass, Decide(MouseEventKind.Down, active: false, clickThrough: false, downSwallowed: false, inToolbar: false));
        Assert.Equal(MousePolicyResult.Pass, Decide(MouseEventKind.Up, active: false, clickThrough: false, downSwallowed: true, inToolbar: false));
    }

    [Fact]
    public void DrawingMode_DownOutsideToolbar_IsSwallowed()
    {
        Assert.Equal(MousePolicyResult.Swallow,
            Decide(MouseEventKind.Down, active: true, clickThrough: false, downSwallowed: false, inToolbar: false));
    }

    [Fact]
    public void DrawingMode_DownInToolbar_IsToolbarCommand()
    {
        Assert.Equal(MousePolicyResult.ToolbarCommand,
            Decide(MouseEventKind.Down, active: true, clickThrough: false, downSwallowed: false, inToolbar: true));
    }

    [Fact]
    public void DrawingMode_Wheel_IsSwallowed()
    {
        Assert.Equal(MousePolicyResult.Swallow,
            Decide(MouseEventKind.Wheel, active: true, clickThrough: false, downSwallowed: false, inToolbar: false));
    }

    [Fact]
    public void ClickThroughMode_DownAndWheel_Pass()
    {
        Assert.Equal(MousePolicyResult.Pass,
            Decide(MouseEventKind.Down, active: true, clickThrough: true, downSwallowed: false, inToolbar: false));
        Assert.Equal(MousePolicyResult.Pass,
            Decide(MouseEventKind.Wheel, active: true, clickThrough: true, downSwallowed: false, inToolbar: false));
    }

    [Fact]
    public void Up_FollowsItsDown_EvenWhenModeChangedMidDrag()
    {
        // down swallowed, then mode switched to click-through: up must still be swallowed
        // so down and up land on the same target.
        Assert.Equal(MousePolicyResult.Swallow,
            Decide(MouseEventKind.Up, active: true, clickThrough: true, downSwallowed: true, inToolbar: false));

        // down leaked (click-through down), then mode switched back: up must pass.
        Assert.Equal(MousePolicyResult.Pass,
            Decide(MouseEventKind.Up, active: true, clickThrough: false, downSwallowed: false, inToolbar: false));
    }

    [Fact]
    public void Move_IsNeverSwallowed()
    {
        Assert.Equal(MousePolicyResult.Pass, Decide(MouseEventKind.Move, active: true, clickThrough: false, downSwallowed: false, inToolbar: false));
        Assert.Equal(MousePolicyResult.Pass, Decide(MouseEventKind.Move, active: true, clickThrough: true, downSwallowed: true, inToolbar: false));
    }

    [Fact]
    public void ClickThrough_DownInToolbarRect_DoesNotRouteToolbar()
    {
        // Toolbar routing only exists in drawing mode; click-through clicks go below.
        Assert.Equal(MousePolicyResult.Pass,
            Decide(MouseEventKind.Down, active: true, clickThrough: true, downSwallowed: false, inToolbar: true));
    }

    [Fact]
    public void DragTracker_PairsDownAndUp()
    {
        var tracker = new MouseDragTracker();

        // Swallowed down -> swallowed up
        Assert.Equal(MousePolicyResult.Swallow, tracker.OnDown(MousePolicyResult.Swallow));
        Assert.True(tracker.OnUp());
        Assert.False(tracker.DownSwallowed);

        // Toolbar down -> also swallowed up (its down never reached the app)
        Assert.Equal(MousePolicyResult.Swallow, tracker.OnDown(MousePolicyResult.ToolbarCommand));
        Assert.True(tracker.OnUp());
        Assert.False(tracker.DownSwallowed);

        // Passed down -> passed up
        Assert.Equal(MousePolicyResult.Pass, tracker.OnDown(MousePolicyResult.Pass));
        Assert.False(tracker.OnUp());
        Assert.False(tracker.DownSwallowed);
    }
}
