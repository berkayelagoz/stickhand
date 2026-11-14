using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using StickHand.Interop;
using StickHand.Logic;

namespace StickHand;

/// <summary>
/// WinForms tray host: NotifyIcon with the pointer toggle, settings, welcome and exit, the
/// global hotkeys, the automation surface (protocol commands, state.json) and the single
/// instance pipe. All overlay work stays in <see cref="OverlayController"/>.
/// </summary>
internal sealed class AppHost : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly OverlayController _controller = new();
    private readonly HotkeyHost _hotkeys;
    private readonly Icon _icon;
    private readonly SynchronizationContext _ui;
    private readonly Automation _automation;
    private readonly SingleInstance _single;
    private SettingsWindow? _settings;
    private WelcomeWindow? _welcome;

    public AppHost(SingleInstance single)
    {
        _single = single;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        if (!Debugger.IsAttached)
        {
            // The overlay is noisy while a debugger holds breakpoints; normal runs use the tray.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        }

        _icon = CreateTrayIcon();
        _hotkeys = new HotkeyHost();
        _hotkeys.HotkeyPressed += OnHotkey;
        if (!_hotkeys.RegisterToggle(Preferences.ToggleShortcut))
            Debug.WriteLine($"RegisterHotKey toggle failed: {Marshal.GetLastWin32Error()}");
        if (!_hotkeys.RegisterQuit())
            Debug.WriteLine($"RegisterHotKey quit failed: {Marshal.GetLastWin32Error()}");
        if (!_hotkeys.RegisterClear(Preferences.ClearShortcut))
            Debug.WriteLine($"RegisterHotKey clear failed: {Marshal.GetLastWin32Error()}");
        if (!_hotkeys.RegisterFade())
            Debug.WriteLine($"RegisterHotKey fade failed: {Marshal.GetLastWin32Error()}");
        if (!_hotkeys.RegisterSmooth())
            Debug.WriteLine($"RegisterHotKey smooth failed: {Marshal.GetLastWin32Error()}");

        _toggleItem = new ToolStripMenuItem("Active") { Checked = false, CheckOnClick = true };
        _toggleItem.Click += (_, _) => Toggle();

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => ShowSettings();

        var welcomeItem = new ToolStripMenuItem("Welcome…");
        welcomeItem.Click += (_, _) => ShowWelcome();

        var makeHandItem = new ToolStripMenuItem("Make your own hand…");
        makeHandItem.Click += (_, _) => ShowMakeHand();

        var exitItem = new ToolStripMenuItem("Exit StickHand");
        exitItem.Click += (_, _) => Exit();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(welcomeItem);
        menu.Items.Add(makeHandItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "StickHand",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // macOS: a click on the menu bar icon turns the pointer on or off.
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Toggle();
        };

        _controller.ActiveChanged += active => _ui.Post(_ => UpdateTray(active), null);
        _controller.StyleChanged += () => _ui.Post(_ => UpdateTray(_controller.IsActive), null);
        CustomHands.Changed += OnCustomHandsChanged;

        _automation = new Automation(_controller);
        _single.CommandReceived += command => _ui.Post(_ => HandleForwardedCommand(command), null);

        // Decode the toolbar thumbnails off the UI thread (macOS preloads them the same way).
        HandAssets.PreloadThumbnails(
            PointerStyle.All.Where(style => style.AssetName is not null).Select(style => style.Id));

        _automation.WriteState();

        // Open on launch so the pointer is immediately usable (macOS starts active).
        _controller.SetActive(true);

        if (!Preferences.WelcomeShown)
            ShowWelcome();
    }

    private void OnCustomHandsChanged()
    {
        _controller.RefreshStyles();
        _ui.Post(_ => UpdateTray(_controller.IsActive), null);
    }

    private void HandleForwardedCommand(string command)
    {
        if (command.StartsWith(Automation.Scheme + "://", StringComparison.OrdinalIgnoreCase))
        {
            _automation.Handle(command);
            return;
        }
        Debug.WriteLine($"forwarded command ignored: {command}");
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyHost.ToggleId:
                Toggle();
                break;
            case HotkeyHost.QuitId:
                // Full quit from any state, including while the overlay holds the cursor.
                Exit();
                break;
            case HotkeyHost.ClearId:
                // macOS ⌃⌥X: wipe the ink, stay in drawing mode.
                _controller.ClearInk();
                break;
            case HotkeyHost.FadeId:
                // Toggle the per-stroke auto-fade; off keeps the ink on screen.
                _controller.ToggleFade();
                break;
            case HotkeyHost.SmoothId:
                // Cycle the pointer smoothing strength (off → light → … → max).
                _controller.ToggleSmoothing();
                break;
        }
    }

    private void Toggle() => _controller.SetActive(!_controller.ActiveRequested);

    private void UpdateTray(bool active)
    {
        _toggleItem.Checked = active;
        _toggleItem.Text = $"Active ({Preferences.ToggleShortcut.Display})";
        _trayIcon.Text = active ? "StickHand — active" : "StickHand";
    }

    private void ShowSettings()
    {
        // macOS openSettings() exits the overlay first: the overlay hit-tests the whole
        // screen while drawing, so a settings window underneath it could not be clicked.
        _controller.SetActive(false);
        if (_settings is null || _settings.IsDisposed)
        {
            _settings = new SettingsWindow(_controller, ApplyToggleShortcut, ApplyClearShortcut, OnRecording);
            _settings.FormClosed += (_, _) => _settings = null;
        }
        _settings.Show();
        _settings.Activate();
    }

    private void ShowMakeHand()
    {
        ShowSettings();
        _settings?.ShowMakeHand();
    }

    private void ShowWelcome()
    {
        _controller.SetActive(false);
        if (_welcome is { IsDisposed: false })
        {
            _welcome.Show();
            _welcome.Activate();
            return;
        }
        _welcome = new WelcomeWindow();
        _welcome.OnStart = () => _controller.SetActive(true);
        _welcome.FormClosed += (_, _) => _welcome = null;
        _welcome.Show();
        _welcome.Activate();
    }

    /// <summary>Settings window: record a new toggle chord and register it immediately.</summary>
    private bool ApplyToggleShortcut(Hotkey shortcut)
    {
        if (!_hotkeys.RegisterToggle(shortcut))
        {
            // Register() released the old chord first: put it back so a rejected recording
            // does not leave the app without a toggle shortcut.
            _hotkeys.RegisterToggle(Preferences.ToggleShortcut);
            return false;
        }
        Preferences.ToggleShortcut = shortcut;
        UpdateTray(_controller.IsActive);
        return true;
    }

    private bool ApplyClearShortcut(Hotkey shortcut)
    {
        if (!_hotkeys.RegisterClear(shortcut))
        {
            _hotkeys.RegisterClear(Preferences.ClearShortcut);
            return false;
        }
        Preferences.ClearShortcut = shortcut;
        return true;
    }

    /// <summary>While the settings window records a chord, release the global hotkeys so the
    /// key press reaches the recorder (macOS unregisters them for the same reason).</summary>
    private void OnRecording(bool recording)
    {
        if (recording)
        {
            _hotkeys.UnregisterToggle();
            _hotkeys.UnregisterClear();
            return;
        }
        _hotkeys.RegisterToggle(Preferences.ToggleShortcut);
        _hotkeys.RegisterClear(Preferences.ClearShortcut);
    }

    private void Exit()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _hotkeys.Dispose();
        _automation.Dispose();
        _controller.Dispose();
        _icon.Dispose();
        Automation.DeleteState();
        SettingsStore.Disable();
        ExitThread();
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(255, 252, 163, 28));
            g.FillEllipse(bg, 2, 2, 28, 28);
            using var pen = new Pen(Color.FromArgb(255, 24, 24, 27), 5f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            // Stylized hand: three "fingers".
            g.DrawLine(pen, 11, 22, 11, 12);
            g.DrawLine(pen, 16, 22, 16, 8);
            g.DrawLine(pen, 21, 22, 21, 12);
        }
        IntPtr hIcon = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        _ = hIcon; // cloned; original handle intentionally left to the garbage collector
        return icon;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _hotkeys.Dispose();
            _automation.Dispose();
            _controller.Dispose();
            _icon.Dispose();
            Automation.DeleteState();
            SettingsStore.Disable();
        }
        base.Dispose(disposing);
    }
}
