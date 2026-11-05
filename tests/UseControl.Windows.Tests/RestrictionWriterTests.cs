using Microsoft.Win32;
using UseControl.Core.Configuration;
using UseControl.Core.Rules;
using UseControl.Windows.Restrictions;

namespace UseControl.Windows.Tests;

/// <summary>Writes into a throw-away key under HKCU that stands in for an account's registry.</summary>
public sealed class RestrictionWriterTests : IDisposable
{
    private const string Sid = "S-1-5-32-546";   // Guests: any valid SID will do, keys are not locked in tests
    private readonly string _path = $@"Software\UseControlTests\{Guid.NewGuid():N}";
    private readonly RegistryKey _root;

    public RestrictionWriterTests() => _root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_path, writable: true);

    public void Dispose()
    {
        _root.Dispose();
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        using var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\UseControlTests");
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\UseControlTests", throwOnMissingSubKey: false); }
            catch (InvalidOperationException) { }   // another test created a key meanwhile
        }
    }

    private static UcConfig Config()
    {
        var c = ConfigSerializer.LoadDefault();
        c.Users.Add("kid");
        c.Browsers.Chrome.ExtensionsAllowed.Add("cjpalhdlnbpafiamejdnhcphjbkeiagm");
        c.Browsers.Firefox.ExtensionsAllowed.Add("uBlock0@raymondhill.net");
        return c;
    }

    private static RuleSet Rules(bool withFile = true) => new(
        [new PathRule(@"C:\Users\kid", RuleSource.Profile), new PathRule(@"D:\", RuleSource.Automatic), new PathRule(@"D:\games", RuleSource.Games)],
        [new PathRule(@"C:\Windows", RuleSource.System), new PathRule(@"C:\Tools", RuleSource.Allowed)],
        ["cmd.exe", "steam.exe"],
        withFile ? [new FileHashEntry { FileName = "game.exe", Sha256 = new string('A', 64), Md5 = new string('B', 32), Size = 42, Category = RuleCategory.Games }] : []);

    private object? Value(string key, string name)
    {
        using var k = _root.OpenSubKey(key);
        return k?.GetValue(name);
    }

    private int SubKeys(string key)
    {
        using var k = _root.OpenSubKey(key);
        return k?.SubKeyCount ?? 0;
    }

    [Fact]
    public void Writes_restriction_policy_names_and_switches()
    {
        RestrictionWriter.Apply(_root, Rules(), Config(), Sid, lockKeys: false);

        Assert.Equal(0x40000, Value(RestrictionWriter.CodeIdentifiers, "DefaultLevel"));
        Assert.DoesNotContain("LNK", (string[])Value(RestrictionWriter.CodeIdentifiers, "ExecutableTypes")!);
        Assert.Equal(3, SubKeys(RestrictionWriter.CodeIdentifiers + @"\0\Paths"));
        Assert.Equal(2, SubKeys(RestrictionWriter.CodeIdentifiers + @"\262144\Paths"));
        Assert.Equal(2, SubKeys(RestrictionWriter.CodeIdentifiers + @"\0\Hashes"));   // MD5 and SHA-256
        Assert.Equal("cmd.exe", Value(RestrictionWriter.DisallowRun, "1"));
        Assert.Equal("steam.exe", Value(RestrictionWriter.DisallowRun, "2"));
        Assert.Equal(1, Value(RestrictionWriter.WindowsSystemPolicies, "DisableCMD"));
        Assert.Equal(1, Value(RestrictionWriter.SystemPolicies, "DisableRegistryTools"));
        Assert.Equal(255, Value(RestrictionWriter.ExplorerPolicies, "NoDriveTypeAutoRun"));
    }

    [Fact]
    public void Hash_rules_hold_the_binary_fingerprint_and_size()
    {
        RestrictionWriter.Apply(_root, Rules(), Config(), Sid, lockKeys: false);
        using var hashes = _root.OpenSubKey(RestrictionWriter.CodeIdentifiers + @"\0\Hashes")!;
        var rules = hashes.GetSubKeyNames().Select(n => hashes.OpenSubKey(n)!).ToList();
        var md5 = rules.Single(r => (int)r.GetValue("HashAlg")! == 0x8003);
        Assert.Equal(16, ((byte[])md5.GetValue("ItemData")!).Length);
        Assert.Equal(42L, md5.GetValue("ItemSize"));
        var sha = rules.Single(r => (int)r.GetValue("HashAlg")! == 0x800C);
        Assert.Equal(32, ((byte[])sha.GetValue("ItemData")!).Length);
        rules.ForEach(r => r.Dispose());
    }

    [Fact]
    public void Writes_browser_policies()
    {
        RestrictionWriter.Apply(_root, Rules(), Config(), Sid, lockKeys: false);
        Assert.Equal("off", Value(BrowserPolicyWriter.ChromeKey, "DnsOverHttpsMode"));
        Assert.Equal(1, Value(BrowserPolicyWriter.ChromeKey, "IncognitoModeAvailability"));
        Assert.Equal("*", Value(BrowserPolicyWriter.ChromeKey + @"\ExtensionInstallBlocklist", "1"));
        Assert.Equal("cjpalhdlnbpafiamejdnhcphjbkeiagm", Value(BrowserPolicyWriter.ChromeKey + @"\ExtensionInstallAllowlist", "1"));
        Assert.Equal(1, Value(BrowserPolicyWriter.EdgeKey, "InPrivateModeAvailability"));
        Assert.Contains("uBlock0@raymondhill.net", ((string[])Value(BrowserPolicyWriter.FirefoxKey, "ExtensionSettings")!)[0], StringComparison.Ordinal);
        Assert.Equal(0, Value(BrowserPolicyWriter.FirefoxKey + @"\DNSOverHTTPS", "Enabled"));
    }

    [Fact]
    public void Applying_again_replaces_the_lists_instead_of_adding_to_them()
    {
        RestrictionWriter.Apply(_root, Rules(), Config(), Sid, lockKeys: false);
        var fewer = new RuleSet([new PathRule(@"D:\", RuleSource.Automatic)], [], ["cmd.exe"], []);
        var c = Config();
        c.Browsers.Chrome.ExtensionsAllowed.Clear();
        RestrictionWriter.Apply(_root, fewer, c, Sid, lockKeys: false);

        Assert.Equal(1, SubKeys(RestrictionWriter.CodeIdentifiers + @"\0\Paths"));
        Assert.Equal(0, SubKeys(RestrictionWriter.CodeIdentifiers + @"\0\Hashes"));
        Assert.Null(Value(RestrictionWriter.DisallowRun, "2"));
        Assert.Null(Value(BrowserPolicyWriter.ChromeKey + @"\ExtensionInstallAllowlist", "1"));
    }

    [Fact]
    public void Removes_values_left_by_the_PowerShell_version()
    {
        using (var k = _root.CreateSubKey(RestrictionWriter.ExplorerPolicies)) k.SetValue("NoControlPanel", 1);
        using (var k = _root.CreateSubKey(RestrictionWriter.SystemPolicies)) k.SetValue("DisableTaskMgr", 1);
        RestrictionWriter.Apply(_root, Rules(), Config(), Sid, lockKeys: false);
        Assert.Null(Value(RestrictionWriter.ExplorerPolicies, "NoControlPanel"));
        Assert.Null(Value(RestrictionWriter.SystemPolicies, "DisableTaskMgr"));
    }

    [Fact]
    public void Keeps_unrelated_values_in_shared_keys()
    {
        using (var k = _root.CreateSubKey(RestrictionWriter.ExplorerPolicies)) k.SetValue("SomethingElse", 7);
        RestrictionWriter.Apply(_root, Rules(), Config(), Sid, lockKeys: false);
        RestrictionWriter.Remove(_root, unlockKeys: false);
        Assert.Equal(7, Value(RestrictionWriter.ExplorerPolicies, "SomethingElse"));
    }

    [Fact]
    public void Remove_takes_everything_away()
    {
        RestrictionWriter.Apply(_root, Rules(), Config(), Sid, lockKeys: false);
        RestrictionWriter.Remove(_root, unlockKeys: false);
        Assert.Null(_root.OpenSubKey(RestrictionWriter.Safer));
        Assert.Null(_root.OpenSubKey(RestrictionWriter.DisallowRun));
        Assert.Null(_root.OpenSubKey(BrowserPolicyWriter.ChromeKey));
        Assert.Null(Value(RestrictionWriter.WindowsSystemPolicies, "DisableCMD"));
        Assert.Null(Value(RestrictionWriter.ExplorerPolicies, "DisallowRun"));
    }

    [Fact]
    public void Read_reports_what_is_written()
    {
        Assert.False(RestrictionWriter.Read(_root, Sid).Applied);
        RestrictionWriter.Apply(_root, Rules(), Config(), Sid, lockKeys: false);
        var s = RestrictionWriter.Read(_root, Sid);
        Assert.True(s.Applied);
        Assert.Equal(3, s.BlockedPathRules);
        Assert.Equal(2, s.AllowedPathRules);
        Assert.Equal(2, s.HashRules);
        Assert.Equal(2, s.BlockedNames);
        Assert.Equal(1, s.RulesBySource["games"]);
        Assert.Equal(1, s.RulesBySource["profile"]);
        Assert.False(s.KeysLocked);
    }
}
