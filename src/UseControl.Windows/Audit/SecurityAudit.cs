using System.Globalization;
using System.Management;
using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Rules;
using UseControl.Localization;
using UseControl.Windows.Accounts;
using UseControl.Windows.FileSystem;
using UseControl.Windows.Restrictions;

namespace UseControl.Windows.Audit;

public enum AuditStatus
{
    Ok,
    Warning,
    Fail,
}

/// <summary>
/// One result of the security check. <see cref="Id"/> is stable (for translations and scripts);
/// <see cref="Subject"/> names what was checked when a check has several results (a folder, an account).
/// <see cref="Detail"/> is English; <see cref="DetailKey"/> and <see cref="DetailArgs"/> let a client
/// show it in its own language.
/// </summary>
public sealed record AuditCheck(string Id, AuditStatus Status, string Detail, string? Subject = null)
{
    public string? DetailKey { get; init; }

    public IReadOnlyList<string> DetailArgs { get; init; } = [];

    /// <summary>The detail in the current language (falls back to English).</summary>
    public string LocalizedDetail() =>
        DetailKey is not null && Loc.Has(DetailKey) ? Loc.T(DetailKey, [.. DetailArgs]) : Detail;
}

/// <summary>The security check. Read-only: it reports, it never changes anything.</summary>
public static class SecurityAudit
{
    private static readonly string[] RemoteToolPatterns =
    [
        "ScreenConnect", "AnyDesk", "TeamViewer", "RustDesk", "Mesh Agent", "Radmin", "RServer", "Splashtop",
        "vncserver", "tvnserver", "uvnc", "LogMeIn", "Ammyy", "Atera", "Kaseya",
    ];

