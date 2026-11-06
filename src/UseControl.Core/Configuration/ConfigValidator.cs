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

        Duplicates(c.Presets.Select(p => p.Name), problems, ErrorCodes.PresetExists);
        foreach (var g in c.Users.GroupBy(u => u, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add(new UcException(ErrorCodes.AccountInTwoPresets, g.Key));

        foreach (var p in c.Presets) ValidatePreset(p, sp, problems, Check);

        foreach (var r in c.FileAccess)
        {
            Check(() => PathRules.EnsureCanChangeAccess(r.Path, sp));
            if (r.Rights == FileAccessRights.None) problems.Add(new UcException(ErrorCodes.NoRights));
            foreach (var u in r.Users) if (!Validators.IsUserName(u)) problems.Add(new UcException(ErrorCodes.InvalidUserName, u));
        }
        return problems;
    }

    private static void ValidatePreset(Preset c, ISystemPaths sp, List<UcException> problems, Action<Action> check)
    {
        if (!Validators.IsPresetName(c.Name)) problems.Add(new UcException(ErrorCodes.InvalidPresetName, c.Name));
        foreach (var u in c.Accounts) if (!Validators.IsUserName(u)) problems.Add(new UcException(ErrorCodes.InvalidUserName, u));

        foreach (var e in c.BlockedPaths) check(() => PathRules.EnsureCanBlock(e.Path, sp));
        DuplicatePaths(c.BlockedPaths.Select(e => e.Path), problems);

        foreach (var e in c.AllowedPaths)
        {
            check(() =>
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

        if (!Enum.IsDefined(c.Games.Mode)) problems.Add(new UcException(ErrorCodes.InvalidArgument, c.Games.Mode, "mode"));
        foreach (var w in c.Games.Schedule) check(w.EnsureValid);

        foreach (var (kind, list) in new[]
        {
            (BrowserKind.Chrome, c.Browsers.Chrome.ExtensionsAllowed),
            (BrowserKind.Edge, c.Browsers.Edge.ExtensionsAllowed),
            (BrowserKind.Firefox, c.Browsers.Firefox.ExtensionsAllowed),
        })
        {
            foreach (var id in list) if (!Validators.IsExtensionId(kind, id)) problems.Add(new UcException(ErrorCodes.InvalidExtensionId, id, kind));
        }
        check(() => Browsers.PolicyValues.FromConfig(c.Browsers.Chrome.Policies));
        check(() => Browsers.PolicyValues.FromConfig(c.Browsers.Edge.Policies));
        check(() => Browsers.PolicyValues.FromConfig(c.Browsers.Firefox.Policies));
    }

    /// <summary>Throws the first problem, if any.</summary>
    public static void EnsureValid(UcConfig c, ISystemPaths sp)
    {
        var problems = Validate(c, sp);
        if (problems.Count > 0) throw problems[0];
    }

    private static void Duplicates(IEnumerable<string> values, List<UcException> problems, string code = ErrorCodes.AlreadyInList)
    {
        foreach (var g in values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add(new UcException(code, g.Key));
    }

    private static void DuplicatePaths(IEnumerable<string> paths, List<UcException> problems) =>
        Duplicates(paths.Select(p => PathRules.TryNormalize(p, out var n) ? n : p), problems);
}
