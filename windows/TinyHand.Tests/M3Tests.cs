using System.Drawing;
using System.Numerics;
using System.Drawing.Imaging;
using TinyHand.Logic;

namespace TinyHand.Tests;

public class HandPoseTests
{
    private static readonly Rectangle Monitor = new(0, 0, 3840, 2160);

    // Synthetic hand profile: narrow finger, wide fist, narrow stem (100×200).
    private static readonly HandMetrics Metrics = new(
        Aspect: 0.5f, HandTop: 0.046f, Tip: 0.05f, Wrist: 0.5f, RodEnd: 0.945f,
        TipX: 0.5f, RodX: 0.5f, HandMinX: 0.2f, HandMaxX: 0.8f);

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(600, 300)]
    [InlineData(3700, 2000)]
    [InlineData(100, 2100)]
    public void Tip_LandsOnTheCursor(float x, float y)
    {
        var cursor = new PointF(x, y);
        float gripX = HandPoseSolver.GripTargetX(x, Monitor);
        var pose = HandPoseSolver.Solve(cursor, Monitor, Metrics, press: 0, gripX, showShadow: true);
        Assert.Equal(cursor.X, pose.Tip.X, 1);
        Assert.Equal(cursor.Y, pose.Tip.Y, 1);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(600, 300)]
    [InlineData(3700, 2000)]
    public void SpriteTip_MapsToTheCursor(float x, float y)
    {
        // The scene draws the sprite through SpriteTransform; the measured fingertip (top of
        // the hand part) must land exactly on the cursor, with the hand upright above the stem.
        var cursor = new PointF(x, y);
        float gripX = HandPoseSolver.GripTargetX(x, Monitor);
        var pose = HandPoseSolver.Solve(cursor, Monitor, Metrics, 0, gripX, true);
        var spriteTip = new Vector2(Metrics.TipX * pose.Width,
            HandPose.SpriteTipY(Metrics, pose.HandLength));
        var mapped = Vector2.Transform(spriteTip, pose.SpriteTransform(Metrics));
        Assert.Equal(cursor.X, mapped.X, 1);
        Assert.Equal(cursor.Y, mapped.Y, 1);
    }

    [Fact]
    public void SpriteLayout_PutsTheFingertipAtTheTopOfTheHandPart()
    {
        // The hand part occupies sprite y ∈ [0, HandLength] and the stem below it; the measured
        // tip sits just inside the hand part's top edge (handTop is 0.4 % above the tip).
        Assert.InRange(HandPose.SpriteTipY(Metrics, 100), 0f, 5f);
    }

    [Fact]
    public void Grip_SitsBelowTheScreenEdgeAndLeansRight()
    {
        float gripX = HandPoseSolver.GripTargetX(1920, Monitor);
        Assert.Equal(1920 + 0.30f * 3840, gripX, 1);
        var pose = HandPoseSolver.Solve(new PointF(1920, 1080), Monitor, Metrics, 0, gripX, true);
        Assert.Equal(2160 * 1.12f, pose.Grip.Y, 1);
    }

    [Fact]
    public void GripTarget_ClampsToTheMacOSRange()
    {
        // The 30 % lean already clears the lower bound on-screen; it only bites when the
        // cursor is left of the monitor (multi-monitor setups).
        Assert.Equal(0.15f * 3840, HandPoseSolver.GripTargetX(-1000, Monitor), 1);
        Assert.Equal(1.05f * 3840, HandPoseSolver.GripTargetX(3839, Monitor), 1);
    }

    [Fact]
    public void Press_ShrinksTheHandAndDropsTheLift()
    {
        var cursor = new PointF(1920, 1080);
        float gripX = HandPoseSolver.GripTargetX(1920, Monitor);
        var rest = HandPoseSolver.Solve(cursor, Monitor, Metrics, 0, gripX, true);
        var pressed = HandPoseSolver.Solve(cursor, Monitor, Metrics, 1, gripX, true);

        Assert.Equal(0.13f * 2160, rest.HandLength, 1);
        Assert.Equal(0.13f * 2160 * 0.94f, pressed.HandLength, 1);
        Assert.Equal(0.06f * 2160, rest.Lift, 1);
        Assert.Equal(0.06f * 2160 * 0.2f, pressed.Lift, 1);
        Assert.True(pressed.Width < rest.Width);
    }

    [Fact]
    public void Shadow_FollowsLiftAndPress()
    {
        var cursor = new PointF(1920, 1080);
        float gripX = HandPoseSolver.GripTargetX(1920, Monitor);
        var rest = HandPoseSolver.Solve(cursor, Monitor, Metrics, 0, gripX, true);
        var pressed = HandPoseSolver.Solve(cursor, Monitor, Metrics, 1, gripX, true);

        // macOS: opacity 0.26 + 0.02 × press, radius max(1, lift × 0.35) + 5 × press.
        Assert.Equal(0.26f, rest.ShadowOpacity, 3);
        Assert.Equal(0.28f, pressed.ShadowOpacity, 3);
        Assert.Equal(0.06f * 2160 * 0.35f, rest.ShadowRadius, 1);
        Assert.Equal(0.06f * 2160 * 0.2f * 0.35f + 5, pressed.ShadowRadius, 1);
        // Offset is a fixed screen-space direction (down-right), scaled by the lift.
        Assert.Equal(rest.Lift * 0.6f, rest.ShadowDx, 1);
        Assert.Equal(rest.Lift, rest.ShadowDy, 1);
        Assert.True(pressed.ShadowDy < rest.ShadowDy);
    }

    [Fact]
    public void Shadow_CanBeDisabled()
    {
        var pose = HandPoseSolver.Solve(new PointF(1920, 1080), Monitor, Metrics, 0,
            HandPoseSolver.GripTargetX(1920, Monitor), showShadow: false);
        Assert.Equal(0f, pose.ShadowOpacity);
    }

    [Fact]
    public void Rod_NeverShorterThanTheMinimum()
    {
        // Cursor right above the grip: the stem would collapse, the macOS floor keeps it.
        float gripX = HandPoseSolver.GripTargetX(1920, Monitor);
        var pose = HandPoseSolver.Solve(new PointF(gripX, 2160 * 1.1f), Monitor, Metrics, 0, gripX, true);
        Assert.Equal(HandPoseSolver.MinRodFrac * 2160, pose.RodLength, 1);
    }

    [Fact]
    public void GripFollow_Is25PercentFasterThanTheMacOSFactor()
    {
        // The macOS factor is per 120 Hz frame; the Windows tuning multiplies the rate by 1.25.
        float at120 = 1 - MathF.Pow(1 - HandPoseSolver.GripFollow, HandPoseSolver.GripFollowSpeedup);
        Assert.Equal(100 + (200 - 100) * at120, HandPoseSolver.FollowGrip(100, 200, 1f / 120f), 3);
        // One 60 Hz frame: two and a half 120 Hz steps, so the wall-clock response is the same.
        Assert.Equal(100 + (200 - 100) * (1 - MathF.Pow(1 - HandPoseSolver.GripFollow, 2.5f)),
            HandPoseSolver.FollowGrip(100, 200, 1f / 60f), 3);
        // 25 % faster than the raw macOS factor at the same cadence.
        Assert.True(at120 > 0.08f * 1.2f, $"at120={at120}");
        Assert.Equal(1f - 0.18f, HandPoseSolver.ReleasePress(1f), 3);
        Assert.Equal(0f, HandPoseSolver.ReleasePress(0f), 3);
    }

    [Fact]
    public void GripFollow_IsFrameRateIndependent()
    {
        // 0.1 s of smoothing at 60 Hz and at 120 Hz must land on the same grip position.
        float at60 = 100;
        for (int i = 0; i < 6; i++) at60 = HandPoseSolver.FollowGrip(at60, 200, 1f / 60f);
        float at120 = 100;
        for (int i = 0; i < 12; i++) at120 = HandPoseSolver.FollowGrip(at120, 200, 1f / 120f);
        Assert.Equal(at120, at60, 1);
        // And it converges faster than the old fixed per-frame factor at 60 Hz.
        Assert.True(at60 > 100 + (200 - 100) * 0.08f * 6, $"at60={at60}");
    }

    [Fact]
    public void LaserRadius_FollowsTheMacOSFormula()
    {
        // max(4, screenH × 0.0038 × size/0.13) × (1 + 0.2 × press)
        Assert.Equal(2160 * 0.0038f, HandPoseSolver.LaserRadius(2160, 0), 3);
        Assert.Equal(2160 * 0.0038f * 1.2f, HandPoseSolver.LaserRadius(2160, 1), 3);
        Assert.Equal(4f, HandPoseSolver.LaserRadius(100, 0), 3);
        Assert.Equal(8.208f * 3.2f, HandPoseSolver.LaserGlowRadius(8.208f, 1f), 3);
    }
}