    /// <summary>A result whose text comes from Strings.resx ("AuditDetail." + key).</summary>
    private static AuditCheck R(string id, AuditStatus status, string key, string? subject = null, params object[] args)
    {
        var texts = args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture) ?? "").ToArray();
        return new AuditCheck(id, status, Loc.English("AuditDetail." + key, texts), subject) { DetailKey = "AuditDetail." + key, DetailArgs = texts };
    }

    private static string List(IEnumerable<string> items) => string.Join("; ", items.Distinct());

    /// <param name="config">What the administrator expects (admins, private folders...).</param>
    /// <param name="autoPaths">The result of the automatic scan (writable places).</param>
    /// <param name="restrictionStates">Per restricted account, what is written in its registry (null when it could not be read).</param>
    public static IReadOnlyList<AuditCheck> Run(UcConfig config, IReadOnlyList<AutoPath> autoPaths, IReadOnlyDictionary<string, RestrictionState?> restrictionStates)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(autoPaths);
        ArgumentNullException.ThrowIfNull(restrictionStates);

        var accounts = new List<AccountInfo>();
        var results = new List<AuditCheck>();
        foreach (var u in config.Users)
        {
            var a = LocalAccounts.Find(u);
            if (a is null) results.Add(R("account", AuditStatus.Fail, "AccountMissing", u, u));
            else if (a.IsAdministrator) results.Add(R("account", AuditStatus.Fail, "AccountIsAdmin", u, u));
            else accounts.Add(a);
        }
        var weak = AclInspector.OrdinaryUserSids.Concat(accounts.Select(a => a.Sid)).ToList();

        results.Add(CheckServices(weak));
        results.Add(CheckWritableFolders(config, autoPaths));
        results.Add(CheckAllowedFolders(config, weak));
        results.AddRange(config.Audit.PrivateFolders.Select(f => CheckPrivateFolder(f, weak)));
        results.AddRange(CheckAccounts(config));
        results.Add(CheckUac());
        results.Add(CheckAlwaysInstallElevated());
        results.Add(CheckRdp());
        results.Add(CheckRemoteTools(config));
        results.Add(CheckDefender());
        results.Add(CheckFirewall());
        foreach (var a in accounts) results.Add(CheckRestrictions(a.Name, restrictionStates.GetValueOrDefault(a.Name)));
        if (config.Users.Count == 0) results.Add(R("restrictions", AuditStatus.Warning, "NoUsers"));
        return results;
    }

    // A service whose program (or its folder) ordinary users can change is a way to become SYSTEM.
    private static AuditCheck CheckServices(IReadOnlyList<string> weak)
    {
        var bad = new List<string>();
        var seen = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        foreach (var name in services?.GetSubKeyNames() ?? [])
        {
            using var s = services!.OpenSubKey(name);
            if (s?.GetValue("Type") is not int type || (type & 0x30) == 0) continue;   // Win32 services only, no drivers
            var exe = ServiceExecutable(s.GetValue("ImagePath") as string);
            if (exe is null || !File.Exists(exe)) continue;
            foreach (var target in new[] { exe, Path.GetDirectoryName(exe)! })
            {
                if (!seen.TryGetValue(target, out var who)) seen[target] = who = AclInspector.WritableBy(target, weak);
                if (who.Count > 0) bad.Add($"{name}: {target} ({string.Join(", ", who)})");
            }
        }
        return bad.Count > 0
            ? R("services", AuditStatus.Fail, "ServicesWritable", null, List(bad))
            : R("services", AuditStatus.Ok, "ServicesOk", null, seen.Count);
    }

    internal static string? ServiceExecutable(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var p = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (p.StartsWith('"')) p = p.Split('"', StringSplitOptions.RemoveEmptyEntries)[0];
        else
        {
            var exeEnd = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd > 0) p = p[..(exeEnd + 4)];
        }
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + p[11..];
        else if (p.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), p);
        return Path.IsPathFullyQualified(p) ? p : null;
    }

    private static AuditCheck CheckWritableFolders(UcConfig config, IReadOnlyList<AutoPath> auto)
    {
        var on = config.Auto.WritableProgramFolders && config.Auto.WritableDriveRoots && config.Auto.SystemDriveRoot;
        if (!on) return R("writableFolders", AuditStatus.Warning, "WritableOff");
        var found = auto.Where(a => a.Reason.StartsWith("program folder", StringComparison.Ordinal) || a.Reason.StartsWith("drive writable", StringComparison.Ordinal))
            .Select(a => a.Path).ToList();
        return found.Count > 0
            ? R("writableFolders", AuditStatus.Ok, "WritableBlocked", null, List(found))
            : R("writableFolders", AuditStatus.Ok, "WritableNone");
    }

    private static AuditCheck CheckAllowedFolders(UcConfig config, IReadOnlyList<string> weak)
    {
        var open = config.AllowedPaths
            .Select(e => PathRules.TryNormalize(e.Path, out var p) ? p : null).OfType<string>()
            .Where(p => Directory.Exists(p) && AclInspector.WritableBy(p, weak).Count > 0).ToList();
        return open.Count > 0
            ? R("allowedFolders", AuditStatus.Warning, "AllowedWritable", null, List(open))
            : R("allowedFolders", AuditStatus.Ok, "AllowedOk", null, config.AllowedPaths.Count);
    }

    private static AuditCheck CheckPrivateFolder(string folder, IReadOnlyList<string> weak)
    {
        if (!Directory.Exists(folder) && !File.Exists(folder)) return R("privateFolder", AuditStatus.Warning, "PrivateMissing", folder);
        var others = AclInspector.AccessFor(folder, weak);
        return others.Count > 0
            ? R("privateFolder", AuditStatus.Fail, "PrivateOpen", folder, List(others))
            : R("privateFolder", AuditStatus.Ok, "PrivateOk", folder);
    }

    private static IEnumerable<AuditCheck> CheckAccounts(UcConfig config)
    {
        var all = LocalAccounts.GetAll();
        var adminNames = all.Where(a => a.IsAdministrator).Select(a => a.Name).ToList();
        var builtin = all.FirstOrDefault(a => a.IsBuiltInAdministrator);
        if (config.Audit.Admins.Count > 0)
        {
            var extra = adminNames.Where(n => !config.Audit.Admins.Contains(n, StringComparer.OrdinalIgnoreCase) && n != builtin?.Name).ToList();
            yield return extra.Count > 0
                ? R("administrators", AuditStatus.Fail, "AdminsUnexpected", null, string.Join(", ", extra))
                : R("administrators", AuditStatus.Ok, "AdminsOk", null, string.Join(", ", adminNames));
        }
        else
        {
            yield return R("administrators", AuditStatus.Ok, "AdminsNotChecked", null, string.Join(", ", adminNames));
        }
        yield return builtin is { Enabled: true }
            ? R("builtinAdministrator", AuditStatus.Warning, "BuiltinEnabled")
            : R("builtinAdministrator", AuditStatus.Ok, "BuiltinDisabled");

        var noPassword = all.Where(a => a.Enabled && a.PasswordNotRequired).Select(a => a.Name).ToList();
        yield return noPassword.Count > 0
            ? R("emptyPasswords", AuditStatus.Warning, "EmptyPasswords", null, string.Join(", ", noPassword))
            : R("emptyPasswords", AuditStatus.Ok, "EmptyPasswordsOk");

        if (config.Audit.HiddenAccounts.Count > 0)
        {
            var visible = config.Audit.HiddenAccounts.Where(n => !LocalAccounts.IsHiddenFromSignIn(n)).ToList();
            yield return visible.Count > 0
                ? R("hiddenAccounts", AuditStatus.Warning, "HiddenVisible", null, string.Join(", ", visible))
                : R("hiddenAccounts", AuditStatus.Ok, "HiddenOk", null, string.Join(", ", config.Audit.HiddenAccounts));
        }
    }

    private static AuditCheck CheckUac()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        int Get(string n, int d) => k?.GetValue(n) is int v ? v : d;
        var lua = Get("EnableLUA", 1);
        var admin = Get("ConsentPromptBehaviorAdmin", 5);
        var secure = Get("PromptOnSecureDesktop", 1);
        var user = Get("ConsentPromptBehaviorUser", 3);
        var ok = lua == 1 && admin is 1 or 2 or 5 && secure == 1 && user is 1 or 3;
        return R("uac", ok ? AuditStatus.Ok : AuditStatus.Warning, ok ? "UacOk" : "UacWeak", null, lua, admin, secure, user);
    }

    private static AuditCheck CheckAlwaysInstallElevated()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Installer");
        return k?.GetValue("AlwaysInstallElevated") is int v && v == 1
            ? R("alwaysInstallElevated", AuditStatus.Fail, "AieOn")
            : R("alwaysInstallElevated", AuditStatus.Ok, "AieOff");
    }

    private static AuditCheck CheckRdp()
    {
        using var ts = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
        if (ts?.GetValue("fDenyTSConnections") is int deny && deny == 1) return R("rdp", AuditStatus.Ok, "RdpOff");
        using var tcp = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
        var nla = tcp?.GetValue("UserAuthentication") is int n && n == 1;
        var scopes = new List<string>();
        var any = false;
        try
        {
            foreach (var r in FirewallRules().Where(r => r.Group == "@FirewallAPI.dll,-28752" && r.Enabled && r.Inbound))
            {
                scopes.Add(r.RemoteAddresses);
                if (r.RemoteAddresses == "*") any = true;
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return R("rdp", AuditStatus.Warning, "RdpUnknown");
        }
        if (scopes.Count == 0) return R("rdp", AuditStatus.Warning, "RdpNoRule");
        var status = !nla || any ? AuditStatus.Warning : AuditStatus.Ok;
        return R("rdp", status, nla ? "RdpOn" : "RdpOnNoNla", null, string.Join(", ", scopes.Distinct()));
    }

    private static AuditCheck CheckRemoteTools(UcConfig config)
    {
        var found = new List<string>();
        using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        foreach (var name in services?.GetSubKeyNames() ?? [])
        {
            if (!RemoteToolPatterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            using var s = services!.OpenSubKey(name);
            if (s?.GetValue("Type") is int type && (type & 0x30) != 0) found.Add(name);
        }
        var unknown = found.Where(f => !config.Audit.KnownRemoteServices.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        var known = found.Except(unknown).ToList();
        if (unknown.Count > 0) return R("remoteTools", AuditStatus.Warning, "RemoteUnknown", null, string.Join(", ", unknown));
        return known.Count > 0
            ? R("remoteTools", AuditStatus.Ok, "RemoteKnown", null, string.Join(", ", known))
            : R("remoteTools", AuditStatus.Ok, "RemoteNone");
    }

    private static AuditCheck CheckDefender()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT RealTimeProtectionEnabled, AntivirusSignatureLastUpdated FROM MSFT_MpComputerStatus");
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    var rt = o["RealTimeProtectionEnabled"] is bool b && b;
                    var sig = o["AntivirusSignatureLastUpdated"] is string s ? ManagementDateTimeConverter.ToDateTime(s).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "?";
                    return rt ? R("defender", AuditStatus.Ok, "DefenderOn", null, sig) : R("defender", AuditStatus.Warning, "DefenderOff", null, sig);
                }
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
        return R("defender", AuditStatus.Warning, "DefenderUnknown");
    }

    private static AuditCheck CheckFirewall()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
            var off = new List<string>();
            foreach (var (bit, name) in new[] { (1, "Domain"), (2, "Private"), (4, "Public") })
            {
                if (!(bool)policy.get_FirewallEnabled(bit)) off.Add(name);
            }
            return off.Count > 0
                ? R("firewall", AuditStatus.Warning, "FirewallOff", null, string.Join(", ", off))
                : R("firewall", AuditStatus.Ok, "FirewallOn");
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            return R("firewall", AuditStatus.Warning, "FirewallUnknown");
        }
    }

    private static AuditCheck CheckRestrictions(string user, RestrictionState? st)
    {
        if (st is null) return R("restrictions", AuditStatus.Warning, "RestrictionsUnreadable", user);
        if (!st.Applied) return R("restrictions", AuditStatus.Fail, "RestrictionsMissing", user);
        return R("restrictions", st.KeysLocked ? AuditStatus.Ok : AuditStatus.Warning, st.KeysLocked ? "RestrictionsOk" : "RestrictionsUnlocked", user,
            st.BlockedPathRules, st.AllowedPathRules, st.HashRules / 2, st.BlockedNames);
    }

    private sealed record FwRule(string Group, bool Enabled, bool Inbound, string RemoteAddresses);

    private static List<FwRule> FirewallRules()
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        var list = new List<FwRule>();
        foreach (dynamic r in policy.Rules)
        {
            list.Add(new FwRule((string?)r.Grouping ?? "", (bool)r.Enabled, (int)r.Direction == 1, (string?)r.RemoteAddresses ?? "*"));
        }
        return list;
    }
}
