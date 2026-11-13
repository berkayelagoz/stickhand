using System.Drawing;
using System.Drawing.Drawing2D;
using TinyHand.Logic;

namespace TinyHand;

/// <summary>
/// First-run card (macOS <c>WelcomeWindow</c>). The macOS version asks for Accessibility;
/// Windows needs no permission, so the card is the "you're all set" state: the toggle
/// shortcut, a Start button that turns the pointer on, and a hint about the tray menu.
/// Shown once (<see cref="Preferences.WelcomeShown"/>) and from the tray menu.
/// </summary>
internal sealed class WelcomeWindow : Form
{
    private static readonly Color PageBack = Color.FromArgb(246, 246, 248);
    private static readonly Color Ink = Color.FromArgb(40, 40, 44);
    private static readonly Color Muted = Color.FromArgb(110, 110, 118);
    private static readonly Color Accent = Color.FromArgb(135, 84, 242);

    /// <summary>Start pressed: turn the pointer on.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? OnStart { get; set; }

    public WelcomeWindow()
    {
        Text = "TinyHand";
        ClientSize = new Size(360, 330);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PageBack;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowIcon = false;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 22, 24, 18),
            BackColor = PageBack,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var icon = new PictureBox
        {
            Image = DrawIcon(76),
            Size = new Size(76, 76),
            SizeMode = PictureBoxSizeMode.Zoom,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 14),
        };
        layout.Controls.Add(icon);

        layout.Controls.Add(new Label
        {
            Text = "You're all set",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
            ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 6),
        });
        layout.Controls.Add(new Label
        {
            Text = $"Press {Preferences.ToggleShortcut.Display} any time to start.",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = Muted,
            Margin = new Padding(0, 0, 0, 18),
        });

        var start = new Button
        {
            Text = "Start",
            Size = new Size(180, 36),
            Anchor = AnchorStyles.None,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = new Font(Font, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 14),
        };
        start.FlatAppearance.BorderSize = 0;
        start.Click += (_, _) =>
        {
            Close();
            OnStart?.Invoke();
        };
        layout.Controls.Add(start);

        layout.Controls.Add(new Label
        {
            Text = "Right-click the tray icon for settings, or to make your own hand.",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = Muted,
            Font = new Font(Font.FontFamily, 8.25f),
            TextAlign = ContentAlignment.MiddleCenter,
            MaximumSize = new Size(300, 0),
            Margin = new Padding(0),
        });

        Controls.Add(layout);
        AcceptButton = start;
        Preferences.WelcomeShown = true;
    }

    private static Bitmap DrawIcon(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new SolidBrush(Color.FromArgb(255, 252, 163, 28));
        g.FillEllipse(bg, 1, 1, size - 2, size - 2);
        using var pen = new Pen(Color.FromArgb(255, 24, 24, 27), size * 0.16f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        float x1 = size * 0.34f, x2 = size * 0.5f, x3 = size * 0.66f;
        g.DrawLine(pen, x1, size * 0.7f, x1, size * 0.38f);
        g.DrawLine(pen, x2, size * 0.7f, x2, size * 0.25f);
        g.DrawLine(pen, x3, size * 0.7f, x3, size * 0.38f);
        return bitmap;
    }
}