public class HandAssetTests
{
    /// <summary>Synthetic sprite: narrow finger (rows 10–40), wide fist (40–100), narrow stem
    /// (100–190) on a 100×200 canvas.</summary>
    private static Bitmap Synthetic()
    {
        var bitmap = new Bitmap(100, 200, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(255, 40, 40, 46));
        g.FillRectangle(brush, 45, 10, 11, 30);
        g.FillRectangle(brush, 20, 40, 61, 60);
        g.FillRectangle(brush, 45, 100, 11, 90);
        return bitmap;
    }

    [Fact]
    public void Measure_MatchesTheMacOSProfile()
    {
        using var bitmap = Synthetic();
        var metrics = HandAssets.Measure(bitmap);
        Assert.NotNull(metrics);
        var m = metrics!;
        Assert.Equal(0.5f, m.Aspect, 3);
        Assert.Equal(0.05f, m.Tip, 3);
        Assert.Equal(0.046f, m.HandTop, 3);
        Assert.Equal(0.5f, m.Wrist, 3);
        Assert.Equal(0.945f, m.RodEnd, 3);
        Assert.Equal(0.5f, m.TipX, 3);
        Assert.Equal(0.5f, m.RodX, 3);
        Assert.Equal(0.2f, m.HandMinX, 3);
        Assert.Equal(0.8f, m.HandMaxX, 3);
        Assert.Equal(0.454f, m.HandSpan, 3);
        Assert.Equal(0.445f, m.RodSpan, 3);
    }

