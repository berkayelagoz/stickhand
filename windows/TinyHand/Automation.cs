using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TinyHand.Logic;

namespace TinyHand;

/// <summary>
/// macOS <c>Automation</c> port: the <c>tinyhand://</c> command grammar, the
/// <c>state.json</c> mirror launchers read, and the protocol registration that makes the
/// links work.
///
/// Contract (shared with the macOS app):
/// <list type="bullet">
/// <item><c>tinyhand://on | off | toggle | clear | style/&lt;id&gt; | sound/on|off|toggle</c></item>
/// <item><c>%APPDATA%\TinyHand\state.json</c>: pid, active, style, soundOn and the full styles
/// list (id, name, type = hand | custom | laser); removed on a normal exit.</item>
/// </list>
/// </summary>
public sealed class Automation : IDisposable
{
    public const string Scheme = "tinyhand";
    private const string ProtocolKey = @"Software\Classes\tinyhand";

    private readonly OverlayController _overlay;
    private readonly Action<bool> _activeHandler;
    private readonly AutoResetEvent _dirty = new(false);
    private readonly Thread _writer;
    private volatile bool _quit;
    private bool _disposed;

    public Automation(OverlayController overlay)
    {
        _overlay = overlay;
        _activeHandler = _ => MarkDirty();
        _overlay.ActiveChanged += _activeHandler;
        _overlay.StyleChanged += MarkDirty;
        _overlay.SoundChanged += MarkDirty;
        CustomHands.Changed += MarkDirty;

        _writer = new Thread(WriterLoop)
        {
            Name = "TinyHand.State",
            IsBackground = true,
        };
        _writer.Start();
    }

    /// <summary><c>%APPDATA%\TinyHand\state.json</c> (macOS: Application Support).</summary>
    public static string StatePath => Path.Combine(SettingsStore.Directory, "state.json");

    /// <summary>Split a <c>tinyhand://</c> URL into its command and optional argument.</summary>
    public static (string Command, string? Argument)? Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        string command = uri.Host.ToLowerInvariant();
        if (command.Length == 0) return null;
        string argument = uri.AbsolutePath.Trim('/');
        return (command, argument.Length == 0 ? null : Uri.UnescapeDataString(argument));
    }

    /// <summary>Run one URL command (macOS <c>Automation.handle</c>). Unknown commands are
    /// logged and ignored, exactly like the macOS app.</summary>
    public void Handle(string url)
    {
        var parsed = Parse(url);
        if (parsed is null)
        {
            Log($"not a tinyhand url: {url}");
            return;
        }

        var (command, argument) = parsed.Value;
        Log($"url command: {command} {argument ?? ""}");
        switch (command)
        {
            case "on":
                _overlay.SetActive(true);
                break;
            case "off":
                _overlay.SetActive(false);
                break;
            case "toggle":
                _overlay.SetActive(!_overlay.ActiveRequested);
                break;
            case "clear":
                _overlay.ClearInk();
                break;
            case "style":
                SelectStyle(argument);
                break;
            case "sound":
                SetSound(argument);
                break;
            default:
                Log($"unknown url command: {command}");
                break;
        }
    }

    private void SelectStyle(string? id)
    {
        if (string.IsNullOrEmpty(id) || !PointerStyle.All.Any(style => style.Id == id))
        {
            Log($"unknown pointer style: {id ?? "none"}");
            return;
        }
        _overlay.SelectStyle(id);
        _overlay.SetActive(true);
    }

    private void SetSound(string? value)
    {
        switch (value)
        {
            case "on":
                _overlay.SetSound(true);
                break;
            case "off":
                _overlay.SetSound(false);
                break;
            case "toggle" or null:
                _overlay.SetSound(!Preferences.SoundOn);
                break;
            default:
                Log($"unknown sound value: {value}");
                break;
        }
    }

    /// <summary>Write the state file now (harness assertions, startup).</summary>
    public void WriteState()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Directory);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("active", _overlay.IsActive);
                writer.WriteNumber("pid", Environment.ProcessId);
                writer.WriteBoolean("soundOn", Preferences.SoundOn);
                writer.WriteString("style", Preferences.Style.Id);
                writer.WriteStartArray("styles");
                foreach (var style in PointerStyle.All)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", style.Id);
                    writer.WriteString("name", style.Title);
                    writer.WriteString("type", style.IsLaser ? "laser" : style.IsCustom ? "custom" : "hand");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            string json = Encoding.UTF8.GetString(buffer.ToArray());
            string temp = StatePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"state write failed: {ex.Message}");
        }
    }

    /// <summary>Remove the state file on a normal exit (contract: a leftover file means a crash).</summary>
    public static void DeleteState()
    {
        try
        {
            if (File.Exists(StatePath)) File.Delete(StatePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"state delete failed: {ex.Message}");
        }
    }

    /// <summary>Register <c>tinyhand://</c> under HKCU (no admin rights needed).</summary>
    public static bool RegisterProtocol(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ProtocolKey);
            key.SetValue(null, "URL:TinyHand Protocol");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exePath}\",0");
            using (var command = key.CreateSubKey(@"shell\open\command"))
                command.SetValue(null, $"\"{exePath}\" \"%1\"");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"protocol registration failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>True when the protocol points at this executable (idempotent startup check).</summary>
    public static bool IsProtocolRegistered(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command");
            if (key?.GetValue(null) is not string command) return false;
            return command.Contains(exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void MarkDirty() => _dirty.Set();

    private void WriterLoop()
    {
        while (!_quit)
        {
            _dirty.WaitOne(500);
            if (_quit) break;
            WriteState();
        }
    }

    private static void Log(string message)
    {
        Debug.WriteLine(message);
        if (Environment.GetEnvironmentVariable("TH_TRACE") == "1")
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "tinyhand-trace.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { /* no trace file */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _quit = true;
        _dirty.Set();
        _writer.Join(TimeSpan.FromSeconds(2));
        _dirty.Dispose();
        _overlay.ActiveChanged -= _activeHandler;
        _overlay.StyleChanged -= MarkDirty;
        _overlay.SoundChanged -= MarkDirty;
        CustomHands.Changed -= MarkDirty;
    }
}
