using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using StickHand.Logic;

namespace StickHand;

internal enum SettingsSection
{
    General,
    Pointers,
    Appearance,
    Drawing,
    Sound,
    About,
}

/// <summary>
/// Settings window (macOS <c>SettingsWindow</c> / <c>SettingsView</c> port). Sections follow
/// the macOS sidebar: General (shortcuts, interact key, double Shift), Pointers (style picker,
/// custom hands), Appearance (size, shadow), Drawing (ink colour, fade, smoothing), Sound
/// (tap sound picker with preview) and About (version, links, reset).
///
/// Every control writes <see cref="Preferences"/> directly, so the overlay picks the change up
/// on its next frame; the window only asks the controller to repaint.
/// </summary>
internal sealed class SettingsWindow : Form
{
    private static readonly Color PageBack = Color.FromArgb(246, 246, 248);
    private static readonly Color SideBack = Color.FromArgb(238, 238, 242);
    private static readonly Color CardBack = Color.White;
    private static readonly Color Accent = Color.FromArgb(135, 84, 242);
    private static readonly Color Muted = Color.FromArgb(110, 110, 118);
    private static readonly Color Ink = Color.FromArgb(40, 40, 44);

    private readonly OverlayController _controller;
    private readonly Func<Hotkey, bool> _applyToggle;
    private readonly Func<Hotkey, bool> _applyClear;
    private readonly Action<bool> _onRecording;
    private readonly Action? _checkForUpdates;
    private readonly Panel _page = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = PageBack };
    private readonly List<(SettingsSection Section, Button Button)> _nav = new();
    private readonly ToolTip _tips = new();
    private readonly TapSound _preview = new(TapSoundChoice.Thock);

    private SettingsSection _section = SettingsSection.General;
    private bool _makingHand;
    private Label? _shortcutError;

    // Make-a-hand draft (kept across page rebuilds).
    private HandMaker.Result? _draft;
    private string? _draftError;
    private string _handName = "";
    private string _handIdea = "";

    public SettingsWindow(
        OverlayController controller,
        Func<Hotkey, bool> applyToggle,
        Func<Hotkey, bool> applyClear,
        Action<bool> onRecording,
        Action? checkForUpdates = null)
    {
        _controller = controller;
        _applyToggle = applyToggle;
        _applyClear = applyClear;
        _onRecording = onRecording;
        _checkForUpdates = checkForUpdates;

        Text = "StickHand Settings";
        ClientSize = new Size(840, 620);
        MinimumSize = new Size(780, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PageBack;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowIcon = false;

        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 200,
            BackColor = SideBack,
            Padding = new Padding(10, 16, 10, 10),
        };
        foreach (var section in Enum.GetValues<SettingsSection>())
            sidebar.Controls.Add(NavButton(section));
        sidebar.Controls.Add(new Label
        {
            Text = "StickHand",
            Dock = DockStyle.Bottom,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Muted,
            Padding = new Padding(8, 0, 0, 0),
        });

        Controls.Add(_page);
        Controls.Add(sidebar);
        Rebuild();
    }

    public void ShowSection(SettingsSection section)
    {
        _section = section;
        _makingHand = false;
        Rebuild();
    }

    public void ShowMakeHand()
    {
        _section = SettingsSection.Pointers;
        _makingHand = true;
        _draft = null;
        _draftError = null;
        Rebuild();
    }

    private Button NavButton(SettingsSection section)
    {
        var button = new Button
        {
            Text = Title(section),
            Dock = DockStyle.Top,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            BackColor = SideBack,
            ForeColor = Ink,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => ShowSection(section);
        _nav.Add((section, button));
        return button;
    }

    private static string Title(SettingsSection section) => section switch
    {
        SettingsSection.General => "General",
        SettingsSection.Pointers => "Pointers",
        SettingsSection.Appearance => "Appearance",
        SettingsSection.Drawing => "Drawing",
        SettingsSection.Sound => "Sound",
        _ => "About",
    };

    private void Rebuild()
    {
        foreach (var (section, button) in _nav)
        {
            bool selected = section == _section;
            button.BackColor = selected ? CardBack : SideBack;
            button.Font = new Font(Font, selected ? FontStyle.Bold : FontStyle.Regular);
        }

        _page.SuspendLayout();
        foreach (Control control in _page.Controls)
        {
            DisposeImages(control);
            control.Dispose();
        }
        _page.Controls.Clear();

        var content = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(26, 18, 26, 26),
            BackColor = PageBack,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        if (_makingHand)
        {
            content.Controls.Add(ActionButton("‹ Pointers", () => ShowSection(SettingsSection.Pointers)));
            AddTitle(content, "Make your own hand");
            BuildMakeHand(content);
        }
        else
        {
            AddTitle(content, Title(_section));
            switch (_section)
            {
                case SettingsSection.General:
                    BuildGeneral(content);
                    break;
                case SettingsSection.Pointers:
                    BuildPointers(content);
                    break;
                case SettingsSection.Appearance:
                    BuildAppearance(content);
                    break;
                case SettingsSection.Drawing:
                    BuildDrawing(content);
                    break;
                case SettingsSection.Sound:
                    BuildSound(content);
                    break;
                case SettingsSection.About:
                    BuildAbout(content);
                    break;
            }
        }

        _page.Controls.Add(content);
        _page.ResumeLayout();
    }

    private void RefreshPage() => BeginInvoke(new Action(Rebuild));

    /// <summary>WinForms does not dispose a control's Image; every rebuild creates fresh
    /// thumbnails/swatches, so release them with the controls they belong to.</summary>
    private static void DisposeImages(Control control)
    {
        switch (control)
        {
            case Button { Image: { } image }:
                image.Dispose();
                break;
            case PictureBox { Image: { } image }:
                image.Dispose();
                break;
        }
        foreach (Control child in control.Controls) DisposeImages(child);
    }

    // ------------------------------------------------------------------
    // Layout helpers
    // ------------------------------------------------------------------

    private void AddTitle(TableLayoutPanel content, string text) => content.Controls.Add(new Label
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
        ForeColor = Ink,
        Margin = new Padding(2, 4, 0, 12),
    });

    private void AddCard(TableLayoutPanel content, string? title, params Control[] rows)
    {
        if (title is not null)
            content.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = Muted,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(4, 10, 0, 4),
            });

        var card = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = CardBack,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(2),
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in rows) card.Controls.Add(row);
        content.Controls.Add(card);
    }

    private Control Row(string title, string? detail, Control trailing)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var text = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        text.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0), ForeColor = Ink });
        if (!string.IsNullOrEmpty(detail))
            text.Controls.Add(new Label
            {
                Text = detail,
                AutoSize = true,
                MaximumSize = new Size(440, 0),
                ForeColor = Muted,
                Font = new Font(Font.FontFamily, 8.25f),
                Margin = new Padding(0, 2, 0, 0),
            });
        row.Controls.Add(text, 0, 0);

        trailing.Anchor = AnchorStyles.Right;
        trailing.Margin = new Padding(12, 0, 0, 0);
        row.Controls.Add(trailing, 1, 0);
        return row;
    }

    private CheckBox Toggle(bool value, Action<bool> set)
    {
        var box = new CheckBox { Checked = value, AutoSize = true, Cursor = Cursors.Hand };
        box.CheckedChanged += (_, _) => set(box.Checked);
        return box;
    }

    private ComboBox Combo((string Label, object Value)[] items, object selected, Action<object> set)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 180,
            FlatStyle = FlatStyle.System,
        };
        foreach (var item in items) combo.Items.Add(new ComboItem(item.Label, item.Value));
        int index = Array.FindIndex(items, item => Equals(item.Value, selected));
        combo.SelectedIndex = Math.Max(0, index);
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboItem item) set(item.Value);
        };
        return combo;
    }

    private sealed record ComboItem(string Label, object Value)
    {
        public override string ToString() => Label;
    }

    private static Button ActionButton(string text, Action click, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Color.FromArgb(238, 238, 242),
            ForeColor = primary ? Color.White : Ink,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 8, 0),
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => click();
        return button;
    }

    private static FlowLayoutPanel Buttons(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    // ------------------------------------------------------------------
    // General
    // ------------------------------------------------------------------

    private void BuildGeneral(TableLayoutPanel content)
    {
        _shortcutError = new Label
        {
            AutoSize = true,
            ForeColor = Color.Firebrick,
            Margin = new Padding(4, 0, 0, 6),
            Visible = false,
        };

        AddCard(content, "Shortcuts",
            Row("Turn the pointer on or off", null,
                new HotkeyRecorder(Preferences.ToggleShortcut, _applyToggle, _onRecording, ShowShortcutError)),
            Row("Clear the drawing", null,
                new HotkeyRecorder(Preferences.ClearShortcut, _applyClear, _onRecording, ShowShortcutError)));
        content.Controls.Add(_shortcutError);

        AddCard(content, "Clicking for real",
            Row("Hold to use the app", "Clicks, drags and scrolls reach the app.",
                Combo(
                [
                    ("Alt", InteractKey.Alt),
                    ("Ctrl", InteractKey.Ctrl),
                    ("Shift", InteractKey.Shift),
                    ("Off", InteractKey.Off),
                ], Preferences.InteractKey, value =>
                {
                    Preferences.InteractKey = (InteractKey)value;
                    RefreshPage();
                })),
            Row("While holding",
                Preferences.InteractKeepsHand
                    ? "The hand stays, in menus too."
                    : "The normal cursor comes back, in menus too.",
                Combo(
                [
                    ("Show the cursor", false),
                    ("Keep the hand", true),
                ], Preferences.InteractKeepsHand, value =>
                {
                    Preferences.InteractKeepsHand = (bool)value;
                    RefreshPage();
                })),
            Row("Double-tap Shift to click through",
                "Clicks reach the app and the hand stays. Double-tap again for taps.",
                Toggle(Preferences.DoubleShiftClickThrough, value => Preferences.DoubleShiftClickThrough = value)));
    }

    private void ShowShortcutError(string? message)
    {
        if (_shortcutError is null) return;
        _shortcutError.Text = message ?? "";
        _shortcutError.Visible = !string.IsNullOrEmpty(message);
    }

    // ------------------------------------------------------------------
    // Pointers
    // ------------------------------------------------------------------

    private void BuildPointers(TableLayoutPanel content)
    {
        var grid = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(8),
        };
        foreach (var style in PointerStyle.All) grid.Controls.Add(StyleButton(style));
        AddCard(content, "In your toolbar", grid);

        var hands = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(8),
        };
        foreach (var hand in CustomHands.All) hands.Controls.Add(CustomHandChip(hand));
        hands.Controls.Add(ActionButton("Make your own hand…", ShowMakeHand, primary: true));
        AddCard(content, "Your hands", hands);
    }

    private Button StyleButton(PointerStyle style)
    {
        bool selected = style.Id == Preferences.Style.Id;
        var button = new Button
        {
            Size = new Size(48, 48),
            Margin = new Padding(3),
            FlatStyle = FlatStyle.Flat,
            BackColor = CardBack,
            Cursor = Cursors.Hand,
            Tag = style.Id,
        };
        button.FlatAppearance.BorderSize = selected ? 2 : 1;
        button.FlatAppearance.BorderColor = selected ? Accent : Color.FromArgb(224, 224, 228);

        Bitmap? thumb = style.IsLaser ? LaserThumbnail() : HandAssets.Thumbnail(style.Id);
        if (thumb is not null) button.Image = new Bitmap(thumb, new Size(36, 36));
        _tips.SetToolTip(button, style.Title);
        button.Click += (_, _) =>
        {
            _controller.SelectStyle(style.Id);
            RefreshPage();
        };
        return button;
    }

    private static Bitmap LaserThumbnail()
    {
        var bitmap = new Bitmap(36, 36);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var glow = new SolidBrush(Color.FromArgb(70, 255, 41, 31));
        using var dot = new SolidBrush(Color.FromArgb(255, 255, 41, 31));
        g.FillEllipse(glow, 2, 2, 32, 32);
        g.FillEllipse(dot, 12, 12, 12, 12);
        return bitmap;
    }

    private Control CustomHandChip(CustomHand hand)
    {
        var chip = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Color.FromArgb(246, 246, 248),
            Margin = new Padding(3),
            Padding = new Padding(6, 4, 4, 4),
        };
        var thumb = HandAssets.Thumbnail(hand.Id);
        if (thumb is not null)
            chip.Controls.Add(new PictureBox
            {
                Image = new Bitmap(thumb, new Size(24, 24)),
                Size = new Size(24, 24),
                SizeMode = PictureBoxSizeMode.Zoom,
                Margin = new Padding(0, 2, 6, 0),
            });
        chip.Controls.Add(new Label
        {
            Text = hand.Name,
            AutoSize = true,
            ForeColor = Ink,
            Margin = new Padding(0, 5, 6, 0),
        });
        var remove = new Button
        {
            Text = "×",
            Size = new Size(20, 20),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Muted,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 0, 0),
        };
        remove.FlatAppearance.BorderSize = 0;
        _tips.SetToolTip(remove, $"Delete {hand.Name}");
        remove.Click += (_, _) =>
        {
            var style = PointerStyle.ById(hand.Id);
            if (Preferences.Style.Id == hand.Id) _controller.SelectStyle(PointerStyle.Default.Id);
            CustomHands.Remove(style);
            _controller.RefreshStyles();
            RefreshPage();
        };
        chip.Controls.Add(remove);
        return chip;
    }

    // ------------------------------------------------------------------
    // Appearance
    // ------------------------------------------------------------------

    private void BuildAppearance(TableLayoutPanel content)
    {
        var sizeLabel = new Label
        {
            Text = $"{Preferences.PointerSize * 100:0}% of the screen height",
            AutoSize = true,
            ForeColor = Muted,
            Font = new Font(Font.FontFamily, 8.25f),
            Margin = new Padding(0, 2, 0, 0),
        };
        var slider = new TrackBar
        {
            Minimum = 8,
            Maximum = 20,
            TickFrequency = 2,
            Value = (int)Math.Round(Preferences.PointerSize * 100),
            Width = 180,
            AutoSize = false,
            Height = 30,
            Margin = new Padding(0),
        };
        slider.ValueChanged += (_, _) =>
        {
            Preferences.PointerSize = slider.Value / 100f;
            sizeLabel.Text = $"{slider.Value}% of the screen height";
            _controller.RefreshStyles();
        };

        var sizeRow = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        sizeRow.Controls.Add(slider);
        sizeRow.Controls.Add(sizeLabel);

        AddCard(content, "Pointer",
            Row("Size", null, sizeRow),
            Row("Shadow under the pointer", null,
                Toggle(Preferences.ShowShadow, value =>
                {
                    Preferences.ShowShadow = value;
                    _controller.RefreshStyles();
                })));
    }

    // ------------------------------------------------------------------
    // Drawing
    // ------------------------------------------------------------------

    private void BuildDrawing(TableLayoutPanel content)
    {
        var swatches = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };
        foreach (var choice in Enum.GetValues<InkChoice>()) swatches.Controls.Add(InkSwatch(choice));

        AddCard(content, "Ink",
            Row("Ink colour",
                Preferences.InkChoice == InkChoice.Match
                    ? "Matches each pointer."
                    : $"All pointers draw in {Preferences.InkChoice.ToString().ToLowerInvariant()}.",
                swatches),
            Row("Lines fade", $"Clear them any time with {Preferences.ClearShortcut.Display}.",
                Combo(
                [
                    ("After 3 seconds", 3.0),
                    ("After 6 seconds", 6.0),
                    ("After 10 seconds", 10.0),
                    ("Keep until cleared", 0.0),
                ], Preferences.FadeSeconds, value =>
                {
                    Preferences.FadeSeconds = (double)value;
                    _controller.RefreshStyles();
                })),
            Row("Smoothing", "How much the pointer's jitter is filtered while drawing.",
                Combo(
                [
                    ("Off", SmoothingLevel.Off),
                    ("Light", SmoothingLevel.Light),
                    ("Medium", SmoothingLevel.Medium),
                    ("Strong", SmoothingLevel.Strong),
                    ("Max", SmoothingLevel.Max),
                ], Preferences.SmoothingLevel, value => Preferences.SmoothingLevel = (SmoothingLevel)value)));
    }

    private Button InkSwatch(InkChoice choice)
    {
        bool selected = Preferences.InkChoice == choice;
        var button = new Button
        {
            Size = new Size(30, 30),
            Margin = new Padding(2),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = selected ? 2 : 1;
        button.FlatAppearance.BorderColor = selected ? Accent : Color.FromArgb(214, 214, 220);
        var color = InkPalette.Fixed(choice);
        button.Image = color is null ? MatchSwatch() : SolidSwatch(color.Value);
        _tips.SetToolTip(button, choice == InkChoice.Match ? "Match the pointer" : choice.ToString());
        button.Click += (_, _) =>
        {
            Preferences.InkChoice = choice;
            _controller.RefreshStyles();
            RefreshPage();
        };
        return button;
    }

    private static Bitmap SolidSwatch(Color color)
    {
        var bitmap = new Bitmap(20, 20);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 1, 1, 18, 18);
        return bitmap;
    }

    private static Bitmap MatchSwatch()
    {
        var bitmap = new Bitmap(20, 20);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, 20, 20), Color.Red, Color.Purple, 45f);
        g.FillEllipse(brush, 1, 1, 18, 18);
        return bitmap;
    }

    // ------------------------------------------------------------------
    // Sound
    // ------------------------------------------------------------------

    private void BuildSound(TableLayoutPanel content)
    {
        var tiles = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(8),
        };
        tiles.Controls.Add(SoundTile("Off", !Preferences.SoundOn, () =>
        {
            // Let the controller own the preference: setting it here first made SetSound
            // return early and left the tap sound enabled.
            _controller.SetSound(false);
            RefreshPage();
        }));
        foreach (var choice in Enum.GetValues<TapSoundChoice>())
        {
            var captured = choice;
            tiles.Controls.Add(SoundTile(choice.ToString(), Preferences.SoundOn && Preferences.TapSound == choice, () =>
            {
                Preferences.TapSound = captured;
                _controller.SetSound(true);
                _preview.Choice = captured;
                _preview.Enabled = true;
                _preview.Play();
                RefreshPage();
            }));
        }
        AddCard(content, "Tap sound", tiles);
        content.Controls.Add(new Label
        {
            Text = "Click a sound to hear it. It plays each time the hand taps.",
            AutoSize = true,
            ForeColor = Muted,
            Font = new Font(Font.FontFamily, 8.25f),
            Margin = new Padding(4, 0, 0, 0),
        });
    }

    private Button SoundTile(string title, bool selected, Action click)
    {
        var button = new Button
        {
            Text = title,
            Size = new Size(112, 54),
            Margin = new Padding(4),
            FlatStyle = FlatStyle.Flat,
            BackColor = selected ? Color.FromArgb(240, 236, 255) : Color.FromArgb(248, 248, 250),
            ForeColor = selected ? Accent : Ink,
            Font = new Font(Font, selected ? FontStyle.Bold : FontStyle.Regular),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = selected ? 2 : 1;
        button.FlatAppearance.BorderColor = selected ? Accent : Color.FromArgb(228, 228, 232);
        button.Click += (_, _) => click();
        return button;
    }

    // ------------------------------------------------------------------
    // About
    // ------------------------------------------------------------------

    private void BuildAbout(TableLayoutPanel content)
    {
        string version = Updater.Version;
        content.Controls.Add(new Label
        {
            Text = "StickHand",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
            ForeColor = Ink,
            Margin = new Padding(2, 0, 0, 2),
        });
        content.Controls.Add(new Label
        {
            Text = $"Version {version} — a tiny hand for your PC. Free and open source.",
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(2, 0, 0, 12),
        });

        AddCard(content, null,
            LinkRow("Website", "tinyhand.app", "https://tinyhand.app"),
            LinkRow("Source code", "github.com/berkayelagoz/stickhand", "https://github.com/berkayelagoz/stickhand"),
            LinkRow("License", "MIT", "https://github.com/berkayelagoz/stickhand/blob/main/LICENSE"));

        if (_checkForUpdates is not null)
        {
            content.Controls.Add(ActionButton("Check for updates…", _checkForUpdates));
            content.Controls.Add(new Label
            {
                Text = Updater.IsInstalled
                    ? "StickHand updates itself from GitHub releases."
                    : "This build is not installed, so it cannot update itself.",
                AutoSize = true,
                ForeColor = Muted,
                Font = new Font(Font.FontFamily, 8.25f),
                Margin = new Padding(4, 0, 0, 8),
            });
        }

        content.Controls.Add(ActionButton("Reset all settings…", ResetAll));
    }

    private Control LinkRow(string title, string detail, string url)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Ink, Margin = new Padding(0) }, 0, 0);
        var link = new LinkLabel
        {
            Text = detail,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            LinkColor = Muted,
            ActiveLinkColor = Accent,
            Margin = new Padding(12, 0, 0, 0),
        };
        link.LinkClicked += (_, _) => OpenUrl(url);
        row.Controls.Add(link, 1, 0);
        return row;
    }

    private void ResetAll()
    {
        var answer = MessageBox.Show(this,
            "Shortcuts, pointer, drawing and sound settings go back to how they were at first.",
            "Reset all settings?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;

        Preferences.ResetAll();
        _applyToggle(Preferences.ToggleShortcut);
        _applyClear(Preferences.ClearShortcut);
        _controller.RefreshStyles();
        _controller.SetSound(Preferences.SoundOn);
        ShowShortcutError(null);
        Rebuild();
    }

    // ------------------------------------------------------------------
    // Make your own hand
    // ------------------------------------------------------------------

    private void BuildMakeHand(TableLayoutPanel content)
    {
        var idea = new TextBox
        {
            Width = 420,
            Text = _handIdea,
            PlaceholderText = "e.g. knitted wool in mustard yellow",
            Margin = new Padding(0),
        };
        idea.TextChanged += (_, _) => _handIdea = idea.Text;
        AddCard(content, "1. Describe it",
            Row("What should your hand look like?", null, idea));

        AddCard(content, "2. Make the image",
            Row("Open it in ChatGPT, or copy the prompt for another image tool.",
                "Download the result as a PNG with a transparent background.",
                Buttons(
                    ActionButton("Open in ChatGPT", () => OpenChatGpt(_handIdea), primary: true),
                    ActionButton("Copy prompt", () => CopyPrompt(_handIdea)))));

        if (_draft is not null)
            AddCard(content, "3. Add it", DraftPreview());
        else
            AddCard(content, "3. Add it", DropZone());
    }

    private Control DropZone()
    {
        var zone = new Panel
        {
            Height = 130,
            Dock = DockStyle.Fill,
            AllowDrop = true,
            BackColor = Color.FromArgb(250, 250, 252),
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand,
            Margin = new Padding(8),
        };
        var status = new Label
        {
            Text = _draftError ?? "Drop the PNG here, or click to choose it",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = _draftError is null ? Muted : Color.Firebrick,
            Padding = new Padding(20),
        };
        zone.Controls.Add(status);

        void Load(string path)
        {
            try
            {
                _draft = HandMaker.Make(path);
                _draftError = null;
                _handName = CustomHands.SuggestedName(path);
            }
            catch (HandMaker.Problem problem)
            {
                _draft = null;
                _draftError = problem.Message;
            }
            catch (Exception)
            {
                _draft = null;
                _draftError = "StickHand couldn't read that file.";
            }
            RefreshPage();
        }

        zone.Click += (_, _) => ChooseFile(Load);
        status.Click += (_, _) => ChooseFile(Load);
        zone.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        zone.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                Load(files[0]);
        };
        return zone;
    }

    private void ChooseFile(Action<string> load)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Pick the PNG of your hand",
            Filter = "Images|*.png;*.webp;*.jpg;*.jpeg|All files|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) load(dialog.FileName);
    }

    private Control DraftPreview()
    {
        var preview = new PictureBox
        {
            Size = new Size(144, 180),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(232, 207, 181),
            Margin = new Padding(8, 8, 18, 8),
        };
        if (_draft is not null)
        {
            var crop = new Rectangle(112, 90, 800, 1000);
            var bitmap = new Bitmap(144, 180);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_draft.Image, new Rectangle(0, 0, 144, 180), crop, GraphicsUnit.Pixel);
            }
            preview.Image = bitmap;
        }

        var name = new TextBox
        {
            Width = 220,
            MaxLength = 16,
            Text = _handName,
            Margin = new Padding(0),
        };
        name.TextChanged += (_, _) => _handName = name.Text;

        var side = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        side.Controls.Add(new Label { Text = "Name", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 2) });
        side.Controls.Add(name);
        side.Controls.Add(new Label
        {
            Text = "It should show the back of the hand, like the ones in StickHand. If you see the palm, try another image.",
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            ForeColor = Muted,
            Font = new Font(Font.FontFamily, 8.25f),
            Margin = new Padding(0, 8, 0, 8),
        });
        side.Controls.Add(Buttons(
            ActionButton("Add hand", SaveDraft, primary: true),
            ActionButton("Use another image", () => ChooseFile(path =>
            {
                try
                {
                    _draft = HandMaker.Make(path);
                    _draftError = null;
                    _handName = CustomHands.SuggestedName(path);
                }
                catch (HandMaker.Problem problem)
                {
                    _draft = null;
                    _draftError = problem.Message;
                }
                RefreshPage();
            }))));

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        row.Controls.Add(preview);
        row.Controls.Add(side);
        return row;
    }

    private void SaveDraft()
    {
        if (_draft is null) return;
        try
        {
            var style = CustomHands.Add(_draft.Image, _draft.Ink, _handName);
            _draft.Image.Dispose();
            _draft = null;
            _draftError = null;
            _handIdea = "";
            _makingHand = false;
            _controller.RefreshStyles();
            _controller.SelectStyle(style.Id);
            Rebuild();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"custom hand save failed: {ex.Message}");
            _draftError = "StickHand couldn't save that hand.";
            RefreshPage();
        }
    }

    private void CopyPrompt(string idea)
    {
        try { Clipboard.SetText(HandMaker.PromptFor(idea)); }
        catch (Exception ex) { Debug.WriteLine($"clipboard failed: {ex.Message}"); }
    }

    private static void OpenChatGpt(string idea)
    {
        string url = "https://chatgpt.com/?q=" + Uri.EscapeDataString(HandMaker.PromptFor(idea));
        OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"open url failed: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preview.Dispose();
            _tips.Dispose();
            _draft?.Image.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Click-to-record shortcut field (macOS <c>HotkeyRecorder</c>): the window
    /// unregisters the global chords while recording so the chord reaches this control.</summary>
    private sealed class HotkeyRecorder : Button
    {
        private readonly Func<Hotkey, bool> _apply;
        private readonly Action<bool> _recording;
        private readonly Action<string?> _error;
        private Hotkey _shortcut;
        private bool _active;

        public HotkeyRecorder(Hotkey shortcut, Func<Hotkey, bool> apply, Action<bool> recording,
            Action<string?> error)
        {
            _shortcut = shortcut;
            _apply = apply;
            _recording = recording;
            _error = error;

            Size = new Size(150, 30);
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.FromArgb(244, 244, 247);
            ForeColor = Ink;
            Text = shortcut.Display;
            Cursor = Cursors.Hand;
            FlatAppearance.BorderColor = Color.FromArgb(214, 214, 220);
            Click += (_, _) => Start();
            LostFocus += (_, _) => Stop();
        }

        private void Start()
        {
            if (_active) return;
            _active = true;
            Text = "Press keys…";
            _recording(true);
            _error(null);
            Focus();
        }

        private void Stop()
        {
            if (!_active) return;
            _active = false;
            Text = _shortcut.Display;
            _recording(false);
        }

        protected override bool IsInputKey(Keys keyData) => true;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!_active)
            {
                base.OnKeyDown(e);
                return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;

            if (e.KeyCode == Keys.Escape)
            {
                Stop();
                return;
            }
            if (!Hotkey.TryFromKey(e.KeyCode, e.Modifiers, out var shortcut))
            {
                Text = "Add Ctrl, Alt or Shift";
                return;
            }
            if (_apply(shortcut))
            {
                _shortcut = shortcut;
                _error(null);
                Stop();
            }
            else
            {
                Text = "In use";
                _error($"{shortcut.Display} is already used by another app. Pick a different shortcut.");
            }
        }
    }
}
