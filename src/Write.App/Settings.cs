using System.IO;
using System.Text.Json;

namespace Write.Desktop;

/// <summary>User preferences, stored in %AppData%\Write\settings.json.</summary>
public sealed class Settings
{
    public List<string> Recent { get; set; } = new();
    public double Zoom { get; set; } = 1.0;
    public bool SpellCheck { get; set; } = true;
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 820;
    public bool Maximized { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Write", "settings.json");

    private static Settings? _current;
    public static Settings Current => _current ??= Load();

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // Unreadable settings are not worth bothering the user with; start fresh.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Preferences are best effort.
        }
    }

    public void AddRecent(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > 10) Recent.RemoveRange(10, Recent.Count - 10);
        Save();
    }
}
