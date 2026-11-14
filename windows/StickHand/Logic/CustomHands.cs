using System.Diagnostics;
using System.Drawing;
using System.Text.Json;

namespace StickHand.Logic;

/// <summary>One user-made hand (macOS <c>CustomHand</c>): id, display name and ink colour.</summary>
public sealed record CustomHand(string Id, string Name, uint Ink);

/// <summary>
/// macOS <c>CustomHands</c> port: the user's hands live in
/// <c>%APPDATA%\StickHand\Hands</c> as <c>custom-xxxxxxxx.png</c> plus a <c>hands.json</c>
/// list. The list feeds <see cref="PointerStyle.All"/>, so a new hand appears in the toolbar
/// and in the settings picker without a restart.
/// </summary>
public static class CustomHands
{
    private const string ListFileName = "hands.json";
    private static readonly object Gate = new();
    private static List<CustomHand>? _loaded;

    /// <summary>Raised after a hand is added or removed (toolbar refresh, state.json rewrite).</summary>
    public static event Action? Changed;

    /// <summary>Hands directory; <c>TH_HANDS_DIR</c> redirects it for harnesses and tests.</summary>
    public static string Directory
    {
        get
        {
            string? overrideDir = Environment.GetEnvironmentVariable("TH_HANDS_DIR");
            if (!string.IsNullOrEmpty(overrideDir)) return overrideDir;
            return Path.Combine(SettingsStore.Directory, "Hands");
        }
    }

    private static string ListPath => Path.Combine(Directory, ListFileName);

    public static IReadOnlyList<CustomHand> All
    {
        get
        {
            lock (Gate)
            {
                _loaded ??= Load();
                return _loaded;
            }
        }
    }

    /// <summary>The custom hand with this id, or null for built-in ids.</summary>
    public static CustomHand? Hand(string id)
    {
        if (!id.StartsWith("custom-", StringComparison.Ordinal)) return null;
        return All.FirstOrDefault(hand => hand.Id == id);
    }

    /// <summary>PNG path for a custom hand id (null for built-in ids).</summary>
    public static string? PathFor(string id) =>
        id.StartsWith("custom-", StringComparison.Ordinal) ? Path.Combine(Directory, id + ".png") : null;

    /// <summary>Add a composed hand: write the PNG, append the list entry, notify listeners.</summary>
    public static PointerStyle Add(Bitmap image, uint ink, string name)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        HandMaker.Write(image, Path.Combine(Directory, id + ".png"));

        string trimmed = (name ?? "").Trim();
        var list = All.ToList();
        list.Add(new CustomHand(id, trimmed.Length == 0 ? $"My hand {list.Count + 1}" : Truncate(trimmed, 16), ink));
        Save(list);
        return PointerStyle.ById(id);
    }

    /// <summary>Delete a custom hand's PNG and list entry (macOS <c>CustomHands.remove</c>).</summary>
    public static void Remove(PointerStyle style)
    {
        if (!style.IsCustom) return;
        try
        {
            string? path = PathFor(style.Id);
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"custom hand delete failed: {ex.Message}");
        }
        HandAssets.Forget(style.Id);
        Save(All.Where(hand => hand.Id != style.Id).ToList());
    }

    /// <summary>Drop the cached list so the next read hits disk (tests, external edits).</summary>
    public static void Reload()
    {
        lock (Gate) _loaded = null;
    }

    /// <summary>macOS <c>CustomHands.suggestedName</c>: first two words of the file name,
    /// title-cased, unless it looks like a generated download name.</summary>
    public static string SuggestedName(string path)
    {
        string baseName = Path.GetFileNameWithoutExtension(path)
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();
        string words = string.Join(' ', baseName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
        if (words.Length == 0 || words.Length > 16 || words.StartsWith("chatgpt image", StringComparison.OrdinalIgnoreCase))
            return $"My hand {All.Count + 1}";
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static List<CustomHand> Load()
    {
        try
        {
            if (!File.Exists(ListPath)) return [];
            string json = File.ReadAllText(ListPath);
            return JsonSerializer.Deserialize<List<CustomHand>>(json) ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"custom hands load failed: {ex.Message}");
            return [];
        }
    }

    private static void Save(List<CustomHand> list)
    {
        lock (Gate)
        {
            _loaded = list;
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                string temp = ListPath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, ListPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"custom hands save failed: {ex.Message}");
            }
        }
        Changed?.Invoke();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
