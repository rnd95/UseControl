using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Validation;

namespace UseControl.Core.Tests;

public class ConfigEditorTests
{
    private static readonly FixedSystemPaths Sp = new();

    private static UcConfig NewConfig()
    {
        var c = ConfigSerializer.LoadDefault();
        c.Users.Add("kid");
        return c;
    }

    private static string Code(Action a) => Assert.Throws<UcException>(a).Code;

    [Fact]
    public void Blocked_folder_is_added_normalised_and_removed()
    {
        var c = NewConfig();
        Assert.Equal(@"F:\Test Games", ConfigEditor.AddBlockedPath(c, @"f:\Test Games\", RuleCategory.Games, Sp));
        Assert.Equal(RuleCategory.Games, c.BlockedPaths.Single(e => e.Path == @"F:\Test Games").Category);
        ConfigEditor.RemoveBlockedPath(c, @"F:\TEST GAMES");
        Assert.DoesNotContain(c.BlockedPaths, e => e.Path == @"F:\Test Games");
    }

    [Fact]
    public void Blocked_folder_refuses_duplicates_protected_places_and_unknown_entries()
    {
        var c = NewConfig();
        ConfigEditor.AddBlockedPath(c, @"F:\X", RuleCategory.Games, Sp);
        Assert.Equal(ErrorCodes.AlreadyInList, Code(() => ConfigEditor.AddBlockedPath(c, @"F:\X\", RuleCategory.Games, Sp)));
        Assert.Equal(ErrorCodes.PathProtected, Code(() => ConfigEditor.AddBlockedPath(c, @"C:\Windows", RuleCategory.Games, Sp)));
        Assert.Equal(ErrorCodes.NotInList, Code(() => ConfigEditor.RemoveBlockedPath(c, @"F:\Not There")));
    }

    [Fact]
    public void Allowed_folder_is_added_but_never_a_whole_drive()
    {
        var c = NewConfig();
        ConfigEditor.AddAllowedPath(c, @"C:\Tools");
        Assert.Equal(ErrorCodes.AlreadyInList, Code(() => ConfigEditor.AddAllowedPath(c, @"c:\tools\")));
        Assert.Equal(ErrorCodes.WholeDriveNotAllowed, Code(() => ConfigEditor.AddAllowedPath(c, @"D:\")));
        ConfigEditor.RemoveAllowedPath(c, @"C:\Tools");
        Assert.Empty(c.AllowedPaths);
    }

    [Fact]
    public void Program_names_are_validated()
    {
        var c = NewConfig();
        ConfigEditor.AddBlockedName(c, "mygame.exe", RuleCategory.Games);
        Assert.Equal(ErrorCodes.AlreadyInList, Code(() => ConfigEditor.AddBlockedName(c, "MyGame.EXE", RuleCategory.Games)));
        Assert.Equal(ErrorCodes.InvalidProgramName, Code(() => ConfigEditor.AddBlockedName(c, @"C:\x\game.exe", RuleCategory.Games)));
        Assert.Equal(ErrorCodes.InvalidProgramName, Code(() => ConfigEditor.AddBlockedName(c, "readme.txt", RuleCategory.Games)));
        ConfigEditor.RemoveBlockedName(c, "mygame.exe");
        Assert.DoesNotContain(c.BlockedNames, e => e.Name == "mygame.exe");
    }

    [Fact]
    public void Accounts_are_validated_and_removed_from_file_rules_too()
    {
        var c = NewConfig();
        ConfigEditor.AddUser(c, "kid2");
        Assert.Equal(ErrorCodes.AlreadyInList, Code(() => ConfigEditor.AddUser(c, "KID2")));
        Assert.Equal(ErrorCodes.InvalidUserName, Code(() => ConfigEditor.AddUser(c, @"bad\name")));
        Assert.Equal(ErrorCodes.InvalidUserName, Code(() => ConfigEditor.AddUser(c, "name-that-is-far-too-long")));
        ConfigEditor.SetFileAccess(c, @"D:\Private", FileAccessMode.Deny, FileAccessRights.Full, ["kid", "kid2"], Sp);
        ConfigEditor.RemoveUser(c, "kid2");
        Assert.DoesNotContain("kid2", c.Users);
        Assert.Equal(["kid"], c.FileAccess.Single().Users);
    }

    [Fact]
    public void File_access_rule_replaces_the_previous_one_for_the_same_path_and_mode()
    {
        var c = NewConfig();
        ConfigEditor.SetFileAccess(c, @"D:\Private", FileAccessMode.Deny, FileAccessRights.Read, [], Sp);
        ConfigEditor.SetFileAccess(c, @"d:\private\", FileAccessMode.Deny, FileAccessRights.Full, [], Sp);
        ConfigEditor.SetFileAccess(c, @"D:\Private", FileAccessMode.Allow, FileAccessRights.Read, [], Sp);
        Assert.Equal(2, c.FileAccess.Count);
        Assert.Equal(FileAccessRights.Full, c.FileAccess.Single(r => r.Mode == FileAccessMode.Deny).Rights);
        Assert.Equal(ErrorCodes.NoRights, Code(() => ConfigEditor.SetFileAccess(c, @"D:\X", FileAccessMode.Deny, FileAccessRights.None, [], Sp)));
        Assert.Equal(ErrorCodes.PathInsideWindows, Code(() => ConfigEditor.SetFileAccess(c, @"C:\Windows\Temp", FileAccessMode.Deny, FileAccessRights.Full, [], Sp)));
        ConfigEditor.RemoveFileAccess(c, @"D:\Private");
        Assert.Empty(c.FileAccess);
    }

    [Fact]
    public void Blocked_files_need_valid_fingerprints()
    {
        var c = NewConfig();
        var ok = new FileHashEntry { FileName = "game.exe", Sha256 = new string('a', 64), Md5 = new string('b', 32), Size = 100 };
        ConfigEditor.AddBlockedFile(c, ok);
        Assert.Equal(new string('A', 64), c.BlockedFiles[0].Sha256);
        Assert.Equal(ErrorCodes.AlreadyInList, Code(() => ConfigEditor.AddBlockedFile(c, new FileHashEntry { FileName = "copy.exe", Sha256 = new string('A', 64), Md5 = new string('b', 32), Size = 100 })));
        Assert.Equal(ErrorCodes.InvalidFileHash, Code(() => ConfigEditor.AddBlockedFile(c, new FileHashEntry { FileName = "x.exe", Sha256 = "123", Md5 = new string('b', 32), Size = 1 })));
        Assert.Equal(ErrorCodes.InvalidFileHash, Code(() => ConfigEditor.AddBlockedFile(c, new FileHashEntry { FileName = "x.exe", Sha256 = new string('c', 64), Md5 = new string('b', 32), Size = 0 })));
        ConfigEditor.RemoveBlockedFile(c, new string('a', 64));
        Assert.Empty(c.BlockedFiles);
    }

    [Fact]
    public void Browser_policies_are_typed_as_written_and_removed_by_name()
    {
        var c = NewConfig();
        ConfigEditor.SetBrowserPolicy(c, BrowserKind.Chrome, "ForceYouTubeRestrict", "2");
        ConfigEditor.SetBrowserPolicy(c, BrowserKind.Chrome, "PasswordManagerEnabled", "false");
        ConfigEditor.SetBrowserPolicy(c, BrowserKind.Chrome, "HomepageLocation", "https://example.org");
        Assert.Equal(2, c.Browsers.Chrome.Policies["ForceYouTubeRestrict"].GetInt32());
        Assert.False(c.Browsers.Chrome.Policies["PasswordManagerEnabled"].GetBoolean());
        Assert.Equal("https://example.org", c.Browsers.Chrome.Policies["HomepageLocation"].GetString());
        Assert.Equal(ErrorCodes.InvalidPolicyName, Code(() => ConfigEditor.SetBrowserPolicy(c, BrowserKind.Edge, "bad name", "1")));
        ConfigEditor.RemoveBrowserPolicy(c, BrowserKind.Chrome, "forceyoutuberestrict");
        Assert.False(c.Browsers.Chrome.Policies.ContainsKey("ForceYouTubeRestrict"));
        Assert.Equal(ErrorCodes.NotInList, Code(() => ConfigEditor.RemoveBrowserPolicy(c, BrowserKind.Chrome, "Nope")));
    }

    [Fact]
    public void Extensions_are_validated_per_browser()
    {
        const string ChromeId = "cjpalhdlnbpafiamejdnhcphjbkeiagm";
        var c = NewConfig();
        ConfigEditor.AddExtension(c, BrowserKind.Chrome, ChromeId);
        Assert.Contains(ChromeId, c.Browsers.Chrome.ExtensionsAllowed);
        Assert.Equal(ErrorCodes.AlreadyInList, Code(() => ConfigEditor.AddExtension(c, BrowserKind.Chrome, ChromeId)));
        Assert.Equal(ErrorCodes.InvalidExtensionId, Code(() => ConfigEditor.AddExtension(c, BrowserKind.Edge, "not-an-id")));
        ConfigEditor.AddExtension(c, BrowserKind.Firefox, "uBlock0@raymondhill.net");
        ConfigEditor.AddExtension(c, BrowserKind.Firefox, "{12345678-1234-1234-1234-123456789abc}");
        ConfigEditor.RemoveExtension(c, BrowserKind.Chrome, ChromeId);
        Assert.Empty(c.Browsers.Chrome.ExtensionsAllowed);
    }
}
