using Microsoft.Win32;
using UseControl.Core.Configuration;
using UseControl.Core.Rules;
using UseControl.Windows.Registry;

namespace UseControl.Windows.Restrictions;

/// <summary>What is written in one account's registry right now.</summary>
public sealed record RestrictionState(bool Applied, int BlockedPathRules, int AllowedPathRules, int HashRules, int BlockedNames, IReadOnlyDictionary<string, int> RulesBySource, bool KeysLocked);

/// <summary>
/// Writes the restrictions into an account's registry (the root passed in is that account's HKCU):
/// Software Restriction Policy, blocked program names, command prompt and registry editor off,
/// no autorun, browser policies; then makes the policy keys read-only for the account.
/// </summary>
public static class RestrictionWriter
{
    internal const string ExplorerPolicies = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    internal const string SystemPolicies = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    internal const string WindowsSystemPolicies = @"Software\Policies\Microsoft\Windows\System";
    internal const string DisallowRun = ExplorerPolicies + @"\DisallowRun";
    internal const string Safer = @"Software\Policies\Microsoft\Windows\Safer";
    internal const string CodeIdentifiers = Safer + @"\CodeIdentifiers";

    /// <summary>Keys made read-only for the account, so it cannot delete its own restrictions.</summary>
    internal static readonly string[] LockedKeys = [@"Software\Policies", @"Software\Microsoft\Windows\CurrentVersion\Policies"];

    private const string Disallowed = "0";
    private const string Unrestricted = "262144";
    private const int HashAlgMd5 = 0x8003;
    private const int HashAlgSha256 = 0x800C;

    /// <summary>
    /// File types the restriction policy checks. LNK and URL are left out on purpose: otherwise every
    /// shortcut on the Desktop and in the Start menu (they live in the profile) would be blocked.
    /// </summary>
    internal static readonly string[] ExecutableTypes =
    [
        "ADE", "ADP", "BAS", "BAT", "CHM", "CMD", "COM", "CPL", "CRT", "EXE", "HLP", "HTA", "INF", "INS", "ISP",
        "JS", "JSE", "MDB", "MDE", "MSC", "MSI", "MSP", "MST", "OCX", "PCD", "PIF", "PS1", "REG", "SCR", "SHS",
        "VB", "VBE", "VBS", "WSC", "WSF", "WSH",
    ];

    /// <param name="root">The account's HKCU.</param>
    /// <param name="rules">The rule set built for this account.</param>
    /// <param name="config">For the browser policies.</param>
    /// <param name="userSid">The account, for the read-only lock.</param>
    /// <param name="lockKeys">False only in tests (locking needs administrator rights).</param>
    public static void Apply(RegistryKey root, RuleSet rules, UcConfig config, string userSid, bool lockKeys = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(config);

        RemoveLegacy(root);
        // Rebuilt from scratch every time, so entries removed from the config disappear.
        RegistryHelpers.DeleteTree(root, Safer);
        RegistryHelpers.DeleteTree(root, DisallowRun);
        BrowserPolicyWriter.Remove(root);

        RegistryHelpers.SetDword(root, ExplorerPolicies, "DisallowRun", 1);
        foreach (var s in Switches(config.Restrictions))
        {
            if (s.On) RegistryHelpers.SetDword(root, s.Key, s.Name, s.Value);
            else RegistryHelpers.DeleteValue(root, s.Key, s.Name);
        }

        using (var names = RegistryHelpers.Ensure(root, DisallowRun))
        {
            var i = 1;
            foreach (var n in rules.BlockedNames) names.SetValue((i++).ToString(System.Globalization.CultureInfo.InvariantCulture), n, RegistryValueKind.String);
        }

        using (var ci = RegistryHelpers.Ensure(root, CodeIdentifiers))
        {
            ci.SetValue("DefaultLevel", 0x40000, RegistryValueKind.DWord);       // everything runs...
            ci.SetValue("TransparentEnabled", 1, RegistryValueKind.DWord);       // ...programs only, not DLLs
            ci.SetValue("PolicyScope", 0, RegistryValueKind.DWord);
            ci.SetValue("AuthenticodeEnabled", 0, RegistryValueKind.DWord);
            ci.SetValue("ExecutableTypes", ExecutableTypes, RegistryValueKind.MultiString);
            WritePathRules(ci, Disallowed, rules.Blocked);                       // ...except from these
            WritePathRules(ci, Unrestricted, rules.Allowed);                     // ...which run again inside them
            WriteHashRules(ci, rules.BlockedFiles);
        }

        BrowserPolicyWriter.Apply(root, config.Browsers);

        if (lockKeys)
        {
            foreach (var k in LockedKeys)
            {
                RegistryHelpers.Ensure(root, k).Dispose();
                RegistryHelpers.Lock(root, k, userSid);
            }
        }
    }

