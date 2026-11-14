using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StickHand.Logic;

/// <summary>
/// Persisted preference values (macOS <c>UserDefaults</c> equivalent). Every property is
/// nullable so a missing key falls back to the macOS default instead of a zero value.
/// </summary>
public sealed class SettingsData
{
    public double? FadeSeconds { get; set; }
    public string? Style { get; set; }
    public bool? SoundOn { get; set; }
    public string? TapSound { get; set; }
    public string? InteractKey { get; set; }
    public bool? InteractKeepsHand { get; set; }
    public bool? DoubleShiftClickThrough { get; set; }
    public float? PointerSize { get; set; }
    public bool? ShowShadow { get; set; }
    public string? SmoothingLevel { get; set; }
    public string? InkChoice { get; set; }
    public string? ToggleShortcut { get; set; }
    public string? ClearShortcut { get; set; }
    public bool? WelcomeShown { get; set; }
}

/// <summary>
/// Settings persistence for the Windows port: <c>%APPDATA%\StickHand\settings.json</c>
/// (macOS keeps the same values in <c>UserDefaults</c>; the automation contract only pins
/// <c>state.json</c>, this file is the app's own store).
///
/// Writes are debounced on a background thread: the low-level hook and the compositor both
/// change preferences, and neither may block on disk I/O. <see cref="Enable"/> is opt-in so
/// unit tests never touch the real profile; <c>TH_SETTINGS_DIR</c> redirects the directory
/// for harnesses.
/// </summary>
public static class SettingsStore
{
    private const string FileName = "settings.json";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keep the file readable: the default encoder escapes '+' as +, which turns
        // every recorded shortcut into noise.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string? _path;
    private static SettingsData _data = new();
    private static bool _dirty;
    private static volatile bool _quit;
    private static Thread? _saver;

    /// <summary>Directory that holds settings.json, state.json and the custom hands.</summary>
    public static string Directory
    {
        get
        {
            string? overrideDir = Environment.GetEnvironmentVariable("TH_SETTINGS_DIR");
            if (!string.IsNullOrEmpty(overrideDir)) return overrideDir;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "StickHand");
        }
    }

    public static string DefaultPath => Path.Combine(Directory, FileName);

    /// <summary>True once <see cref="Enable"/> has run: preferences are read from and written
    /// to disk. Harnesses and unit tests leave it off unless they opt in.</summary>
    public static bool Enabled => _path is not null;

    /// <summary>Loaded values; empty defaults when persistence is off.</summary>
    public static SettingsData Data
    {
        get { lock (Gate) return _data; }
    }

    /// <summary>Load the settings file and start the debounced writer.</summary>
    public static void Enable(string? path = null)
    {
        lock (Gate)
        {
            _path = path ?? DefaultPath;
            _data = Load(_path);
            _dirty = false;
        }

        if (_saver is not null) return;
        _quit = false;
        _saver = new Thread(SaverLoop)
        {
            Name = "StickHand.Settings",
            IsBackground = true,
        };
        _saver.Start();
    }

    /// <summary>Re-read the file into memory (harness round-trip checks).</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            if (_path is null) return;
            _data = Load(_path);
            _dirty = false;
        }
    }

    /// <summary>Write pending changes now (normal exit, harness assertions).</summary>
    public static void Flush()
    {
        string? path;
        SettingsData data;
        lock (Gate)
        {
            if (_path is null || !_dirty) return;
            path = _path;
            data = _data;
            _dirty = false;
        }
        Write(path, data);
    }

    /// <summary>Stop the writer and flush (app shutdown).</summary>
    public static void Disable()
    {
        Flush();
        _quit = true;
        _saver?.Join(TimeSpan.FromSeconds(2));
        _saver = null;
        lock (Gate) _path = null;
    }

    /// <summary>Mark the in-memory values as changed; the writer persists them shortly.</summary>
    public static void MarkDirty()
    {
        lock (Gate)
        {
            if (_path is null) return;
            _dirty = true;
        }
    }

    /// <summary>Apply a change to the persisted values and schedule a write. Preferences
    /// setters go through here so the file always mirrors the live state.</summary>
    public static void Update(Action<SettingsData> change)
    {
        lock (Gate)
        {
            change(_data);
            if (_path is not null) _dirty = true;
        }
    }

    /// <summary>Replace the whole store (reset all settings).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _data = new SettingsData();
            if (_path is null) return;
            _dirty = true;
        }
        Flush();
    }

    private static void SaverLoop()
    {
        while (!_quit)
        {
            Thread.Sleep(250);
            try { Flush(); }
            catch (Exception ex) { Debug.WriteLine($"settings save failed: {ex.Message}"); }
        }
    }

    private static SettingsData Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new SettingsData();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SettingsData>(json, JsonOptions) ?? new SettingsData();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"settings load failed: {ex.Message}");
            return new SettingsData();
        }
    }

    private static void Write(string path, SettingsData data)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(data, JsonOptions);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"settings write failed: {ex.Message}");
        }
    }
}
