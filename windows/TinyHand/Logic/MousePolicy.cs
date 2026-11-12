namespace TinyHand.Logic;

public enum MouseEventKind
{
    Move,
    Down,
    Up,
    Wheel,
    Other,
}

public sealed record MousePolicyInput(
    MouseEventKind Kind,
    bool IsActive,
    bool ClickThrough,
    bool DownSwallowedByUs,
    bool InToolbar);

public enum MousePolicyResult
{
    /// <summary>Event reaches the window under the cursor.</summary>
    Pass,

    /// <summary>Hook swallows the event; no window message is produced.</summary>
    Swallow,

    /// <summary>Hook swallows the event and routes the action as a toolbar command.</summary>
    ToolbarCommand,
}

/// <summary>
/// Pure decision table for the low-level mouse hook. Mirrors the PLAN.md interaction
/// matrix:
/// - Drawing (active, not click-through): clicks are swallowed, commands for the
///   toolbar region are routed from inside the hook.
/// - Click-through: everything passes; the overlay window additionally carries
///   WS_EX_TRANSPARENT so hit-testing never targets it.
/// - Up events follow their down event: up is swallowed only if TinyHand swallowed
///   its down, so down/up always land on the same window even when the mode changes
///   while the button is held.
/// </summary>
public static class MousePolicy
{
    public static MousePolicyResult Decide(in MousePolicyInput input)
    {
        if (!input.IsActive)
            return MousePolicyResult.Pass;

        switch (input.Kind)
        {
            case MouseEventKind.Up:
                return input.DownSwallowedByUs
                    ? MousePolicyResult.Swallow
                    : MousePolicyResult.Pass;

            case MouseEventKind.Down:
                if (input.ClickThrough)
                    return MousePolicyResult.Pass;
                return input.InToolbar
                    ? MousePolicyResult.ToolbarCommand
                    : MousePolicyResult.Swallow;

            case MouseEventKind.Wheel:
                return input.ClickThrough ? MousePolicyResult.Pass : MousePolicyResult.Swallow;

            default:
                // Move is never swallowed: the real cursor must keep moving and the
                // overlay's WM_SETCURSOR blank-cursor flow depends on delivered moves.
                return MousePolicyResult.Pass;
        }
    }
}

/// <summary>
/// Tracks whether the current button-down was swallowed by TinyHand, so the matching
/// up event is routed to the same "window" (see PLAN.md: down/up consistency when the
/// mode changes mid-drag). Owned by the hook thread.
/// </summary>
public sealed class MouseDragTracker
{
    private bool _downSwallowed;

    public bool DownSwallowed => _downSwallowed;

    /// <summary>Call for every down event. Returns the decision to apply.</summary>
    public MousePolicyResult OnDown(MousePolicyResult downDecision)
    {
        _downSwallowed = downDecision is MousePolicyResult.Swallow or MousePolicyResult.ToolbarCommand;
        return _downSwallowed ? MousePolicyResult.Swallow : MousePolicyResult.Pass;
    }

    /// <summary>Call for every up event. Clears the state.</summary>
    public bool OnUp()
    {
        bool swallowed = _downSwallowed;
        _downSwallowed = false;
        return swallowed;
    }

    public void Reset() => _downSwallowed = false;
}
