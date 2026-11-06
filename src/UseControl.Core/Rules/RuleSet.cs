using UseControl.Core.Configuration;
using UseControl.Core.Paths;

namespace UseControl.Core.Rules;

/// <summary>Why a path is in the rule set: a category from the config, or one of the built-in sources.</summary>
public enum RuleSource
{
    Writable,
    Games,
    Vpn,
    Tools,
    Other,

    /// <summary>The account's own profile (Downloads, Desktop, AppData).</summary>
    Profile,

    /// <summary>Found on the PC by the automatic checks.</summary>
    Automatic,

    /// <summary>Windows and Program Files.</summary>
    System,

    /// <summary>A folder the administrator allowed.</summary>
    Allowed,
}

/// <summary>A path rule of the Software Restriction Policy.</summary>
public sealed record PathRule(string Path, RuleSource Source, string? Reason = null);

/// <summary>A place found on the PC that should be blocked, with the reason.</summary>
public sealed record AutoPath(string Path, string Reason);

/// <summary>
/// The rules for one account. Windows uses the most specific matching path, so a blocked folder
/// inside an allowed one is blocked, and an allowed folder inside a blocked one runs.
/// </summary>
public sealed record RuleSet(IReadOnlyList<PathRule> Blocked, IReadOnlyList<PathRule> Allowed, IReadOnlyList<string> BlockedNames, IReadOnlyList<FileHashEntry> BlockedFiles);

public static class RuleSetBuilder
{
    /// <param name="config">The account's preset.</param>
    /// <param name="profilePath">The account's profile folder, blocked as a whole.</param>
    /// <param name="autoPaths">Places found on this PC (writable folders, other drive letters...).</param>
    /// <param name="systemAllowed">Windows and Program Files, allowed under a blocked drive root.</param>
    /// <param name="gamesAllowed">Games time: game folders become allowed and game names and files are not blocked.</param>
    public static RuleSet Build(Preset config, string? profilePath, IEnumerable<AutoPath> autoPaths, IEnumerable<string> systemAllowed, bool gamesAllowed)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(autoPaths);
        ArgumentNullException.ThrowIfNull(systemAllowed);

        var blocked = new List<PathRule>();
        var allowed = new List<PathRule>();
        if (!string.IsNullOrEmpty(profilePath)) blocked.Add(new PathRule(PathRules.Normalize(profilePath), RuleSource.Profile));
        foreach (var a in autoPaths) blocked.Add(new PathRule(PathRules.Normalize(a.Path), RuleSource.Automatic, a.Reason));
        foreach (var e in config.BlockedPaths)
        {
            var r = new PathRule(PathRules.Normalize(e.Path), ToSource(e.Category), e.Note);
            if (gamesAllowed && e.Category == RuleCategory.Games) allowed.Add(r); else blocked.Add(r);
        }
        foreach (var s in systemAllowed) allowed.Add(new PathRule(PathRules.Normalize(s), RuleSource.System));
        foreach (var e in config.AllowedPaths) allowed.Add(new PathRule(PathRules.Normalize(e.Path), RuleSource.Allowed, e.Note));

        // During games time nothing inside an allowed game folder stays blocked (for example a game
        // folder that ordinary users can write to, found automatically).
        var gameFolders = allowed.Where(r => r.Source == RuleSource.Games).Select(r => r.Path).ToList();
        var outBlocked = new List<PathRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in blocked)
        {
            if (PathRules.IsUnderAny(b.Path, gameFolders)) continue;
            if (seen.Add(b.Path)) outBlocked.Add(b);
        }

        // The same path both blocked and allowed: blocking wins.
        var outAllowed = new List<PathRule>();
        var seenAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in allowed)
        {
            if (seen.Contains(a.Path)) continue;
            if (seenAllowed.Add(a.Path)) outAllowed.Add(a);
        }

        var names = config.BlockedNames
            .Where(n => !(gamesAllowed && n.Category == RuleCategory.Games))
            .Select(n => n.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = config.BlockedFiles.Where(f => !(gamesAllowed && f.Category == RuleCategory.Games)).ToList();

        return new RuleSet(outBlocked, outAllowed, names, files);
    }

    public static RuleSource ToSource(RuleCategory c) => c switch
    {
        RuleCategory.Writable => RuleSource.Writable,
        RuleCategory.Games => RuleSource.Games,
        RuleCategory.Vpn => RuleSource.Vpn,
        RuleCategory.Tools => RuleSource.Tools,
        _ => RuleSource.Other,
    };
}
