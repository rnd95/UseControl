using UseControl.Core.Games;
using UseControl.Core.Paths;
using UseControl.Core.Validation;

namespace UseControl.Core.Configuration;

/// <summary>
/// The changes the panel and uc.exe make to a configuration. Each method validates its input and
/// throws <see cref="UcException"/> with a message the user can act on; the config is only changed
/// when the input is valid.
/// </summary>
public static class ConfigEditor
{
    /// <summary>
    /// Restricts an account with a preset. Without a preset name the account gets its own preset, named
    /// after it (an existing one of that name, or a new one with the recommended settings).
    /// </summary>
    public static Preset AddUser(UcConfig c, string user, string? presetName = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var u = (user ?? "").Trim();
        if (!Validators.IsUserName(u)) throw new UcException(ErrorCodes.InvalidUserName, user ?? "");
        if (c.PresetOf(u) is not null) throw new UcException(ErrorCodes.AlreadyInList, u);
        Preset preset;
        if (!string.IsNullOrWhiteSpace(presetName)) preset = c.GetPreset(presetName);
        else preset = c.FindPreset(u) ?? AddPreset(c, u);
        preset.Accounts.Add(u);
        return preset;
    }

    /// <summary>Stops restricting an account. Its preset stays (with its settings) for later.</summary>
    public static void RemoveUser(UcConfig c, string user)
    {
        ArgumentNullException.ThrowIfNull(c);
        var u = user?.Trim() ?? "";
        var p = c.PresetOf(u) ?? throw new UcException(ErrorCodes.NotInList, user ?? "");
        p.Accounts.RemoveAll(x => x.Equals(u, StringComparison.OrdinalIgnoreCase));
        foreach (var r in c.FileAccess) r.Users.RemoveAll(x => x.Equals(u, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gives a restricted account the settings of another preset.</summary>
    public static void MoveUser(UcConfig c, string user, string presetName)
    {
        ArgumentNullException.ThrowIfNull(c);
        var u = user?.Trim() ?? "";
        var from = c.PresetOf(u) ?? throw new UcException(ErrorCodes.NotRestricted, user ?? "");
        var to = c.GetPreset(presetName);
        if (ReferenceEquals(from, to)) return;
        var name = from.Accounts.First(x => x.Equals(u, StringComparison.OrdinalIgnoreCase));
        from.Accounts.Remove(name);
        to.Accounts.Add(name);
    }

    /// <summary>A new preset: a copy of another one, or the recommended settings.</summary>
    public static Preset AddPreset(UcConfig c, string name, Preset? copyOf = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var n = CheckPresetName(c, name);
        var p = copyOf is null ? ConfigSerializer.DefaultPreset(n) : ConfigSerializer.CopyPreset(copyOf, n);
        c.Presets.Add(p);
        return p;
    }

    public static void RenamePreset(UcConfig c, string name, string newName)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = c.GetPreset(name);
        if ((newName ?? "").Trim().Equals(p.Name, StringComparison.Ordinal)) return;
        var others = new UcConfig { Presets = [.. c.Presets.Where(x => !ReferenceEquals(x, p))] };
        p.Name = CheckPresetName(others, newName ?? "");
    }

    /// <summary>Deletes a preset; its accounts are no longer restricted.</summary>
    public static IReadOnlyList<string> RemovePreset(UcConfig c, string name)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = c.GetPreset(name);
        var accounts = p.Accounts.ToList();
        foreach (var a in accounts) RemoveUser(c, a);
        c.Presets.Remove(p);
        return accounts;
    }

    private static string CheckPresetName(UcConfig c, string name)
    {
        var n = (name ?? "").Trim();
        if (!Validators.IsPresetName(n)) throw new UcException(ErrorCodes.InvalidPresetName, name ?? "");
        if (c.FindPreset(n) is not null) throw new UcException(ErrorCodes.PresetExists, n);
        return n;
    }

    // ---------------------------------------------------------------- games time

    /// <summary>Adds a time window to the schedule of a profile.</summary>
    public static GamesWindow AddGamesWindow(Preset p, IEnumerable<DayOfWeek> days, string from, string to, int limitMinutes)
    {
        ArgumentNullException.ThrowIfNull(p);
        var w = new GamesWindow
        {
            Days = [.. (days ?? []).Distinct().Order()],
            From = GamesWindow.ParseTime(from) is { } f ? GamesWindow.FormatTime(f) : from ?? "",
            To = GamesWindow.ParseTime(to) is { } t ? GamesWindow.FormatTime(t) : to ?? "",
            LimitMinutes = limitMinutes,
        };
        w.EnsureValid();
        p.Games.Schedule.Add(w);
        return w;
    }

    public static void RemoveGamesWindow(Preset p, int index)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (index < 0 || index >= p.Games.Schedule.Count) throw new UcException(ErrorCodes.NotInList, index + 1);
        p.Games.Schedule.RemoveAt(index);
    }

