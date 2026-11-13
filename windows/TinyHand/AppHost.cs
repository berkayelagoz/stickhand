using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using TinyHand.Interop;

namespace TinyHand;

/// <summary>
/// WinForms tray host (M1): NotifyIcon with a toggle menu, Ctrl+Alt+H global hotkey,
/// all overlay work delegated to OverlayController.
/// </summary>
internal sealed class AppHost : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly OverlayController _controller = new();
    private readonly HotkeyHost _hotkeys;
    private readonly Icon _icon;
    private readonly SynchronizationContext _ui;

    public AppHost()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        if (!Debugger.IsAttached)
        {
            // The overlay is noisy while a debugger holds breakpoints; normal runs use the tray.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        }

        _icon = CreateTrayIcon();
        _hotkeys = new HotkeyHost();
        _hotkeys.HotkeyPressed += OnHotkey;
        if (!_hotkeys.RegisterToggle())
            Debug.WriteLine($"RegisterHotKey toggle failed: {Marshal.GetLastWin32Error()}");
        if (!_hotkeys.RegisterQuit())
            Debug.WriteLine($"RegisterHotKey quit failed: {Marshal.GetLastWin32Error()}");
        if (!_hotkeys.RegisterClear())
            Debug.WriteLine($"RegisterHotKey clear failed: {Marshal.GetLastWin32Error()}");
        if (!_hotkeys.RegisterFade())
            Debug.WriteLine($"RegisterHotKey fade failed: {Marshal.GetLastWin32Error()}");
        if (!_hotkeys.RegisterSmooth())
            Debug.WriteLine($"RegisterHotKey smooth failed: {Marshal.GetLastWin32Error()}");

        _toggleItem = new ToolStripMenuItem("Active (Ctrl+Alt+H)") { Checked = false, CheckOnClick = true };
        _toggleItem.Click += (_, _) => Toggle();

        var exitItem = new ToolStripMenuItem("Exit TinyHand (Ctrl+Alt+Q)");
        exitItem.Click += (_, _) => Exit();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "TinyHand (M1 prototype)",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _controller.ActiveChanged += active =>
            _ui.Post(_ =>
            {
                _toggleItem.Checked = active;
                _trayIcon.Text = active ? "TinyHand — active" : "TinyHand (M1 prototype)";
            }, null);

        // Open on launch so the prototype is immediately testable.
        _controller.SetActive(true);
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

    private void Exit()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _hotkeys.Dispose();
        _controller.Dispose();
        _icon.Dispose();
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
            _controller.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
