using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using TinyHand.Interop;
using TinyHand.Logic;

namespace TinyHand;

/// <summary>
/// M4 system-integration harness: protocol registration, URL commands, state.json, the
/// single-instance pipe, settings persistence, the settings/welcome windows and the
/// HandMaker pipeline (validation + compose + custom hand in the toolbar).
///
/// Run: TinyHand.exe --m4check
/// </summary>
internal static class M4Check
{
    private static int _failures;
    private static int _checks;
    private static string _temp = "";

    public static int Run(string[] args)
    {
        Program.TryAttachConsole();

        _temp = Path.Combine(Path.GetTempPath(), "tinyhand-m4-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
        Environment.SetEnvironmentVariable("TH_SETTINGS_DIR", _temp);
        Environment.SetEnvironmentVariable("TH_HANDS_DIR", Path.Combine(_temp, "Hands"));
        SettingsStore.Enable();
        Preferences.Reload();

        Console.WriteLine($"m4check: temp={_temp}, style {Preferences.Style.Id}, " +
            $"toggle {Preferences.ToggleShortcut.Display}, clear {Preferences.ClearShortcut.Display}");

        try
        {
            CheckProtocol();
            CheckUrlParsing();
            CheckStateFile();
            CheckUrlCommands();
            CheckSingleInstance();
            CheckSettingsPersistence();
            CheckTapSound();
            CheckHandMaker();
            CheckCustomHands();
            CheckWindows();
        }
        catch (Exception ex)
        {
            Check("harness completed", false, ex.ToString());
        }
        finally
        {
            SettingsStore.Disable();
            try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine($"m4check: {_checks - _failures}/{_checks} passed");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // Protocol + URL grammar
    // ------------------------------------------------------------------

    private static void CheckProtocol()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        bool registered = Automation.RegisterProtocol(exe);
        Check("protocol registers under HKCU", registered, exe);
        Check("protocol points at this exe", Automation.IsProtocolRegistered(exe), exe);

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\tinyhand\shell\open\command");
        string command = key?.GetValue(null) as string ?? "";
        Check("protocol command passes the url", command.Contains("\"%1\""), command);
        using var protocol = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\tinyhand");
        Check("protocol marks itself as a url handler",
            protocol?.GetValue("URL Protocol") is not null, "URL Protocol");
    }

    private static void CheckUrlParsing()
    {
        var on = Automation.Parse("tinyhand://on");
        Check("url on", on is ("on", null), Describe(on));
        var style = Automation.Parse("tinyhand://style/coral");
        Check("url style/<id>", style is ("style", "coral"), Describe(style));
        var sound = Automation.Parse("tinyhand://sound/off");
        Check("url sound/<arg>", sound is ("sound", "off"), Describe(sound));
        Check("url rejects other schemes", Automation.Parse("https://tinyhand.app") is null, "https");
        Check("url rejects empty host", Automation.Parse("tinyhand://") is null, "empty");
    }

    private static string Describe((string Command, string? Argument)? parsed) =>
        parsed is null ? "null" : $"{parsed.Value.Command}/{parsed.Value.Argument ?? "-"}";

    // ------------------------------------------------------------------
    // state.json
    // ------------------------------------------------------------------

    private static void CheckStateFile()
    {
        using var controller = new OverlayController();
        using var automation = new Automation(controller);
        controller.SetActive(true);
        Pump(600);

        automation.WriteState();
        Check("state.json written", File.Exists(Automation.StatePath), Automation.StatePath);

        using var document = JsonDocument.Parse(File.ReadAllText(Automation.StatePath));
        var root = document.RootElement;
        Check("state pid", root.GetProperty("pid").GetInt32() == Environment.ProcessId,
            root.GetProperty("pid").GetInt32().ToString());
        Check("state active", root.GetProperty("active").GetBoolean(), "active");
        Check("state style", root.GetProperty("style").GetString() == Preferences.Style.Id,
            root.GetProperty("style").GetString() ?? "");
        Check("state soundOn", root.GetProperty("soundOn").GetBoolean() == Preferences.SoundOn, "soundOn");

        var styles = root.GetProperty("styles");
        Check("state styles list", styles.GetArrayLength() == PointerStyle.All.Count,
            $"{styles.GetArrayLength()} vs {PointerStyle.All.Count}");
        var first = styles[0];
        Check("state style entry shape",
            first.GetProperty("id").GetString() == "lavender"
            && first.GetProperty("name").GetString() == "Lavender toy"
            && first.GetProperty("type").GetString() == "hand",
            first.ToString());
        var last = styles[styles.GetArrayLength() - 1];
        Check("state laser entry", last.GetProperty("type").GetString() == "laser", last.ToString());

        // Sorted keys: the macOS writer uses .sortedKeys, launchers may rely on the order.
        string text = File.ReadAllText(Automation.StatePath);
        int active = text.IndexOf("\"active\"", StringComparison.Ordinal);
        int pid = text.IndexOf("\"pid\"", StringComparison.Ordinal);
        int soundOn = text.IndexOf("\"soundOn\"", StringComparison.Ordinal);
        int style = text.IndexOf("\"style\"", StringComparison.Ordinal);
        Check("state keys sorted", active >= 0 && active < pid && pid < soundOn && soundOn < style,
            $"{active},{pid},{soundOn},{style}");

        // Stop the debounced writer before deleting: a pending write must not recreate the
        // file after the exit contract removed it.
        automation.Dispose();
        Automation.DeleteState();
        Check("state.json removed on exit", !File.Exists(Automation.StatePath), "deleted");
    }

    // ------------------------------------------------------------------
    // URL commands
    // ------------------------------------------------------------------

    private static void CheckUrlCommands()
    {
        using var controller = new OverlayController();
        using var automation = new Automation(controller);
        controller.SetActive(true);
        Pump(500);

        automation.Handle("tinyhand://off");
        Pump(300);
        Check("url off deactivates", !controller.ActiveRequested, controller.ActiveRequested.ToString());

        automation.Handle("tinyhand://on");
        Pump(300);
        Check("url on activates", controller.ActiveRequested, controller.ActiveRequested.ToString());

        automation.Handle("tinyhand://toggle");
        Pump(300);
        Check("url toggle flips", !controller.ActiveRequested, controller.ActiveRequested.ToString());
        automation.Handle("tinyhand://toggle");
        Pump(300);

        automation.Handle("tinyhand://style/coral");
        Pump(300);
        Check("url style selects and activates",
            Preferences.Style.Id == "coral" && controller.ActiveRequested,
            $"{Preferences.Style.Id} active={controller.ActiveRequested}");

        automation.Handle("tinyhand://style/nope");
        Pump(200);
        Check("url unknown style ignored", Preferences.Style.Id == "coral", Preferences.Style.Id);

        automation.Handle("tinyhand://sound/off");
        Pump(200);
        Check("url sound off", !Preferences.SoundOn, Preferences.SoundOn.ToString());
        automation.Handle("tinyhand://sound/toggle");
        Pump(200);
        Check("url sound toggle", Preferences.SoundOn, Preferences.SoundOn.ToString());

        automation.Handle("tinyhand://clear");
        Pump(200);
        Check("url clear", controller.InkStrokeCount == 0, controller.InkStrokeCount.ToString());

        automation.Handle("tinyhand://bogus");
        Pump(100);
        Check("url unknown command ignored", true, "logged");
    }

    // ------------------------------------------------------------------
    // Single instance
    // ------------------------------------------------------------------

    private static void CheckSingleInstance()
    {
        using var first = new SingleInstance();
        if (!first.TryAcquire())
        {
            Check("single instance (skipped: another TinyHand is running)", true, "skipped");
            return;
        }

        using var second = new SingleInstance();
        Check("second instance is refused", !second.TryAcquire(), "refused");

        string? received = null;
        using var signal = new ManualResetEventSlim(false);
        first.CommandReceived += command =>
        {
            received = command;
            signal.Set();
        };
        bool sent = SingleInstance.Send("tinyhand://toggle");
        bool arrived = signal.Wait(TimeSpan.FromSeconds(2));
        Check("pipe forwards the command", sent && arrived && received == "tinyhand://toggle",
            $"{sent}/{arrived}/{received}");
    }

    // ------------------------------------------------------------------
    // Settings persistence
    // ------------------------------------------------------------------

    private static void CheckSettingsPersistence()
    {
        Preferences.FadeSeconds = 6;
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

        string path = SettingsStore.DefaultPath;
        Check("settings.json written", File.Exists(path), path);
        string json = File.ReadAllText(path);
        Check("settings file has the values",
            json.Contains("\"fadeSeconds\": 6") && json.Contains("\"mint\"")
            && json.Contains("Ctrl+Shift+T") && json.Contains("\"Coin\""),
            json);

        // Change everything, then reload from disk: the file must win.
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
        Check("settings round-trip",
            Preferences.FadeSeconds == 6
            && Preferences.Style.Id == "mint"
            && Preferences.InteractKey == InteractKey.Ctrl
            && Preferences.InteractKeepsHand
            && !Preferences.SoundOn
            && Preferences.TapSound == TapSoundChoice.Coin
            && Preferences.InkChoice == InkChoice.Blue
            && Preferences.SmoothingLevel == SmoothingLevel.Strong
            && Math.Abs(Preferences.PointerSize - 0.17f) < 0.001f
            && !Preferences.ShowShadow
            && !Preferences.DoubleShiftClickThrough
            && Preferences.ToggleShortcut.Display == "Ctrl+Shift+T"
            && Preferences.ClearShortcut.Display == "Ctrl+Shift+Y",
            $"fade {Preferences.FadeSeconds}, style {Preferences.Style.Id}, interact {Preferences.InteractKey}, " +
            $"keeps {Preferences.InteractKeepsHand}, sound {Preferences.SoundOn}, tap {Preferences.TapSound}, " +
            $"ink {Preferences.InkChoice}, smooth {Preferences.SmoothingLevel}, size {Preferences.PointerSize}, " +
            $"shadow {Preferences.ShowShadow}, dblshift {Preferences.DoubleShiftClickThrough}, " +
            $"toggle {Preferences.ToggleShortcut.Display}, clear {Preferences.ClearShortcut.Display}");

        Preferences.ResetAll();
        Check("reset all restores defaults",
            Preferences.FadeSeconds == Preferences.DefaultFadeSeconds
            && Preferences.Style.Id == PointerStyle.Default.Id
            && Preferences.InteractKey == InteractKey.Alt
            && Preferences.SoundOn
            && Preferences.TapSound == TapSoundChoice.Thock
            && Preferences.InkChoice == InkChoice.Match
            && Preferences.ToggleShortcut.Display == Hotkey.ToggleDefault.Display,
            $"{Preferences.FadeSeconds}/{Preferences.Style.Id}/{Preferences.InteractKey}");
    }

    // ------------------------------------------------------------------
    // Tap sound choice
    // ------------------------------------------------------------------

    private static void CheckTapSound()
    {
        using var sound = new TapSound(TapSoundChoice.Thock);
        sound.Warm();
        Pump(600);
        Check("tap sound opens the default voice", sound.VoicesChoice == TapSoundChoice.Thock,
            $"{sound.VoicesChoice}");

        sound.Choice = TapSoundChoice.Coin;
        sound.Play();
        Pump(600);
        Check("tap sound follows the choice", sound.VoicesChoice == TapSoundChoice.Coin,
            $"{sound.VoicesChoice}");

        // The settings window changes the sound while it is already on: the controller must
        // apply the preference even when SetSound's on/off state does not change.
        using var controller = new OverlayController();
        Preferences.TapSound = TapSoundChoice.Quack;
        Preferences.SoundOn = true;
        controller.SetSound(true);
        Check("controller applies the tap sound", controller.TapSound.Choice == TapSoundChoice.Quack,
            controller.TapSound.Choice.ToString());
    }

    // ------------------------------------------------------------------
    // HandMaker
    // ------------------------------------------------------------------

    private static void CheckHandMaker()
    {
        string valid = Path.Combine(_temp, "hand.png");
        using (var hand = SyntheticHand())
            hand.Save(valid, ImageFormat.Png);

        var made = HandMaker.Make(valid);
        Check("handmaker composes the canvas",
            made.Image.Width == HandMaker.CanvasWidth && made.Image.Height == HandMaker.CanvasHeight,
            $"{made.Image.Width}x{made.Image.Height}");
        Check("handmaker draws the hand at the top",
            made.Image.GetPixel(HandMaker.CanvasWidth / 2, 400).A > 200,
            made.Image.GetPixel(HandMaker.CanvasWidth / 2, 400).ToString());
        Check("handmaker draws the stem at the bottom",
            made.Image.GetPixel(HandMaker.CanvasWidth / 2, 1200).A > 200
            && made.Image.GetPixel(HandMaker.CanvasWidth / 2, 1200).R < 90,
            made.Image.GetPixel(HandMaker.CanvasWidth / 2, 1200).ToString());
        Check("handmaker extracts the ink colour",
            made.Ink is > 0x800000 and < 0xFFFFFF, $"0x{made.Ink:X6}");
        made.Image.Dispose();

        CheckProblem("handmaker rejects a missing alpha channel", NoAlpha(), "transparent background");
        CheckProblem("handmaker rejects a tiny image", Tiny(), "too small");
        CheckProblem("handmaker rejects a hand touching the edge", EdgeTouching(), "touches the edge");
        CheckProblem("handmaker rejects two objects", TwoObjects(), "more than one thing");
        CheckProblem("handmaker rejects a wide top", WideTop(), "single finger");
        CheckProblem("handmaker rejects a missing wrist", NoWrist(), "flat wrist");

        string prompt = HandMaker.PromptFor("knitted wool in mustard yellow");
        Check("handmaker prompt fills the idea",
            prompt.Contains("knitted wool in mustard yellow") && !prompt.Contains("[describe your hand"),
            prompt.Length.ToString());
        Check("handmaker prompt default idea",
            HandMaker.PromptFor("  ").Contains("a soft vinyl toy in a bright colour"), "default");
    }

    private static void CheckProblem(string name, Bitmap bitmap, string expected)
    {
        using (bitmap)
        {
            try
            {
                using var result = HandMaker.Make(bitmap).Image;
                Check(name, false, "no problem raised");
            }
            catch (HandMaker.Problem problem)
            {
                Check(name, problem.Message.Contains(expected, StringComparison.OrdinalIgnoreCase),
                    problem.Message);
            }
            catch (Exception ex)
            {
                Check(name, false, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private static Bitmap SyntheticHand()
    {
        var bitmap = new Bitmap(512, 768, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(255, 200, 120, 40));
        g.FillRectangle(brush, 180, 60, 50, 260);   // index finger
        g.FillRectangle(brush, 120, 300, 260, 260); // fist
        g.FillRectangle(brush, 200, 560, 100, 140); // wrist
        return bitmap;
    }

    private static Bitmap NoAlpha()
    {
        var bitmap = new Bitmap(512, 768, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        return bitmap;
    }

    private static Bitmap Tiny()
    {
        var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.Orange);
        g.FillRectangle(brush, 8, 4, 6, 24);
        return bitmap;
    }

    private static Bitmap EdgeTouching()
    {
        var bitmap = new Bitmap(512, 768, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.Orange);
        g.FillRectangle(brush, 0, 60, 50, 640);
        return bitmap;
    }

    private static Bitmap TwoObjects()
    {
        var bitmap = new Bitmap(512, 768, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.Orange);
        g.FillRectangle(brush, 120, 60, 50, 640);
        g.FillRectangle(brush, 340, 60, 50, 640);
        return bitmap;
    }

    private static Bitmap WideTop()
    {
        var bitmap = new Bitmap(512, 768, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.Orange);
        g.FillRectangle(brush, 120, 60, 260, 640);
        return bitmap;
    }

    private static Bitmap NoWrist()
    {
        var bitmap = new Bitmap(512, 768, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.Orange);
        // Narrow finger, wide fist, then a stem too thin to be a wrist.
        g.FillRectangle(brush, 180, 60, 50, 240);
        g.FillRectangle(brush, 120, 300, 260, 260);
        g.FillRectangle(brush, 240, 560, 20, 140);
        return bitmap;
    }

    // ------------------------------------------------------------------
    // Custom hands
    // ------------------------------------------------------------------

    private static void CheckCustomHands()
    {
        using var controller = new OverlayController();
        controller.SetActive(true);
        Pump(500);

        int before = PointerStyle.All.Count;
        using var hand = SyntheticHand();
        var made = HandMaker.Make(hand);
        var style = CustomHands.Add(made.Image, made.Ink, "Test hand");
        made.Image.Dispose();

        Check("custom hand added", style.Id.StartsWith("custom-"), style.Id);
        Check("custom hand in the style list", PointerStyle.All.Count == before + 1,
            $"{before} -> {PointerStyle.All.Count}");
        Check("custom hand before the laser",
            PointerStyle.All[^2].Id == style.Id && PointerStyle.All[^1].Id == "laser",
            $"{PointerStyle.All[^2].Id}/{PointerStyle.All[^1].Id}");
        Check("custom hand title", PointerStyle.ById(style.Id).Title == "Test hand",
            PointerStyle.ById(style.Id).Title);
        Check("custom hand ink", PointerStyle.ById(style.Id).Ink.ToArgb() == InkPalette.Rgb(made.Ink).ToArgb(),
            $"0x{made.Ink:X6}");
        Check("custom hand sprite loads", HandAssets.Sprite(style.Id) is not null, style.Id);
        Check("custom hand metrics", HandAssets.Metrics(style.Id) is not null, style.Id);
        Check("custom hand thumbnail", HandAssets.Thumbnail(style.Id) is not null, style.Id);

        var layout = ToolbarModel.Build(3840, 96, style.Id, true);
        Check("toolbar includes the custom hand", layout.Buttons.Count == PointerStyle.All.Count + 2,
            $"{layout.Buttons.Count} vs {PointerStyle.All.Count + 2}");
        Check("toolbar marks the custom hand selected",
            layout.Buttons.Any(button => button.Id == style.Id && button.Selected), style.Id);

        controller.SelectStyle(style.Id);
        Pump(300);
        Check("custom hand selectable", Preferences.Style.Id == style.Id, Preferences.Style.Id);

        CustomHands.Remove(style);
        controller.RefreshStyles();
        Pump(300);
        Check("custom hand removed", PointerStyle.All.Count == before, PointerStyle.All.Count.ToString());
        Check("custom hand sprite forgotten", HandAssets.Sprite(style.Id) is null, style.Id);
        Check("custom hand file deleted",
            !File.Exists(Path.Combine(CustomHands.Directory, style.Id + ".png")), style.Id);
    }

    // ------------------------------------------------------------------
    // Settings + welcome windows
    // ------------------------------------------------------------------

    private static void CheckWindows()
    {
        using var controller = new OverlayController();
        using var window = new SettingsWindow(controller, _ => true, _ => true, _ => { });
        foreach (var section in Enum.GetValues<SettingsSection>())
        {
            window.ShowSection(section);
            Check($"settings page builds: {section}", true, section.ToString());
        }
        window.ShowMakeHand();
        Check("settings make-hand page builds", true, "make hand");

        using var welcome = new WelcomeWindow();
        Check("welcome marks itself shown", Preferences.WelcomeShown, "welcomeShown");
        Preferences.WelcomeShown = false;
    }

    // ------------------------------------------------------------------

    private static void Check(string name, bool ok, string detail)
    {
        _checks++;
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  ({detail})");
    }

    private static void Pump(int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}
