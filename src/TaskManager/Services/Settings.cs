using System.Text.Json;
using TaskManager.Data;

namespace TaskManager.Services;

public sealed class Settings
{
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 800;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
    public double SidebarWidth { get; set; } = 220;
    /// <summary>Width of the issue list while an issue is open (the issue itself takes the rest).</summary>
    public double ListWidth { get; set; } = 520;
    /// <summary>Expanded folders of each knowledge base source (key: source id).</summary>
    public Dictionary<string, List<string>> KbExpanded { get; set; } = [];
    public bool BoardView { get; set; }
    public SortMode Sort { get; set; } = SortMode.Priority;
    public bool HideClosed { get; set; }
    public string? Nav { get; set; }
    public double ReadingSize { get; set; } = 16;
    public bool ReadingSerif { get; set; }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options) ?? new Settings();
        }
        catch (Exception)
        {
            // A broken settings file must never prevent the app from starting.
        }
        return new Settings();
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, Options)); }
        catch (IOException) { }
    }
}