    /// <summary>Removes everything <see cref="Apply"/> writes and gives the account its keys back.</summary>
    public static void Remove(RegistryKey root, bool unlockKeys = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (unlockKeys) foreach (var k in LockedKeys) RegistryHelpers.Unlock(root, k);
        RemoveLegacy(root);
        RegistryHelpers.DeleteTree(root, Safer);
        RegistryHelpers.DeleteTree(root, DisallowRun);
        BrowserPolicyWriter.Remove(root);
        RegistryHelpers.DeleteValue(root, ExplorerPolicies, "DisallowRun");
        foreach (var s in Switches(new AccountRestrictions())) RegistryHelpers.DeleteValue(root, s.Key, s.Name);
    }

    /// <summary>The Windows policy behind each switch of <see cref="AccountRestrictions"/>.</summary>
    internal static IEnumerable<(string Key, string Name, int Value, bool On)> Switches(AccountRestrictions r)
    {
        ArgumentNullException.ThrowIfNull(r);
        yield return (WindowsSystemPolicies, "DisableCMD", 1, r.DisableCommandPrompt);
        yield return (SystemPolicies, "DisableRegistryTools", 1, r.DisableRegistryEditor);
        yield return (SystemPolicies, "DisableTaskMgr", 1, r.DisableTaskManager);
        yield return (SystemPolicies, "DisableChangePassword", 1, r.DisablePasswordChange);
        yield return (ExplorerPolicies, "NoControlPanel", 1, r.DisableControlPanel);
        yield return (ExplorerPolicies, "NoRun", 1, r.DisableRunDialog);
        yield return (ExplorerPolicies, "NoDriveTypeAutoRun", 255, r.DisableAutorun);
    }

    public static RestrictionState Read(RegistryKey root, string userSid)
    {
        ArgumentNullException.ThrowIfNull(root);
        var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int blocked = 0, allowed = 0, hashes = 0;
        using (var paths = root.OpenSubKey($@"{CodeIdentifiers}\{Disallowed}\Paths"))
        {
            foreach (var name in paths?.GetSubKeyNames() ?? [])
            {
                using var r = paths!.OpenSubKey(name);
                var desc = (r?.GetValue("Description") as string ?? "").Split(':', 2);
                var source = desc.Length == 2 ? desc[1] : "unknown";
                bySource[source] = bySource.GetValueOrDefault(source) + 1;
                blocked++;
            }
        }
        using (var paths = root.OpenSubKey($@"{CodeIdentifiers}\{Unrestricted}\Paths")) allowed = paths?.SubKeyCount ?? 0;
        using (var h = root.OpenSubKey($@"{CodeIdentifiers}\{Disallowed}\Hashes")) hashes = h?.SubKeyCount ?? 0;
        int names;
        using (var n = root.OpenSubKey(DisallowRun)) names = n?.ValueCount ?? 0;
        var locked = LockedKeys.All(k => RegistryHelpers.IsLocked(root, k, userSid));
        return new RestrictionState(blocked > 0, blocked, allowed, hashes, names, bySource, locked);
    }

    /// <summary>Values written by the PowerShell version that are no longer wanted.</summary>
    private static void RemoveLegacy(RegistryKey root)
    {
        RegistryHelpers.DeleteValue(root, @"Software\Policies\Microsoft\WindowsStore", "RemoveWindowsStore");
    }

    private static void WritePathRules(RegistryKey ci, string level, IEnumerable<PathRule> rules)
    {
        using var paths = RegistryHelpers.Ensure(ci, $@"{level}\Paths");
        foreach (var p in rules)
        {
            using var r = RegistryHelpers.Ensure(paths, "{" + Guid.NewGuid() + "}");
            r.SetValue("ItemData", p.Path, RegistryValueKind.String);
            r.SetValue("SaferFlags", 0, RegistryValueKind.DWord);
            r.SetValue("Description", $"UseControl:{p.Source.ToString().ToLowerInvariant()}", RegistryValueKind.String);
            r.SetValue("LastModified", DateTime.Now.ToFileTime(), RegistryValueKind.QWord);
        }
    }

    // A hash rule per algorithm: Windows checks MD5 (classic) and SHA-256 entries.
    private static void WriteHashRules(RegistryKey ci, IEnumerable<FileHashEntry> files)
    {
        using var hashes = RegistryHelpers.Ensure(ci, $@"{Disallowed}\Hashes");
        foreach (var f in files)
        {
            foreach (var (alg, hex) in new[] { (HashAlgMd5, f.Md5), (HashAlgSha256, f.Sha256) })
            {
                using var r = RegistryHelpers.Ensure(hashes, "{" + Guid.NewGuid() + "}");
                r.SetValue("ItemData", Convert.FromHexString(hex), RegistryValueKind.Binary);
                r.SetValue("HashAlg", alg, RegistryValueKind.DWord);
                r.SetValue("ItemSize", f.Size, RegistryValueKind.QWord);
                r.SetValue("SaferFlags", 0, RegistryValueKind.DWord);
                r.SetValue("FriendlyName", f.FileName, RegistryValueKind.String);
                r.SetValue("Description", $"UseControl:{f.Category.ToString().ToLowerInvariant()}", RegistryValueKind.String);
                r.SetValue("LastModified", DateTime.Now.ToFileTime(), RegistryValueKind.QWord);
            }
        }
    }
}
