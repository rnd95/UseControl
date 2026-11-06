using System.Security.AccessControl;
using System.Security.Principal;
using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Rules;
using UseControl.Windows.Accounts;
using UseControl.Windows.Audit;
using UseControl.Windows.FileSystem;
using UseControl.Windows.Registry;
using UseControl.Windows.Restrictions;
using UseControl.Windows.Scanning;

namespace UseControl.Windows;

/// <summary>The result of applying the configuration to one account.</summary>
public sealed record AccountResult(string User, bool Success, string? Error, int BlockedPaths, int AllowedPaths, int BlockedNames, int BlockedFiles, bool SignedIn);

public sealed record ApplyResult(IReadOnlyList<AccountResult> Accounts, IReadOnlyList<FileAccessChange> FileAccess);

/// <summary>What would be written for one account, and why.</summary>
public sealed record AccountPreview(string User, RuleSet Rules, string? Error);

/// <summary>
/// Applies a configuration to the PC: every restricted account's registry, file-system access and the
/// machine-wide Store setting. Needs administrator rights (the service runs it as SYSTEM).
/// </summary>
public sealed class RestrictionEngine
{
    private readonly ISystemPaths _sp;
    private readonly string _dataDirectory;
    private readonly AutoPathScanner _scanner;

    public RestrictionEngine(ISystemPaths sp, string dataDirectory)
    {
        _sp = sp;
        _dataDirectory = dataDirectory;
        _scanner = new AutoPathScanner(sp);
    }

    /// <summary>C:\ProgramData\UseControl: configuration, state and the file-access ledger.</summary>
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UseControl");

