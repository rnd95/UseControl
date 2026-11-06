using System.Security.AccessControl;
using System.Security.Principal;
using UseControl.Core.Configuration;
using UseControl.Windows.FileSystem;

namespace UseControl.Windows.Tests;

public sealed class FileSystemTests : IDisposable
{
    private const string GuestsSid = "S-1-5-32-546";   // stands in for a restricted account
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly Dictionary<string, string> _accounts = new() { ["kid"] = GuestsSid };

    public FileSystemTests()
    {
        Directory.CreateDirectory(_dir);
        _data = Path.Combine(_dir, "data");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static List<FileSystemAccessRule> RulesFor(string path, string sid)
    {
        FileSystemSecurity sec = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        return [.. sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(r => r.IdentityReference.Value == sid)];
    }

    private static UcConfig ConfigWith(string path, FileAccessMode mode, FileAccessRights rights)
    {
        var c = new UcConfig { Presets = [new Preset { Name = "kid", Accounts = ["kid"] }] };
        c.FileAccess.Add(new FileAccessRule { Path = path, Mode = mode, Rights = rights });
        return c;
    }

    [Fact]
    public void Deny_rule_is_added_once_and_removed_exactly()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "private")).FullName;
        var m = new FileAccessManager(_data);

        var changes = m.Apply(ConfigWith(folder, FileAccessMode.Deny, FileAccessRights.Read | FileAccessRights.Write), _accounts);
        Assert.Equal("added", Assert.Single(changes).Action);
        var rule = Assert.Single(RulesFor(folder, GuestsSid));
        Assert.Equal(AccessControlType.Deny, rule.AccessControlType);
        Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.ReadData));
        Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);

        // Applying the same config again changes nothing.
        Assert.Empty(m.Apply(ConfigWith(folder, FileAccessMode.Deny, FileAccessRights.Read | FileAccessRights.Write), _accounts));
        Assert.Single(RulesFor(folder, GuestsSid));

        // Without the rule in the config, the entry goes away.
        Assert.Equal("removed", Assert.Single(m.Apply(new UcConfig { Presets = [new Preset { Name = "kid", Accounts = ["kid"] }] }, _accounts)).Action);
        Assert.Empty(RulesFor(folder, GuestsSid));
        Assert.Empty(m.LoadLedger());
    }

    [Fact]
    public void Changing_rights_replaces_the_entry()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "f")).FullName;
        var m = new FileAccessManager(_data);
        m.Apply(ConfigWith(folder, FileAccessMode.Deny, FileAccessRights.Read), _accounts);
        m.Apply(ConfigWith(folder, FileAccessMode.Deny, FileAccessRights.Full), _accounts);
        var rule = Assert.Single(RulesFor(folder, GuestsSid));
        Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.Delete));
    }

    [Fact]
    public void Works_on_single_files_and_leaves_other_entries_alone()
    {
        var file = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(file, "x");
        var sec = new FileInfo(file).GetAccessControl();
        var everyoneRead = new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow);
        sec.AddAccessRule(everyoneRead);
        new FileInfo(file).SetAccessControl(sec);

        var m = new FileAccessManager(_data);
        m.Apply(ConfigWith(file, FileAccessMode.Deny, FileAccessRights.Read), _accounts);
        Assert.Equal(InheritanceFlags.None, Assert.Single(RulesFor(file, GuestsSid)).InheritanceFlags);
        m.RemoveAll();
        Assert.Empty(RulesFor(file, GuestsSid));
        Assert.Single(RulesFor(file, "S-1-1-0"));
    }

    [Fact]
    public void Missing_paths_are_reported_and_not_remembered()
    {
        var m = new FileAccessManager(_data);
        var c = m.Apply(ConfigWith(Path.Combine(_dir, "nope"), FileAccessMode.Deny, FileAccessRights.Read), _accounts);
        Assert.NotNull(Assert.Single(c).Error);
        Assert.Empty(m.LoadLedger());
    }

    [Fact]
    public void Rules_apply_only_to_the_listed_accounts()
    {
        var c = ConfigWith(@"D:\X", FileAccessMode.Deny, FileAccessRights.Read);
        c.FileAccess[0].Users.Add("other");
        var accounts = new Dictionary<string, string> { ["kid"] = GuestsSid, ["other"] = "S-1-5-32-545" };
        Assert.Equal("S-1-5-32-545", Assert.Single(FileAccessManager.Desired(c, accounts)).Sid);
        c.FileAccess[0].Users.Clear();
        Assert.Equal(2, FileAccessManager.Desired(c, accounts).Count);
    }

    [Fact]
    public void Deny_read_keeps_attributes_visible_allow_read_includes_them()
    {
        var deny = FileAccessManager.MapRights(FileAccessRights.Read, FileAccessMode.Deny);
        Assert.False(deny.HasFlag(FileSystemRights.ReadAttributes));
        Assert.False(deny.HasFlag(FileSystemRights.Synchronize));
        var allow = FileAccessManager.MapRights(FileAccessRights.Read, FileAccessMode.Allow);
        Assert.True(allow.HasFlag(FileSystemRights.ReadAttributes));
        Assert.Equal(FileSystemRights.ExecuteFile, FileAccessManager.MapRights(FileAccessRights.Execute, FileAccessMode.Deny));
    }

    [Fact]
    public void Fingerprint_matches_known_hashes()
    {
        var file = Path.Combine(_dir, "abc.exe");
        File.WriteAllText(file, "abc");
        var f = FileFingerprint.Compute(file, RuleCategory.Games);
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", f.Sha256);
        Assert.Equal("900150983CD24FB0D6963F7D28E17F72", f.Md5);
        Assert.Equal(3, f.Size);
        Assert.Equal("abc.exe", f.FileName);
    }

    [Fact]
    public void Writable_folders_are_detected()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "open")).FullName;
        var di = new DirectoryInfo(folder);
        var sec = di.GetAccessControl();
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        di.SetAccessControl(sec);
        Assert.Empty(AclInspector.WritableBy(folder));

        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        di.SetAccessControl(sec);
        Assert.Single(AclInspector.WritableBy(folder));
    }
}
