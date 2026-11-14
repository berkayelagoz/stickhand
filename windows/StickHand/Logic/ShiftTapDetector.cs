namespace StickHand.Logic;

/// <summary>
/// macOS <c>OverlayController.shiftChanged</c> port: a Shift *tap* (pressed and released
/// within 0.3 s with no other input in between) counts, and two taps within 0.35 s latch
/// click-through. Counting inputs instead of inspecting keys is what tells a clean tap from
/// typing a capital letter — the macOS reference does the same with
/// <c>CGEventSource.counterForEventType</c>.
///
/// Pure logic, clock injected for tests. Owned by the hook thread.
/// </summary>
public sealed class ShiftTapDetector(Func<long> clockMs)
{
    /// <summary>macOS <c>shiftTapMax</c>: a tap must be shorter than this.</summary>
    public const int TapMaxMs = 300;

    /// <summary>macOS <c>shiftDoubleMax</c>: the second tap must start within this window.</summary>
    public const int DoubleMaxMs = 350;

    public const uint VkShift = 0x10;
    public const uint VkLeftShift = 0xA0;
    public const uint VkRightShift = 0xA1;
    public const uint VkControl = 0x11;
    public const uint VkLeftControl = 0xA2;
    public const uint VkRightControl = 0xA3;
    public const uint VkMenu = 0x12;
    public const uint VkLeftMenu = 0xA4;
    public const uint VkRightMenu = 0xA5;
    public const uint VkLeftWin = 0x5B;
    public const uint VkRightWin = 0x5C;

    private readonly Func<long> _clockMs = clockMs;
    private long _downAt = -1;
    private long _downInputs;
    private long _lastTapAt = long.MinValue;
    private long _lastTapInputs;
    private long _inputs;
    private bool _shiftDown;
    private int _otherModifiers;

    /// <summary>Inputs (other keys, mouse downs) seen so far; a tap is clean only when this
    /// did not change while Shift was held.</summary>
    public long Inputs => _inputs;

    public bool ShiftDown => _shiftDown;

    /// <summary>Feed a keyboard event. Returns true exactly once per double tap.</summary>
    public bool RegisterKey(uint vk, bool down)
    {
        if (IsShift(vk))
        {
            if (down)
            {
                if (_shiftDown) return false;
                _shiftDown = true;
                _downAt = _clockMs();
                _downInputs = _inputs;
                return false;
            }

            if (!_shiftDown) return false;
            _shiftDown = false;
            long downAt = _downAt;
            _downAt = -1;
            if (downAt < 0) return false;

            long now = _clockMs();
            bool clean = now - downAt < TapMaxMs && _inputs == _downInputs && _otherModifiers == 0;
            if (!clean)
            {
                _lastTapAt = long.MinValue;
                return false;
            }

            bool isDouble = _lastTapAt != long.MinValue
                && downAt - _lastTapAt < DoubleMaxMs
                && _lastTapInputs == _downInputs;
            if (isDouble)
            {
                _lastTapAt = long.MinValue;
                return true;
            }

            _lastTapAt = now;
            _lastTapInputs = _inputs;
            return false;
        }

        if (IsModifier(vk))
        {
            if (down) _otherModifiers++;
            else _otherModifiers = Math.Max(0, _otherModifiers - 1);
            return false;
        }

        if (down) _inputs++;
        return false;
    }

    /// <summary>Mouse downs count as input too (macOS <c>inputCount</c> includes them).</summary>
    public void RegisterMouseDown() => _inputs++;

    public void Reset()
    {
        _downAt = -1;
        _lastTapAt = long.MinValue;
        _shiftDown = false;
        _otherModifiers = 0;
    }

    private static bool IsShift(uint vk) => vk is VkShift or VkLeftShift or VkRightShift;

    private static bool IsModifier(uint vk) => vk is VkControl or VkLeftControl or VkRightControl
        or VkMenu or VkLeftMenu or VkRightMenu or VkLeftWin or VkRightWin;
}