    // ---------------------------------------------------------------- programs

    public static string AddBlockedPath(Preset c, string path, RuleCategory category, ISystemPaths sp, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = PathRules.EnsureCanBlock(path, sp);
        if (c.BlockedPaths.Any(e => SamePath(e.Path, p))) throw new UcException(ErrorCodes.AlreadyInList, p);
        c.BlockedPaths.Add(new PathEntry { Path = p, Category = category, Note = note });
        return p;
    }

    public static void RemoveBlockedPath(Preset c, string path)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = PathRules.Normalize(path);
        if (c.BlockedPaths.RemoveAll(e => SamePath(e.Path, p)) == 0) throw new UcException(ErrorCodes.NotInList, p);
    }

    /// <summary>A program folder that should run although it is inside a blocked place (for example C:\Tools).</summary>
    public static string AddAllowedPath(Preset c, string path, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = PathRules.Normalize(path);
        if (PathRules.IsDriveRoot(p)) throw new UcException(ErrorCodes.WholeDriveNotAllowed, p);
        if (c.AllowedPaths.Any(e => SamePath(e.Path, p))) throw new UcException(ErrorCodes.AlreadyInList, p);
        c.AllowedPaths.Add(new AllowedPathEntry { Path = p, Note = note });
        return p;
    }

    public static void RemoveAllowedPath(Preset c, string path)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = PathRules.Normalize(path);
        if (c.AllowedPaths.RemoveAll(e => SamePath(e.Path, p)) == 0) throw new UcException(ErrorCodes.NotInList, p);
    }

    public static string AddBlockedName(Preset c, string name, RuleCategory category, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var n = (name ?? "").Trim().Trim('"');
        if (!Validators.IsProgramName(n)) throw new UcException(ErrorCodes.InvalidProgramName, name ?? "");
        if (c.BlockedNames.Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) throw new UcException(ErrorCodes.AlreadyInList, n);
        c.BlockedNames.Add(new NameEntry { Name = n, Category = category, Note = note });
        return n;
    }

    public static void RemoveBlockedName(Preset c, string name)
    {
        ArgumentNullException.ThrowIfNull(c);
        var n = (name ?? "").Trim().Trim('"');
        if (c.BlockedNames.RemoveAll(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) == 0) throw new UcException(ErrorCodes.NotInList, n);
    }

    /// <summary>Blocks a program by its content. The hashes are computed by the caller from the file.</summary>
    public static void AddBlockedFile(Preset c, FileHashEntry entry)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(entry);
        entry.Sha256 = entry.Sha256.Trim().ToUpperInvariant();
        entry.Md5 = entry.Md5.Trim().ToUpperInvariant();
        if (!Validators.IsHex(entry.Sha256, 64) || !Validators.IsHex(entry.Md5, 32) || entry.Size <= 0)
            throw new UcException(ErrorCodes.InvalidFileHash, entry.FileName);
        if (c.BlockedFiles.Any(e => e.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new UcException(ErrorCodes.AlreadyInList, entry.FileName);
        c.BlockedFiles.Add(entry);
    }

    public static void RemoveBlockedFile(Preset c, string sha256)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.BlockedFiles.RemoveAll(e => e.Sha256.Equals(sha256?.Trim(), StringComparison.OrdinalIgnoreCase)) == 0)
            throw new UcException(ErrorCodes.NotInList, sha256 ?? "");
    }

    /// <summary>
    /// Moves a blocked folder, program name or program file (by SHA-256; its name moves with it) to another
    /// category, for example from "always blocked" to games, which follow the schedule.
    /// </summary>
    public static void SetBlockedCategory(Preset c, string key, RuleCategory category)
    {
        ArgumentNullException.ThrowIfNull(c);
        var k = (key ?? "").Trim().Trim('"');
        var found = false;
        if (PathRules.TryNormalize(k, out var path))
        {
            foreach (var e in c.BlockedPaths.Where(e => SamePath(e.Path, path)))
            {
                e.Category = category;
                found = true;
            }
        }
        foreach (var f in c.BlockedFiles.Where(e => e.Sha256.Equals(k, StringComparison.OrdinalIgnoreCase)))
        {
            f.Category = category;
            found = true;
            foreach (var n in c.BlockedNames.Where(n => n.Name.Equals(f.FileName, StringComparison.OrdinalIgnoreCase))) n.Category = category;
        }
        foreach (var n in c.BlockedNames.Where(n => n.Name.Equals(k, StringComparison.OrdinalIgnoreCase)))
        {
            n.Category = category;
            found = true;
        }
        if (!found) throw new UcException(ErrorCodes.NotInList, k);
    }

    /// <summary>Adds or replaces the access rule for a path (one rule per path and mode).</summary>
    public static string SetFileAccess(UcConfig c, string path, FileAccessMode mode, FileAccessRights rights, IEnumerable<string>? users, ISystemPaths sp, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = PathRules.EnsureCanChangeAccess(path, sp);
        if (rights == FileAccessRights.None) throw new UcException(ErrorCodes.NoRights);
        var list = (users ?? []).Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var u in list) if (!Validators.IsUserName(u)) throw new UcException(ErrorCodes.InvalidUserName, u);
        c.FileAccess.RemoveAll(r => SamePath(r.Path, p) && r.Mode == mode);
        c.FileAccess.Add(new FileAccessRule { Path = p, Mode = mode, Rights = rights, Users = list, Note = note });
        return p;
    }

    public static void RemoveFileAccess(UcConfig c, string path, FileAccessMode? mode = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = PathRules.Normalize(path);
        if (c.FileAccess.RemoveAll(r => SamePath(r.Path, p) && (mode is null || r.Mode == mode)) == 0) throw new UcException(ErrorCodes.NotInList, p);
    }

    public static void AddExtension(Preset c, BrowserKind browser, string id)
    {
        var list = ExtensionList(c, browser);
        var x = (id ?? "").Trim();
        if (!Validators.IsExtensionId(browser, x)) throw new UcException(ErrorCodes.InvalidExtensionId, id ?? "", browser);
        if (list.Contains(x, StringComparer.Ordinal)) throw new UcException(ErrorCodes.AlreadyInList, x);
        list.Add(x);
    }

    public static void RemoveExtension(Preset c, BrowserKind browser, string id)
    {
        if (ExtensionList(c, browser).RemoveAll(x => x == id?.Trim()) == 0) throw new UcException(ErrorCodes.NotInList, id ?? "");
    }

    /// <summary>
    /// Sets any browser policy by its registry name. The value is typed as people write it:
    /// a whole number or true/false becomes a DWORD, anything else text.
    /// </summary>
    public static void SetBrowserPolicy(Preset c, BrowserKind browser, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(c);
        var n = (name ?? "").Trim();
        if (!Validators.IsPolicyName(n)) throw new UcException(ErrorCodes.InvalidPolicyName, name ?? "");
        Policies(c, browser)[n] = ParsePolicyValue(value ?? "");
    }

    public static void RemoveBrowserPolicy(Preset c, BrowserKind browser, string name)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = Policies(c, browser);
        var key = p.Keys.FirstOrDefault(k => k.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (key is null) throw new UcException(ErrorCodes.NotInList, name ?? "");
        p.Remove(key);
    }

    internal static System.Text.Json.JsonElement ParsePolicyValue(string value)
    {
        var v = value.Trim();
        if (int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
            return System.Text.Json.JsonSerializer.SerializeToElement(i);
        if (bool.TryParse(v, out var b)) return System.Text.Json.JsonSerializer.SerializeToElement(b);
        return System.Text.Json.JsonSerializer.SerializeToElement(value);
    }

    private static Dictionary<string, System.Text.Json.JsonElement> Policies(Preset c, BrowserKind browser) => browser switch
    {
        BrowserKind.Chrome => c.Browsers.Chrome.Policies,
        BrowserKind.Edge => c.Browsers.Edge.Policies,
        _ => c.Browsers.Firefox.Policies,
    };

    private static List<string> ExtensionList(Preset c, BrowserKind browser)
    {
        ArgumentNullException.ThrowIfNull(c);
        return browser switch
        {
            BrowserKind.Chrome => c.Browsers.Chrome.ExtensionsAllowed,
            BrowserKind.Edge => c.Browsers.Edge.ExtensionsAllowed,
            _ => c.Browsers.Firefox.ExtensionsAllowed,
        };
    }

    private static bool SamePath(string stored, string normalized) =>
        PathRules.TryNormalize(stored, out var s) && s.Equals(normalized, StringComparison.OrdinalIgnoreCase);
}