    [Fact]
    public void Measure_RejectsAnEmptySprite()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        Assert.Null(HandAssets.Measure(bitmap));
    }

    [Fact]
    public void EmbeddedSprites_MeasureLikeTheMacOSAssets()
    {
        foreach (var style in PointerStyle.BuiltIn)
        {
            var metrics = HandAssets.Metrics(style.Id);
            Assert.NotNull(metrics);
            var m = metrics!;
            Assert.Equal(1024f / 1536f, m.Aspect, 3);
            Assert.InRange(m.Tip, 0.001f, 0.2f);
            Assert.True(m.Wrist > m.Tip, $"{style.Id}: wrist {m.Wrist} <= tip {m.Tip}");
            Assert.True(m.RodEnd > m.Wrist, $"{style.Id}: rodEnd {m.RodEnd} <= wrist {m.Wrist}");
            Assert.InRange(m.TipX, 0.05f, 0.95f);
            Assert.InRange(m.RodX, 0.05f, 0.95f);
            Assert.True(m.HandMaxX > m.HandMinX, $"{style.Id}: empty hand extent");
        }
    }

    [Fact]
    public void Thumbnail_IsASquareCrop()
    {
        var thumb = HandAssets.Thumbnail("lavender");
        Assert.NotNull(thumb);
        Assert.Equal(96, thumb!.Width);
        Assert.Equal(96, thumb.Height);
    }

    [Fact]
    public void Shadow_IsABlurredPaddedSilhouette()
    {
        var shadow = HandAssets.Shadow("lavender", 2160);
        Assert.NotNull(shadow);
        var soft = shadow!.Soft;
        var tight = shadow.Tight;
        // 1/4 resolution of the 1024×1536 sprite plus the transparent blur margin.
        Assert.True(soft.Padding > 0, $"padding={soft.Padding}");
        Assert.Equal(256 + 2 * soft.Padding, soft.Bitmap.Width);
        Assert.Equal(384 + 2 * soft.Padding, soft.Bitmap.Height);
        // The pressed level is tighter: smaller blur, smaller padding.
        Assert.True(tight.Padding < soft.Padding, $"soft={soft.Padding} tight={tight.Padding}");
        // Black with alpha only: the blur spreads alpha past the sprite's opaque edge, into
        // the padding (the fingertip's shadow lives there).
        var pixel = soft.Bitmap.GetPixel(soft.Padding + 128, soft.Padding + 40);
        Assert.Equal(0, pixel.R);
        Assert.Equal(0, pixel.G);
        Assert.Equal(0, pixel.B);
        // The fingertip's shadow lives in the padding above the silhouette's top row.
        var metrics = HandAssets.Metrics("lavender")!;
        int silhouetteTop = soft.Padding + (int)(metrics.HandTop * 384);
        int tipX = soft.Padding + (int)(metrics.TipX * 256);
        var aboveTip = soft.Bitmap.GetPixel(tipX, silhouetteTop - 10);
        Assert.True(aboveTip.A > 0, $"alpha above the fingertip={aboveTip.A}");
    }

    [Fact]
    public void ShadowRadius_TightensWithThePress()
    {
        // macOS: max(1, lift × 0.35) + 5 × press; 4K rest 45.4 px → full press 14.1 px.
        Assert.Equal(0.06f * 2160 * 0.35f, HandPoseSolver.ShadowRadius(2160, 0), 1);
        Assert.Equal(0.06f * 2160 * 0.2f * 0.35f + 5, HandPoseSolver.ShadowRadius(2160, 1), 1);
        Assert.True(HandPoseSolver.ShadowRadius(2160, 1) < HandPoseSolver.ShadowRadius(2160, 0) / 2);
    }
}