    /// <param name="config">The configuration.</param>
    /// <param name="gamesAllowed">Whether an account may play now (its game rules are left out).</param>
    public ApplyResult Apply(UcConfig config, Func<string, bool> gamesAllowed)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(gamesAllowed);
        SecureDataDirectory(_dataDirectory);
        var system = _scanner.SystemAllowedPaths();
        var scans = new Dictionary<string, IReadOnlyList<AutoPath>>();
        var results = new List<AccountResult>();
        var sids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var preset in config.Presets)
        {
            var auto = Scan(preset.Auto, scans);
            foreach (var user in preset.Accounts)
            {
                try
                {
                    var a = LocalAccounts.GetRestrictable(user);
                    sids[a.Name] = a.Sid;
                    var rules = RuleSetBuilder.Build(preset, a.ProfilePath, auto, system, gamesAllowed(a.Name));
                    bool signedIn;
                    using (var hive = UserHive.Open(a.Name, a.Sid, a.ProfilePath!))
                    {
                        RestrictionWriter.Apply(hive.Root, rules, preset, a.Sid);
                        signedIn = hive.AccountSignedIn;
                    }
                    results.Add(new AccountResult(a.Name, true, null, rules.Blocked.Count, rules.Allowed.Count, rules.BlockedNames.Count, rules.BlockedFiles.Count, signedIn));
                }
                catch (Exception e) when (e is Core.UcException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    results.Add(new AccountResult(user, false, e.Message, 0, 0, 0, 0, false));
                }
            }
        }

        MachinePolicies.SetStoreInstallsAdminOnly(config.BlockStoreInstalls && config.Users.Count > 0);
        var files = new FileAccessManager(_dataDirectory).Apply(config, sids);
        return new ApplyResult(results, files);
    }

    /// <summary>The restricted accounts that exist on this PC, with their SID and preset.</summary>
    public static IReadOnlyDictionary<string, (string Sid, Preset Preset)> RestrictedAccounts(UcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var d = new Dictionary<string, (string, Preset)>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in config.Presets)
        {
            foreach (var user in preset.Accounts)
            {
                if (LocalAccounts.Find(user) is { } a) d[a.Name] = (a.Sid, preset);
            }
        }
        return d;
    }

    // Presets with the same automatic options share one scan of the drives.
    private IReadOnlyList<AutoPath> Scan(AutoBlockOptions o, Dictionary<string, IReadOnlyList<AutoPath>> cache)
    {
        var key = string.Join(",", o.SystemDriveRoot, o.WritableDriveRoots, o.OtherDriveLetters, o.RecycleBins, o.SystemWritableFolders, o.WritableProgramFolders);
        if (!cache.TryGetValue(key, out var r)) cache[key] = r = _scanner.Scan(o);
        return r;
    }

    /// <summary>Removes the restrictions of one account (for example when it is no longer restricted).</summary>
    public static AccountResult RemoveAccount(string user)
    {
        try
        {
            var a = LocalAccounts.Find(user) ?? throw new Core.UcException(Core.ErrorCodes.AccountNotFound, user);
            if (string.IsNullOrEmpty(a.ProfilePath)) throw new Core.UcException(Core.ErrorCodes.ProfileMissing, user);
            using var hive = UserHive.Open(a.Name, a.Sid, a.ProfilePath);
            RestrictionWriter.Remove(hive.Root);
            return new AccountResult(a.Name, true, null, 0, 0, 0, 0, hive.AccountSignedIn);
        }
        catch (Exception e) when (e is Core.UcException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new AccountResult(user, false, e.Message, 0, 0, 0, 0, false);
        }
    }

    /// <summary>Removes everything: every listed account, the file-access entries and the Store setting.</summary>
    public ApplyResult RemoveAll(UcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var results = config.Users.Select(RemoveAccount).ToList();
        MachinePolicies.SetStoreInstallsAdminOnly(false);
        var files = new FileAccessManager(_dataDirectory).RemoveAll();
        return new ApplyResult(results, files);
    }

    /// <summary>What <see cref="Apply"/> would write, without writing anything.</summary>
    public IReadOnlyList<AccountPreview> Preview(UcConfig config, Func<string, bool> gamesAllowed, out IReadOnlyList<AutoPath> autoPaths)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(gamesAllowed);
        var scans = new Dictionary<string, IReadOnlyList<AutoPath>>();
        var system = _scanner.SystemAllowedPaths();
        var list = new List<AccountPreview>();
        foreach (var preset in config.Presets)
        {
            var auto = Scan(preset.Auto, scans);
            foreach (var user in preset.Accounts)
            {
                try
                {
                    var a = LocalAccounts.GetRestrictable(user);
                    list.Add(new AccountPreview(a.Name, RuleSetBuilder.Build(preset, a.ProfilePath, auto, system, gamesAllowed(a.Name)), null));
                }
                catch (Core.UcException e)
                {
                    list.Add(new AccountPreview(user, RuleSetBuilder.Build(preset, null, auto, system, gamesAllowed(user)), e.Message));
                }
            }
        }
        autoPaths = [.. scans.Values.SelectMany(x => x).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)];
        return list;
    }

    /// <summary>What is written in each restricted account's registry now (null when it cannot be read).</summary>
    public static IReadOnlyDictionary<string, RestrictionState?> ReadStates(UcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var d = new Dictionary<string, RestrictionState?>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in config.Users)
        {
            try
            {
                var a = LocalAccounts.GetRestrictable(user);
                using var hive = UserHive.Open(a.Name, a.Sid, a.ProfilePath!);
                d[a.Name] = RestrictionWriter.Read(hive.Root, a.Sid);
            }
            catch (Exception e) when (e is Core.UcException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                d[user] = null;
            }
        }
        return d;
    }

    public IReadOnlyList<AuditCheck> Audit(UcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // The PC is checked with every automatic block on, whatever the presets choose.
        return SecurityAudit.Run(config, _scanner.Scan(new AutoBlockOptions()), ReadStates(config));
    }

    public IReadOnlyList<string> FindGames() => GameFinder.Find(_sp);

    /// <summary>The data folder: only Administrators and SYSTEM, nothing inherited (it holds the configuration and the ledger).</summary>
    public static void SecureDataDirectory(string path)
    {
        var di = Directory.CreateDirectory(path);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        di.SetAccessControl(sec);
    }
}
