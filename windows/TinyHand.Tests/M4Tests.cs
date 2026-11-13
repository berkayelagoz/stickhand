using System.Drawing;
using System.Drawing.Imaging;
using TinyHand.Interop;
using TinyHand.Logic;

namespace TinyHand.Tests;

public class HotkeyTests
{
    [Fact]
    public void DefaultsMatchTheMacOSChords()
    {
        Assert.Equal("Ctrl+Alt+H", Hotkey.ToggleDefault.Display);
        Assert.Equal("Ctrl+Alt+X", Hotkey.ClearDefault.Display);
        Assert.Equal(Win32.MOD_CONTROL | Win32.MOD_ALT, Hotkey.ToggleDefault.Modifiers);
        Assert.Equal(0x48u, Hotkey.ToggleDefault.Vk);
    }

    [Theory]
    [InlineData("Ctrl+Alt+H")]
    [InlineData("Ctrl+Shift+T")]
    [InlineData("Alt+F4")]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Shift+Win+Left")]
    public void ParseRoundTripsTheDisplay(string display)
    {
        Assert.True(Hotkey.TryParse(display, out var hotkey));
        Assert.Equal(display, hotkey.Display);
    }

    [Theory]
    [InlineData("H")] // no modifier
    [InlineData("Ctrl+")] // no key
    [InlineData("Ctrl+Nope")] // unknown key
    [InlineData("")]
    [InlineData(null)]
    public void ParseRejectsInvalidChords(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void TryFromKeyRequiresAModifier()
    {
        Assert.False(Hotkey.TryFromKey(Keys.H, Keys.None, out _));
        Assert.True(Hotkey.TryFromKey(Keys.H, Keys.Control | Keys.Alt, out var hotkey));
        Assert.Equal("Ctrl+Alt+H", hotkey.Display);
    }

    [Fact]
    public void TryFromKeyRejectsModifierOnlyPresses()
    {
        Assert.False(Hotkey.TryFromKey(Keys.ControlKey, Keys.Control, out _));
        Assert.False(Hotkey.TryFromKey(Keys.Menu, Keys.Alt, out _));
    }

    [Fact]
    public void FormatOrdersModifiersLikeTheMacOSSymbols()
    {
        Assert.Equal("Ctrl+Alt+Shift+Win+K",
            Hotkey.Format(Win32.MOD_WIN | Win32.MOD_SHIFT | Win32.MOD_ALT | Win32.MOD_CONTROL, 0x4B));
    }
}

public class AutomationUrlTests
{
    [Theory]
    [InlineData("tinyhand://on", "on", null)]
    [InlineData("tinyhand://off", "off", null)]
    [InlineData("tinyhand://toggle", "toggle", null)]
    [InlineData("tinyhand://clear", "clear", null)]
    [InlineData("tinyhand://style/coral", "style", "coral")]
    [InlineData("tinyhand://style/custom-abc12345", "style", "custom-abc12345")]
    [InlineData("tinyhand://sound/on", "sound", "on")]
    [InlineData("tinyhand://sound/toggle", "sound", "toggle")]
    [InlineData("TINYHAND://ON", "on", null)]
    public void ParsesTheCommandGrammar(string url, string command, string? argument)
    {
        var parsed = Automation.Parse(url);
        Assert.NotNull(parsed);
        Assert.Equal(command, parsed.Value.Command);
        Assert.Equal(argument, parsed.Value.Argument);
    }

    [Theory]
    [InlineData("https://tinyhand.app")]
    [InlineData("tinyhand://")]
    [InlineData("not a url")]
    public void RejectsOtherInput(string url)
    {
        Assert.Null(Automation.Parse(url));
    }
}

public class HandMakerTests
{
    [Fact]
    public void ValidHandComposesOntoTheCanvas()
    {
        using var hand = TestHands.Synthetic();
        var made = HandMaker.Make(hand);

        Assert.Equal(HandMaker.CanvasWidth, made.Image.Width);
        Assert.Equal(HandMaker.CanvasHeight, made.Image.Height);
        Assert.True(made.Image.GetPixel(HandMaker.CanvasWidth / 2, 400).A > 200);
        Assert.True(made.Image.GetPixel(HandMaker.CanvasWidth / 2, 1200).A > 200);
        Assert.InRange(made.Ink, 0x800000u, 0xFFFFFFu);
        made.Image.Dispose();
    }

    [Fact]
    public void RejectsAnImageWithoutAlpha()
    {
        using var bitmap = new Bitmap(512, 768, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.White);
        var problem = Assert.Throws<HandMaker.Problem>(() => HandMaker.Make(bitmap));
        Assert.Contains("transparent background", problem.Message);
    }

