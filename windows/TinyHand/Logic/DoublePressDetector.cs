namespace TinyHand.Logic;

/// <summary>
/// Detects a double press of the Shift key within a time window (macOS reference:
/// InputTap.shiftDoubleMax == 0.35s). Any other keydown resets the pending state.
/// Pure logic, clock injected for tests.
/// </summary>
public sealed class DoublePressDetector(Func<long> clockMs)
{
    public const int DefaultWindowMs = 350;
    public const uint ShiftVk = 0x10;

    private static readonly uint[] ShiftVks = [ShiftVk, 0xA0 /* VK_LSHIFT */, 0xA1 /* VK_RSHIFT */];

    private readonly Func<long> _clockMs = clockMs;
    private long _lastDown = long.MinValue;

    public bool Armed => _lastDown != long.MinValue;

    /// <summary>Feed keydown events. Returns true exactly once per double press.</summary>
    public bool RegisterKeyDown(uint vk, int windowMs = DefaultWindowMs)
    {
        if (ShiftVks.Contains(vk))
        {
            long now = _clockMs();
            if (Armed && now - _lastDown <= windowMs)
            {
                _lastDown = long.MinValue;
                return true;
            }
            _lastDown = now;
            return false;
        }

        _lastDown = long.MinValue;
        return false;
    }

    public void Reset() => _lastDown = long.MinValue;
}
