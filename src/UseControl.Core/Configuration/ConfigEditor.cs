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
    public static void AddUser(UcConfig c, string user)
    {
        ArgumentNullException.ThrowIfNull(c);
        var u = (user ?? "").Trim();
        if (!Validators.IsUserName(u)) throw new UcException(ErrorCodes.InvalidUserName, user ?? "");
        if (c.Users.Contains(u, StringComparer.OrdinalIgnoreCase)) throw new UcException(ErrorCodes.AlreadyInList, u);
        c.Users.Add(u);
    }

    public static void RemoveUser(UcConfig c, string user)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Users.RemoveAll(x => x.Equals(user?.Trim(), StringComparison.OrdinalIgnoreCase)) == 0) throw new UcException(ErrorCodes.NotInList, user ?? "");
        foreach (var r in c.FileAccess) r.Users.RemoveAll(x => x.Equals(user?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string AddBlockedPath(UcConfig c, string path, RuleCategory category, ISystemPaths sp, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = PathRules.EnsureCanBlock(path, sp);
        if (c.BlockedPaths.Any(e => SamePath(e.Path, p))) throw new UcException(ErrorCodes.AlreadyInList, p);
        c.BlockedPaths.Add(new PathEntry { Path = p, Category = category, Note = note });
        return p;
    }

    public static void RemoveBlockedPath(UcConfig c, string path)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = PathRules.Normalize(path);
        if (c.BlockedPaths.RemoveAll(e => SamePath(e.Path, p)) == 0) throw new UcException(ErrorCodes.NotInList, p);
    }

    /// <summary>A program folder that should run although it is inside a blocked place (for example C:\Tools).</summary>
    public static string AddAllowedPath(UcConfig c, string path, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = PathRules.Normalize(path);
        if (PathRules.IsDriveRoot(p)) throw new UcException(ErrorCodes.WholeDriveNotAllowed, p);
        if (c.AllowedPaths.Any(e => SamePath(e.Path, p))) throw new UcException(ErrorCodes.AlreadyInList, p);
        c.AllowedPaths.Add(new AllowedPathEntry { Path = p, Note = note });
        return p;
    }

    public static void RemoveAllowedPath(UcConfig c, string path)
    {
        ArgumentNullException.ThrowIfNull(c);
        var p = PathRules.Normalize(path);
        if (c.AllowedPaths.RemoveAll(e => SamePath(e.Path, p)) == 0) throw new UcException(ErrorCodes.NotInList, p);
    }

    public static string AddBlockedName(UcConfig c, string name, RuleCategory category, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var n = (name ?? "").Trim().Trim('"');
        if (!Validators.IsProgramName(n)) throw new UcException(ErrorCodes.InvalidProgramName, name ?? "");
        if (c.BlockedNames.Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) throw new UcException(ErrorCodes.AlreadyInList, n);
        c.BlockedNames.Add(new NameEntry { Name = n, Category = category, Note = note });
        return n;
    }

    public static void RemoveBlockedName(UcConfig c, string name)
    {
        ArgumentNullException.ThrowIfNull(c);
        var n = (name ?? "").Trim().Trim('"');
        if (c.BlockedNames.RemoveAll(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) == 0) throw new UcException(ErrorCodes.NotInList, n);
    }

    /// <summary>Blocks a program by its content. The hashes are computed by the caller from the file.</summary>
    public static void AddBlockedFile(UcConfig c, FileHashEntry entry)
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

    public static void RemoveBlockedFile(UcConfig c, string sha256)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.BlockedFiles.RemoveAll(e => e.Sha256.Equals(sha256?.Trim(), StringComparison.OrdinalIgnoreCase)) == 0)
            throw new UcException(ErrorCodes.NotInList, sha256 ?? "");
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

    public static void AddExtension(UcConfig c, BrowserKind browser, string id)
    {
        var list = ExtensionList(c, browser);
        var x = (id ?? "").Trim();
        if (!Validators.IsExtensionId(browser, x)) throw new UcException(ErrorCodes.InvalidExtensionId, id ?? "", browser);
        if (list.Contains(x, StringComparer.Ordinal)) throw new UcException(ErrorCodes.AlreadyInList, x);
        list.Add(x);
    }

    public static void RemoveExtension(UcConfig c, BrowserKind browser, string id)
    {
        if (ExtensionList(c, browser).RemoveAll(x => x == id?.Trim()) == 0) throw new UcException(ErrorCodes.NotInList, id ?? "");
    }

    /// <summary>
    /// Sets any browser policy by its registry name. The value is typed as people write it:
    /// a whole number or true/false becomes a DWORD, anything else text.
    /// </summary>
    public static void SetBrowserPolicy(UcConfig c, BrowserKind browser, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(c);
        var n = (name ?? "").Trim();
        if (!Validators.IsPolicyName(n)) throw new UcException(ErrorCodes.InvalidPolicyName, name ?? "");
        Policies(c, browser)[n] = ParsePolicyValue(value ?? "");
    }

    public static void RemoveBrowserPolicy(UcConfig c, BrowserKind browser, string name)
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

    private static Dictionary<string, System.Text.Json.JsonElement> Policies(UcConfig c, BrowserKind browser) => browser switch
    {
        BrowserKind.Chrome => c.Browsers.Chrome.Policies,
        BrowserKind.Edge => c.Browsers.Edge.Policies,
        _ => c.Browsers.Firefox.Policies,
    };

    private static List<string> ExtensionList(UcConfig c, BrowserKind browser)
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
