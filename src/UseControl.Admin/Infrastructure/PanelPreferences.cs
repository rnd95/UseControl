using System.Globalization;
using System.IO;
using System.Text.Json;

namespace UseControl.Admin.Infrastructure;

/// <summary>Per-administrator preferences of the panel (only the language for now), in %LOCALAPPDATA%\UseControl\panel.json.</summary>
public sealed class PanelPreferences
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UseControl", "panel.json");

    public string? Language { get; set; }

    public static PanelPreferences Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<PanelPreferences>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public static void SaveLanguage(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new PanelPreferences { Language = culture.Name }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember the language is not worth an error message.
        }
    }
}
