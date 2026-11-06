using System.Globalization;
using System.IO;
using System.Text.Json;
using UseControl.Admin.ViewModels.Infrastructure;

namespace UseControl.Admin.Infrastructure;

/// <summary>Per-administrator preferences of the panel (language and theme), in %LOCALAPPDATA%\UseControl\panel.json.</summary>
public sealed class PanelPreferences
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UseControl", "panel.json");

    public string? Language { get; set; }

    public PanelTheme Theme { get; set; } = PanelTheme.System;

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
        Save(p => p.Language = culture.Name);
    }

    public static void SaveTheme(PanelTheme theme) => Save(p => p.Theme = theme);

    private static void Save(Action<PanelPreferences> change)
    {
        try
        {
            var p = Load();
            change(p);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(p));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember a preference is not worth an error message.
        }
    }
}
