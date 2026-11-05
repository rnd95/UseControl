using System.Management;
using Microsoft.Win32;
using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Rules;
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
/// </summary>
public sealed record AuditCheck(string Id, AuditStatus Status, string Detail, string? Subject = null);

/// <summary>The security check. Read-only: it reports, it never changes anything.</summary>
public static class SecurityAudit
{
    private static readonly string[] RemoteToolPatterns =
    [
        "ScreenConnect", "AnyDesk", "TeamViewer", "RustDesk", "Mesh Agent", "Radmin", "RServer", "Splashtop",
        "vncserver", "tvnserver", "uvnc", "LogMeIn", "Ammyy", "Atera", "Kaseya",
    ];

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
            if (a is null) results.Add(new("account", AuditStatus.Fail, $"There is no local account '{u}'.", u));
            else if (a.IsAdministrator) results.Add(new("account", AuditStatus.Fail, $"{u} is an administrator and cannot be restricted.", u));
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
        if (config.Users.Count == 0) results.Add(new("restrictions", AuditStatus.Warning, "No accounts are restricted yet."));
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
            ? new("services", AuditStatus.Fail, "Writable by ordinary users: " + string.Join("; ", bad.Distinct()))
            : new("services", AuditStatus.Ok, $"No service program or folder is writable by ordinary users ({seen.Count} checked).");
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
        var found = auto.Where(a => a.Reason.StartsWith("program folder", StringComparison.Ordinal) || a.Reason.StartsWith("drive writable", StringComparison.Ordinal))
            .Select(a => a.Path).ToList();
        if (!on) return new("writableFolders", AuditStatus.Warning, "Automatic blocking of writable places is switched off (auto in config.json).");
        return new("writableFolders", AuditStatus.Ok, found.Count > 0
            ? "Programs cannot start from these (blocked automatically): " + string.Join("; ", found)
            : "None found.");
    }

    private static AuditCheck CheckAllowedFolders(UcConfig config, IReadOnlyList<string> weak)
    {
        var open = config.AllowedPaths
            .Select(e => PathRules.TryNormalize(e.Path, out var p) ? p : null).OfType<string>()
            .Where(p => Directory.Exists(p) && AclInspector.WritableBy(p, weak).Count > 0).ToList();
        return open.Count > 0
            ? new("allowedFolders", AuditStatus.Warning, "Allowed but writable by ordinary users (a copied program would run): " + string.Join("; ", open))
            : new("allowedFolders", AuditStatus.Ok, $"{config.AllowedPaths.Count} allowed folder(s), none writable by ordinary users.");
    }

    private static AuditCheck CheckPrivateFolder(string folder, IReadOnlyList<string> weak)
    {
        if (!Directory.Exists(folder) && !File.Exists(folder)) return new("privateFolder", AuditStatus.Warning, "Not found.", folder);
        var others = AclInspector.AccessFor(folder, weak);
        return others.Count > 0
            ? new("privateFolder", AuditStatus.Fail, "Others have access: " + string.Join("; ", others), folder)
            : new("privateFolder", AuditStatus.Ok, "Only administrators, SYSTEM and the owners listed in its permissions.", folder);
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
                ? new("administrators", AuditStatus.Fail, "Unexpected members: " + string.Join(", ", extra))
                : new("administrators", AuditStatus.Ok, string.Join(", ", adminNames));
        }
        else
        {
            yield return new("administrators", AuditStatus.Ok, string.Join(", ", adminNames) + " (list the expected ones in audit.admins to be warned about others)");
        }
        yield return builtin is { Enabled: true }
            ? new("builtinAdministrator", AuditStatus.Warning, "Enabled.")
            : new("builtinAdministrator", AuditStatus.Ok, "Disabled.");

        var noPassword = all.Where(a => a.Enabled && a.PasswordNotRequired).Select(a => a.Name).ToList();
        yield return noPassword.Count > 0
            ? new("emptyPasswords", AuditStatus.Warning, "May have an empty password (fix: net user <name> /passwordreq:yes): " + string.Join(", ", noPassword))
            : new("emptyPasswords", AuditStatus.Ok, "No enabled account may have an empty password.");

        if (config.Audit.HiddenAccounts.Count > 0)
        {
            var visible = config.Audit.HiddenAccounts.Where(n => !LocalAccounts.IsHiddenFromSignIn(n)).ToList();
            yield return visible.Count > 0
                ? new("hiddenAccounts", AuditStatus.Warning, "Shown on the sign-in screen: " + string.Join(", ", visible))
                : new("hiddenAccounts", AuditStatus.Ok, string.Join(", ", config.Audit.HiddenAccounts) + " hidden.");
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
        return new("uac", ok ? AuditStatus.Ok : AuditStatus.Warning,
            $"EnableLUA={lua} ConsentPromptBehaviorAdmin={admin} PromptOnSecureDesktop={secure} ConsentPromptBehaviorUser={user} (recommended: 1, 5, 1, 3)");
    }

    private static AuditCheck CheckAlwaysInstallElevated()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Installer");
        return k?.GetValue("AlwaysInstallElevated") is int v && v == 1
            ? new("alwaysInstallElevated", AuditStatus.Fail, "On: any .msi installs with SYSTEM rights.")
            : new("alwaysInstallElevated", AuditStatus.Ok, "Off.");
    }

    private static AuditCheck CheckRdp()
    {
        using var ts = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
        if (ts?.GetValue("fDenyTSConnections") is int deny && deny == 1) return new("rdp", AuditStatus.Ok, "Off.");
        using var tcp = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
        var nla = tcp?.GetValue("UserAuthentication") is int n ? n : 0;
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
            return new("rdp", AuditStatus.Warning, $"On, NLA={nla}; firewall rules could not be read.");
        }
        var status = nla != 1 || any || scopes.Count == 0 ? AuditStatus.Warning : AuditStatus.Ok;
        return new("rdp", status, $"On, NLA={nla}, allowed from: " + (scopes.Count > 0 ? string.Join(", ", scopes.Distinct()) : "no enabled Remote Desktop firewall rule"));
    }

    private static AuditCheck CheckRemoteTools(UcConfig config)
    {
        var found = new List<(string Name, string Text)>();
        using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        foreach (var name in services?.GetSubKeyNames() ?? [])
        {
            if (!RemoteToolPatterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            using var s = services!.OpenSubKey(name);
            if (s?.GetValue("Type") is int type && (type & 0x30) != 0) found.Add((name, name));
        }
        var unknown = found.Where(f => !config.Audit.KnownRemoteServices.Contains(f.Name, StringComparer.OrdinalIgnoreCase)).Select(f => f.Text).ToList();
        var known = found.Where(f => config.Audit.KnownRemoteServices.Contains(f.Name, StringComparer.OrdinalIgnoreCase)).Select(f => f.Text).ToList();
        if (unknown.Count > 0)
            return new("remoteTools", AuditStatus.Warning, "Not in audit.knownRemoteServices, make sure you know them: " + string.Join(", ", unknown)
                + (known.Count > 0 ? "; known: " + string.Join(", ", known) : ""));
        return new("remoteTools", AuditStatus.Ok, known.Count > 0 ? "Known: " + string.Join(", ", known) : "None found.");
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
                    var sig = o["AntivirusSignatureLastUpdated"] is string s ? ManagementDateTimeConverter.ToDateTime(s).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "?";
                    return new("defender", rt ? AuditStatus.Ok : AuditStatus.Warning, $"Real-time protection: {(rt ? "on" : "OFF")}, signatures {sig}.");
                }
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
        return new("defender", AuditStatus.Warning, "Status unavailable (another antivirus?).");
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
                ? new("firewall", AuditStatus.Warning, "Off for: " + string.Join(", ", off))
                : new("firewall", AuditStatus.Ok, "On for all profiles.");
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            return new("firewall", AuditStatus.Warning, "Status unavailable.");
        }
    }

    private static AuditCheck CheckRestrictions(string user, RestrictionState? st)
    {
        if (st is null) return new("restrictions", AuditStatus.Warning, "Could not read the account's settings (run as administrator).", user);
        var status = !st.Applied ? AuditStatus.Fail : !st.KeysLocked ? AuditStatus.Warning : AuditStatus.Ok;
        return new("restrictions", status,
            $"{st.BlockedPathRules} blocked and {st.AllowedPathRules} allowed folders, {st.HashRules / 2} blocked files, {st.BlockedNames} names, policy keys locked: {st.KeysLocked}", user);
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
