using UseControl.Core.Paths;
using UseControl.Core.Validation;

namespace UseControl.Core.Configuration;

/// <summary>
/// Checks a whole configuration (for example one imported from a file) with the same rules the
/// editor applies to single entries. Returns every problem, so the user can fix them in one go.
/// </summary>
public static class ConfigValidator
{
    public static IReadOnlyList<UcException> Validate(UcConfig c, ISystemPaths sp)
    {
        ArgumentNullException.ThrowIfNull(c);
        var problems = new List<UcException>();
        void Check(Action a)
        {
            try { a(); }
            catch (UcException e) { problems.Add(e); }
        }

        if (c.SchemaVersion > ConfigSerializer.CurrentSchemaVersion)
            problems.Add(new UcException(ErrorCodes.ConfigTooNew, "config", c.SchemaVersion, ConfigSerializer.CurrentSchemaVersion));

        foreach (var u in c.Users) if (!Validators.IsUserName(u)) problems.Add(new UcException(ErrorCodes.InvalidUserName, u));
        Duplicates(c.Users, problems);

        foreach (var e in c.BlockedPaths) Check(() => PathRules.EnsureCanBlock(e.Path, sp));
        DuplicatePaths(c.BlockedPaths.Select(e => e.Path), problems);

        foreach (var e in c.AllowedPaths)
        {
            Check(() =>
            {
                var p = PathRules.Normalize(e.Path);
                if (PathRules.IsDriveRoot(p)) throw new UcException(ErrorCodes.WholeDriveNotAllowed, p);
            });
        }
        DuplicatePaths(c.AllowedPaths.Select(e => e.Path), problems);

        foreach (var e in c.BlockedNames) if (!Validators.IsProgramName(e.Name)) problems.Add(new UcException(ErrorCodes.InvalidProgramName, e.Name));
        Duplicates(c.BlockedNames.Select(e => e.Name), problems);

        foreach (var f in c.BlockedFiles)
        {
            if (!Validators.IsHex(f.Sha256, 64) || !Validators.IsHex(f.Md5, 32) || f.Size <= 0) problems.Add(new UcException(ErrorCodes.InvalidFileHash, f.FileName));
        }
        Duplicates(c.BlockedFiles.Select(f => f.Sha256), problems);

        foreach (var r in c.FileAccess)
        {
            Check(() => PathRules.EnsureCanChangeAccess(r.Path, sp));
            if (r.Rights == FileAccessRights.None) problems.Add(new UcException(ErrorCodes.NoRights));
            foreach (var u in r.Users) if (!Validators.IsUserName(u)) problems.Add(new UcException(ErrorCodes.InvalidUserName, u));
        }

        foreach (var (kind, list) in new[]
        {
            (BrowserKind.Chrome, c.Browsers.Chrome.ExtensionsAllowed),
            (BrowserKind.Edge, c.Browsers.Edge.ExtensionsAllowed),
            (BrowserKind.Firefox, c.Browsers.Firefox.ExtensionsAllowed),
        })
        {
            foreach (var id in list) if (!Validators.IsExtensionId(kind, id)) problems.Add(new UcException(ErrorCodes.InvalidExtensionId, id, kind));
        }
        Check(() => Browsers.PolicyValues.FromConfig(c.Browsers.Chrome.Policies));
        Check(() => Browsers.PolicyValues.FromConfig(c.Browsers.Edge.Policies));
        Check(() => Browsers.PolicyValues.FromConfig(c.Browsers.Firefox.Policies));
        return problems;
    }

    /// <summary>Throws the first problem, if any.</summary>
    public static void EnsureValid(UcConfig c, ISystemPaths sp)
    {
        var problems = Validate(c, sp);
        if (problems.Count > 0) throw problems[0];
    }

    private static void Duplicates(IEnumerable<string> values, List<UcException> problems)
    {
        foreach (var g in values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add(new UcException(ErrorCodes.AlreadyInList, g.Key));
    }

    private static void DuplicatePaths(IEnumerable<string> paths, List<UcException> problems) =>
        Duplicates(paths.Select(p => PathRules.TryNormalize(p, out var n) ? n : p), problems);
}
