using System.Drawing;
using System.Numerics;

namespace TinyHand.Logic;

/// <summary>Solved arm pose for one frame (screen coordinates, y down).</summary>
public sealed record HandPose(
    PointF Grip,
    float Rotation,
    float RodLength,
    float HandLength,
    float Width,
    float Lift,
    float ShadowDx,
    float ShadowDy,
    float ShadowRadius,
    float ShadowOpacity,
    PointF Tip)
{
    /// <summary>
    /// Sprite-space → screen-space transform for this pose. The sprite is drawn upright (its
    /// top row is the fingertip) with the hand part at sprite y ∈ [0, HandLength] and the stem
    /// below it; the grip is the rotation origin (macOS <c>group.anchorPoint = (rodX, 0)</c>).
    /// </summary>
    public Matrix3x2 SpriteTransform(HandMetrics metrics, float offsetX = 0, float offsetY = 0) =>
        Matrix3x2.CreateTranslation(-metrics.RodX * Width, -(RodLength + HandLength))
        * Matrix3x2.CreateRotation(Rotation)
        * Matrix3x2.CreateTranslation(Grip.X + offsetX, Grip.Y + offsetY);

    /// <summary>Sprite-space y of the measured fingertip (top of the hand part).</summary>
    public static float SpriteTipY(HandMetrics metrics, float handLength) =>
        (metrics.Tip - metrics.HandTop) / metrics.HandSpan * handLength;
}

/// <summary>
/// macOS <c>ArmView.aim</c> port: the arm hangs from a grip point below the screen edge and
/// reaches for the cursor, so the fingertip lands exactly on the pointer. The hand is split
/// into the stem (rod) and the hand part of the same sprite; the stem stretches with the
/// distance, the hand keeps its length (minus the press shrink).
///
/// Pure math, unit-tested: the tip-on-cursor invariant, the press shrink, the lift/shadow
/// factors and the laser radius all come from the macOS constants.
/// </summary>
public static class HandPoseSolver
{
    /// <summary><c>ArmView.minRodFrac</c>: the stem never gets shorter than 5 % of the screen.</summary>
    public const float MinRodFrac = 0.05f;

    /// <summary><c>ArmView.gripLeanFrac</c>: the grip sits 30 % of the screen width right of the cursor.</summary>
    public const float GripLeanFrac = 0.30f;

    /// <summary><c>ArmView.gripDepthFrac</c>: the grip sits 12 % of the screen height below the bottom edge.</summary>
    public const float GripDepthFrac = 0.12f;

    /// <summary><c>ArmView.gripFollow</c>: per-frame grip smoothing (0.08).</summary>
    public const float GripFollow = 0.08f;

    /// <summary><c>ArmView.liftFrac</c>: the hand floats 6 % of the screen height above the surface.</summary>
    public const float LiftFrac = 0.06f;

    /// <summary><c>ArmView.press()</c>: the hand shortens by 6 % at full press.</summary>
    public const float PressHandShrink = 0.06f;

    /// <summary><c>ArmView.currentLift</c>: the lift drops by 80 % at full press.</summary>
    public const float PressLiftShrink = 0.8f;

    /// <summary><c>ArmView.aim</c>: <c>pressAmount += (0 − pressAmount) × 0.18</c> per frame.</summary>
    public const float PressReleaseFollow = 0.18f;

    /// <summary>Grip x target for a cursor position (macOS clamps to 15 %..105 % of the width).</summary>
    public static float GripTargetX(float cursorX, Rectangle monitor)
    {
        float w = monitor.Width;
        float local = cursorX - monitor.X;
        float target = Math.Clamp(local + GripLeanFrac * w, 0.15f * w, 1.05f * w);
        return monitor.X + target;
    }

    /// <summary>Advance the smoothed grip x one frame toward its target.</summary>
    public static float FollowGrip(float gripX, float targetX) =>
        gripX + (targetX - gripX) * GripFollow;

    /// <summary>Advance the press amount one frame while the button is released.</summary>
    public static float ReleasePress(float press) => press + (0 - press) * PressReleaseFollow;

    public static HandPose Solve(PointF cursor, Rectangle monitor, HandMetrics metrics,
        float press, float gripX, bool showShadow)
    {
        float screenH = monitor.Height;
        float handLen = Preferences.PointerSize * screenH * (1 - PressHandShrink * press);
        float s = handLen / metrics.HandSpan;
        float width = s * metrics.Aspect;
        float handTip = (metrics.Wrist - metrics.Tip) * s;
        float tipOffX = (metrics.TipX - metrics.RodX) * width;

        float gripY = monitor.Y + screenH * (1 + GripDepthFrac);
        float dx = cursor.X - gripX;
        float dy = cursor.Y - gripY;
        float reach = MathF.Sqrt(dx * dx + dy * dy);
        float along = MathF.Sqrt(MathF.Max(0, reach * reach - tipOffX * tipOffX));
        float rodLen = MathF.Max(MinRodFrac * screenH, along - handTip);

        // Rotation that puts the tip on the ray from the grip through the cursor. The local
        // frame is y-down with the arm extending toward −y (the sprite is drawn upright: its
        // top row is the fingertip), so the tip sits at (tipOffX, −(rodLen + handTip)) and the
        // rotation adds the tip's own angle. Angles use atan2(y, x) in screen space (y down),
        // so a positive rotation turns clockwise — the same sense as Matrix3x2.CreateRotation.
        float tipAngle = MathF.Atan2(rodLen + handTip, tipOffX);
        float rotation = MathF.Atan2(dy, dx) + tipAngle;

        float lift = LiftFrac * screenH * (1 - PressLiftShrink * press);
        // macOS pre-rotates the CALayer shadow offset by −rotation, which cancels the layer
        // rotation: the shadow keeps a fixed screen-space direction (down-right).
        float shadowDx = lift * 0.6f;
        float shadowDy = lift;
        float shadowRadius = MathF.Max(1, lift * 0.35f) + 5 * press;
        float shadowOpacity = showShadow ? 0.26f + 0.02f * press : 0;

        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);
        float far = rodLen + handTip;
        var tip = new PointF(
            gripX + tipOffX * cos + far * sin,
            gripY + tipOffX * sin - far * cos);

        return new HandPose(
            Grip: new PointF(gripX, gripY),
            Rotation: rotation,
            RodLength: rodLen,
            HandLength: handLen,
            Width: width,
            Lift: lift,
            ShadowDx: shadowDx,
            ShadowDy: shadowDy,
            ShadowRadius: shadowRadius,
            ShadowOpacity: shadowOpacity,
            Tip: tip);
    }

    /// <summary><c>ArmView.aimLaser</c>: <c>max(4, screenH × 0.0038 × size/0.13) × (1 + 0.2 × press)</c>.</summary>
    public static float LaserRadius(float screenH, float press) =>
        MathF.Max(4, screenH * 0.0038f * Preferences.PointerSize / 0.13f) * (1 + 0.2f * press);

    /// <summary><c>ArmView.aimLaser</c>: the glow radius is <c>r × (2.2 + press)</c>.</summary>
    public static float LaserGlowRadius(float radius, float press) => radius * (2.2f + press);
}