public class PointerStyleSelectionTests
{
    [Fact]
    public void All_IsThe36HandsPlusLaser()
    {
        Assert.Equal(37, PointerStyle.All.Count);
        Assert.Equal("lavender", PointerStyle.All[0].Id);
        Assert.Equal("cookie", PointerStyle.All[35].Id);
        Assert.Equal("laser", PointerStyle.All[36].Id);
        Assert.True(PointerStyle.Laser.IsLaser);
        Assert.Null(PointerStyle.Laser.AssetName);
        Assert.Equal("hand_lavender", PointerStyle.Lavender.AssetName);
    }

    [Fact]
    public void ById_FallsBackToTheDefault()
    {
        Assert.Equal("coral", PointerStyle.ById("coral").Id);
        Assert.Equal(PointerStyle.Default.Id, PointerStyle.ById("nope").Id);
        Assert.Equal("lavender", PointerStyle.Default.Id);
    }

    [Fact]
    public void Defaults_MatchTheMacOSPreferences()
    {
        Assert.Equal("lavender", Preferences.Style.Id);
        Assert.Equal(InteractKey.Alt, Preferences.InteractKey);
        Assert.False(Preferences.InteractKeepsHand);
        Assert.True(Preferences.SoundOn);
        Assert.True(Preferences.DoubleShiftClickThrough);
        Assert.True(Preferences.ShowShadow);
        Assert.Equal(0.13f, Preferences.PointerSize, 3);
        Assert.Equal(TapSoundChoice.Thock, Preferences.TapSound);
    }

    [Fact]
    public void PointerSize_ClampsToTheMacOSRange()
    {
        float original = Preferences.PointerSize;
        try
        {
            Preferences.PointerSize = 0.5f;
            Assert.Equal(0.20f, Preferences.PointerSize, 3);
            Preferences.PointerSize = 0.01f;
            Assert.Equal(0.08f, Preferences.PointerSize, 3);
        }
        finally
        {
            Preferences.PointerSize = original;
        }
    }
}

public class TapSoundTests
{
    [Fact]
    public void All15EmbeddedSoundsLoad()
    {
        using var sound = new TapSound(TapSoundChoice.Thock);
        Assert.Equal(15, sound.LoadedSounds);
        Assert.Equal(15, Enum.GetValues<TapSoundChoice>().Length);
    }

    [Fact]
    public void Play_IsCountedEvenWhenMuted()
    {
        using var sound = new TapSound(TapSoundChoice.Click) { Enabled = false };
        sound.Play();
        Assert.Equal(0, sound.PlayCount); // muted: nothing queued
        sound.Enabled = true;
        sound.Play();
        Assert.Equal(1, sound.PlayCount);
    }
}