    [Fact]
    public void RejectsATinyImage()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        var problem = Assert.Throws<HandMaker.Problem>(() => HandMaker.Make(bitmap));
        Assert.Contains("too small", problem.Message);
    }

    [Fact]
    public void RejectsAHandTouchingTheEdge()
    {
        using var bitmap = TestHands.Blank();
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.Orange))
            g.FillRectangle(brush, 0, 60, 50, 640);
        var problem = Assert.Throws<HandMaker.Problem>(() => HandMaker.Make(bitmap));
        Assert.Contains("touches the edge", problem.Message);
    }

    [Fact]
    public void RejectsTwoObjects()
    {
        using var bitmap = TestHands.Blank();
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.Orange))
        {
            g.FillRectangle(brush, 120, 60, 50, 640);
            g.FillRectangle(brush, 340, 60, 50, 640);
        }
        var problem = Assert.Throws<HandMaker.Problem>(() => HandMaker.Make(bitmap));
        Assert.Contains("more than one thing", problem.Message);
    }

    [Fact]
    public void RejectsAWideTop()
    {
        using var bitmap = TestHands.Blank();
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.Orange))
            g.FillRectangle(brush, 120, 60, 260, 640);
        var problem = Assert.Throws<HandMaker.Problem>(() => HandMaker.Make(bitmap));
        Assert.Contains("single finger", problem.Message);
    }

    [Fact]
    public void RejectsAMissingWrist()
    {
        using var bitmap = TestHands.Blank();
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.Orange))
        {
            g.FillRectangle(brush, 180, 60, 50, 240);
            g.FillRectangle(brush, 120, 300, 260, 260);
            g.FillRectangle(brush, 240, 560, 20, 140);
        }
        var problem = Assert.Throws<HandMaker.Problem>(() => HandMaker.Make(bitmap));
        Assert.Contains("flat wrist", problem.Message);
    }

    [Fact]
    public void RejectsASoftShadow()
    {
        using var bitmap = TestHands.Blank();
        using (var g = Graphics.FromImage(bitmap))
        {
            using var glow = new SolidBrush(Color.FromArgb(120, 200, 120, 40));
            g.FillRectangle(glow, 100, 40, 300, 700);
            using var solid = new SolidBrush(Color.FromArgb(255, 200, 120, 40));
            g.FillRectangle(solid, 180, 60, 50, 240);
            g.FillRectangle(solid, 120, 300, 260, 260);
            g.FillRectangle(solid, 200, 560, 100, 140);
        }
        var problem = Assert.Throws<HandMaker.Problem>(() => HandMaker.Make(bitmap));
        Assert.Contains("soft shadow", problem.Message);
    }

    [Fact]
    public void PromptFillsTheIdeaAndKeepsTheRules()
    {
        string prompt = HandMaker.PromptFor("knitted wool in mustard yellow");
        Assert.Contains("knitted wool in mustard yellow", prompt);
        Assert.DoesNotContain("[describe your hand", prompt);
        Assert.Contains("Transparent background", prompt);
        Assert.Contains("a soft vinyl toy in a bright colour", HandMaker.PromptFor("   "));
    }
}

public class CustomHandsTests
{
    [Fact]
    public void AddAndRemoveRoundTrip()
    {
        int before = PointerStyle.All.Count;
        using var hand = TestHands.Synthetic();
        var made = HandMaker.Make(hand);
        var style = CustomHands.Add(made.Image, made.Ink, "Test hand");
        made.Image.Dispose();

        try
        {
            Assert.StartsWith("custom-", style.Id);
            Assert.Equal(before + 1, PointerStyle.All.Count);
            Assert.Equal(style.Id, PointerStyle.All[^2].Id);
            Assert.Equal("laser", PointerStyle.All[^1].Id);
            Assert.Equal("Test hand", PointerStyle.ById(style.Id).Title);
            Assert.True(PointerStyle.ById(style.Id).IsCustom);
            Assert.Equal(style.Id, PointerStyle.ById(style.Id).AssetName);
            Assert.NotNull(HandAssets.Sprite(style.Id));
            Assert.NotNull(HandAssets.Metrics(style.Id));

            var layout = ToolbarModel.Build(3840, 96, style.Id, true);
            Assert.Equal(PointerStyle.All.Count + 2, layout.Buttons.Count);
            Assert.Contains(layout.Buttons, button => button.Id == style.Id && button.Selected);
        }
        finally
        {
            CustomHands.Remove(style);
        }

        Assert.Equal(before, PointerStyle.All.Count);
        Assert.Null(HandAssets.Sprite(style.Id));
        Assert.False(File.Exists(Path.Combine(CustomHands.Directory, style.Id + ".png")));
    }

