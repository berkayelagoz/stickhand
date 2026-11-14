using System.Drawing;

namespace StickHand;

/// <summary>
/// Small progress window shown while Velopack downloads a release. The download runs on a
/// background thread, so <see cref="SetProgress"/> marshals to the UI thread.
/// </summary>
internal sealed class UpdateProgressForm : Form
{
    private readonly ProgressBar _bar;
    private readonly Label _label;

    public UpdateProgressForm(string version)
    {
        Text = "StickHand";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(400, 104);
        BackColor = Color.White;

        _label = new Label
        {
            Text = $"Downloading StickHand {version}…",
            AutoSize = true,
            Location = new Point(20, 20),
        };
        _bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Location = new Point(20, 52),
            Width = 360,
            Height = 18,
        };
        Controls.Add(_label);
        Controls.Add(_bar);
    }

    /// <summary>0–100 from Velopack's download callback (any thread).</summary>
    public void SetProgress(int percent)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                _bar.Value = Math.Clamp(percent, 0, 100);
                _label.Text = $"Downloading StickHand… {Math.Clamp(percent, 0, 100)}%";
            });
        }
        catch (InvalidOperationException)
        {
            // The window closed while the download was finishing.
        }
    }

    /// <summary>Current bar value (harness: the progress callback reaches the UI thread).</summary>
    internal int Percent => _bar.Value;
}
