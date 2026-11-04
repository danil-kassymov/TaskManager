using System.Windows.Media;

namespace TaskManager.Markdown;

/// <summary>
/// Typography of issue descriptions and knowledge base entries: one base size from which headings,
/// spacing and code sizes are derived, plus the choice of a sans or serif reading font.
/// </summary>
public static class ReadingStyle
{
    public static readonly double[] Sizes = [14, 15, 16, 17.5, 19, 21, 23];
    public const double DefaultSize = 16;

    static readonly FontFamily SansFont = new("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily SerifFont = new("Georgia, Cambria, Times New Roman");
    public static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas");

    public static double Size { get; private set; } = DefaultSize;
    public static bool Serif { get; private set; }

    /// <summary>Raised after any change; views re-render, settings are saved.</summary>
    public static event Action? Changed;

    public static FontFamily Family => Serif ? SerifFont : SansFont;

    /// <summary>Comfortable line spacing for long-form reading (~1.65 of the font size).</summary>
    public static double LineHeight(double fontSize) => Math.Round(fontSize * 1.65);

    /// <summary>Monospace editor text is a little smaller than the rendered text, as monospace glyphs are wider.</summary>
    public static double EditorSize => Math.Max(12.5, Size - 1.5);

    public static void Set(double size, bool serif)
    {
        size = Sizes.MinBy(s => Math.Abs(s - size));
        if (size == Size && serif == Serif) return;
        Size = size;
        Serif = serif;
        Changed?.Invoke();
    }

    public static void Bigger() => Set(Sizes.FirstOrDefault(s => s > Size, Sizes[^1]), Serif);
    public static void Smaller() => Set(Sizes.LastOrDefault(s => s < Size, Sizes[0]), Serif);
    public static void Reset() => Set(DefaultSize, Serif);
    public static void ToggleSerif() => Set(Size, !Serif);
}
