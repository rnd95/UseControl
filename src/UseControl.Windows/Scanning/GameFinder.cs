using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UseControl.Core.Configuration;
using UseControl.Core.Paths;

namespace UseControl.Windows.Scanning;

/// <summary>Finds installed games: Steam libraries, Epic games and the folders of known launchers.</summary>
public static partial class GameFinder
{
    private static readonly string[] KnownLauncherFolders =
    [
        "Steam", "Epic Games", "Riot Games", "Battle.net", "GOG Galaxy", "EA Games", "Electronic Arts", "Ubisoft",
        "Rockstar Games", "Wargaming.net", "Roblox", "Minecraft Launcher", "BlueStacks", "BlueStacks_nxt",
    ];

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex SteamLibraryPath();

    /// <summary>Game folders found on this PC.</summary>
    public static IReadOnlyList<string> Find(ISystemPaths sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        var found = new List<string>();

        foreach (var root in SteamRoots())
        {
            found.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf)) found.AddRange(ParseSteamLibraries(File.ReadAllText(vdf)));
        }

        var epic = Path.Combine(sp.ProgramData, @"Epic\EpicGamesLauncher\Data\Manifests");
        if (Directory.Exists(epic))
        {
            foreach (var item in Directory.EnumerateFiles(epic, "*.item"))
            {
                var loc = ParseEpicInstallLocation(SafeRead(item));
                if (loc is not null) found.Add(loc);
            }
        }

        foreach (var pf in sp.ProgramFilesDirectories)
            foreach (var n in KnownLauncherFolders) found.Add(Path.Combine(pf, n));

        return
        [
            .. found
                .Select(p => PathRules.TryNormalize(p, out var n) ? n : null).OfType<string>()
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The found folders that a preset does not list as games yet.</summary>
    public static IReadOnlyList<string> NotListed(IEnumerable<string> found, Preset preset)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(preset);
        var games = preset.BlockedPaths.Where(e => e.Category == RuleCategory.Games)
            .Select(e => PathRules.TryNormalize(e.Path, out var p) ? p : null).OfType<string>().ToList();
        return [.. found.Where(p => !PathRules.IsUnderAny(p, games))];
    }

    /// <summary>The library folders listed in Steam's libraryfolders.vdf.</summary>
    public static IReadOnlyList<string> ParseSteamLibraries(string vdf) =>
        [.. SteamLibraryPath().Matches(vdf ?? "").Select(m => m.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal))];

    /// <summary>InstallLocation from an Epic Games Launcher manifest (.item, JSON).</summary>
    public static string? ParseEpicInstallLocation(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("InstallLocation", out var p) ? p.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        foreach (var k in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(k);
            if (key?.GetValue("InstallPath") is string p && p.Length > 0) yield return p;
        }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