    [Fact]
    public void SuggestedNameUsesTheFileName()
    {
        Assert.Equal("Mustard hand", CustomHands.SuggestedName("/tmp/mustard_hand.png"));
        Assert.Equal("My hand 1", CustomHands.SuggestedName("/tmp/chatgpt image 4.png"));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void ValuesRoundTripThroughTheFile()
    {
        string dir = Path.Combine(TestSetup.Root, "settings-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            SettingsStore.Enable(Path.Combine(dir, "settings.json"));
            Preferences.Reload();

            Preferences.FadeSeconds = 7;
            Preferences.Style = PointerStyle.ById("mint");
            Preferences.InteractKey = InteractKey.Ctrl;
            Preferences.InteractKeepsHand = true;
            Preferences.SoundOn = false;
            Preferences.TapSound = TapSoundChoice.Coin;
            Preferences.InkChoice = InkChoice.Blue;
            Preferences.SmoothingLevel = SmoothingLevel.Strong;
            Preferences.PointerSize = 0.17f;
            Preferences.ShowShadow = false;
            Preferences.DoubleShiftClickThrough = false;
            Preferences.ToggleShortcut = new Hotkey(Win32.MOD_CONTROL | Win32.MOD_SHIFT, 0x54, "Ctrl+Shift+T");
            Preferences.ClearShortcut = new Hotkey(Win32.MOD_CONTROL | Win32.MOD_SHIFT, 0x59, "Ctrl+Shift+Y");
            SettingsStore.Flush();

            string json = File.ReadAllText(Path.Combine(dir, "settings.json"));
            Assert.Contains("\"fadeSeconds\": 7", json);
            Assert.Contains("\"mint\"", json);
            Assert.Contains("Ctrl+Shift+T", json);

            // Change everything in memory, then reload from disk: the file wins.
            Preferences.FadeSeconds = 1;
            Preferences.Style = PointerStyle.Default;
            Preferences.InteractKey = InteractKey.Off;
            Preferences.InteractKeepsHand = false;
            Preferences.SoundOn = true;
            Preferences.TapSound = TapSoundChoice.Thock;
            Preferences.InkChoice = InkChoice.Match;
            Preferences.SmoothingLevel = SmoothingLevel.Off;
            Preferences.PointerSize = 0.1f;
            Preferences.ShowShadow = true;
            Preferences.DoubleShiftClickThrough = true;
            Preferences.ToggleShortcut = Hotkey.ToggleDefault;
            Preferences.ClearShortcut = Hotkey.ClearDefault;

            SettingsStore.Reload();
            Preferences.Reload();

            Assert.Equal(7, Preferences.FadeSeconds);
            Assert.Equal("mint", Preferences.Style.Id);
            Assert.Equal(InteractKey.Ctrl, Preferences.InteractKey);
            Assert.True(Preferences.InteractKeepsHand);
            Assert.False(Preferences.SoundOn);
            Assert.Equal(TapSoundChoice.Coin, Preferences.TapSound);
            Assert.Equal(InkChoice.Blue, Preferences.InkChoice);
            Assert.Equal(SmoothingLevel.Strong, Preferences.SmoothingLevel);
            Assert.Equal(0.17f, Preferences.PointerSize, 3);
            Assert.False(Preferences.ShowShadow);
            Assert.False(Preferences.DoubleShiftClickThrough);
            Assert.Equal("Ctrl+Shift+T", Preferences.ToggleShortcut.Display);
            Assert.Equal("Ctrl+Shift+Y", Preferences.ClearShortcut.Display);
        }
        finally
        {
            SettingsStore.Disable();
            Preferences.ResetAll();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ResetAllRestoresTheMacOSDefaults()
    {
        Preferences.FadeSeconds = 9;
        Preferences.Style = PointerStyle.ById("coral");
        Preferences.InteractKey = InteractKey.Shift;
        Preferences.ResetAll();

        Assert.Equal(Preferences.DefaultFadeSeconds, Preferences.FadeSeconds);
        Assert.Equal(PointerStyle.Default.Id, Preferences.Style.Id);
        Assert.Equal(InteractKey.Alt, Preferences.InteractKey);
        Assert.Equal(Hotkey.ToggleDefault.Display, Preferences.ToggleShortcut.Display);
        Assert.Equal(Hotkey.ClearDefault.Display, Preferences.ClearShortcut.Display);
    }
}

internal static class TestHands
{
    public static Bitmap Blank() => new(512, 768, PixelFormat.Format32bppArgb);

    /// <summary>Narrow finger, wide fist, flat wrist — passes every HandMaker rule.</summary>
    public static Bitmap Synthetic()
    {
        var bitmap = Blank();
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(255, 200, 120, 40));
        g.FillRectangle(brush, 180, 60, 50, 260);
        g.FillRectangle(brush, 120, 300, 260, 260);
        g.FillRectangle(brush, 200, 560, 100, 140);
        return bitmap;
    }
}
