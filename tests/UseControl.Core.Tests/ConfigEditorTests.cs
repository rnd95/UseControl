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
        ConfigEditor.AddUser(c, "kid");
        return c;
    }

    /// <summary>The preset of "kid" (the lists live in presets).</summary>
    private static Preset NewPreset() => NewConfig().Presets[0];

    private static string Code(Action a) => Assert.Throws<UcException>(a).Code;

    [Fact]
    public void Blocked_folder_is_added_normalised_and_removed()
    {
        var c = NewPreset();
        Assert.Equal(@"F:\Test Games", ConfigEditor.AddBlockedPath(c, @"f:\Test Games\", RuleCategory.Games, Sp));
        Assert.Equal(RuleCategory.Games, c.BlockedPaths.Single(e => e.Path == @"F:\Test Games").Category);
        ConfigEditor.RemoveBlockedPath(c, @"F:\TEST GAMES");
        Assert.DoesNotContain(c.BlockedPaths, e => e.Path == @"F:\Test Games");
    }

    [Fact]
    public void Category_of_a_folder_name_or_program_can_be_changed()
    {
        var c = NewPreset();
        ConfigEditor.AddBlockedPath(c, @"F:\Games", RuleCategory.Other, Sp);
        ConfigEditor.SetBlockedCategory(c, @"f:\games\", RuleCategory.Games);
        Assert.Equal(RuleCategory.Games, c.BlockedPaths.Single(e => e.Path == @"F:\Games").Category);

        ConfigEditor.AddBlockedFile(c, new FileHashEntry { FileName = "x.exe", Sha256 = new string('A', 64), Md5 = new string('B', 32), Size = 1 });
        ConfigEditor.AddBlockedName(c, "x.exe", RuleCategory.Other);
        ConfigEditor.SetBlockedCategory(c, new string('a', 64), RuleCategory.Games);
        Assert.Equal(RuleCategory.Games, c.BlockedFiles.Single().Category);
        Assert.Equal(RuleCategory.Games, c.BlockedNames.Single(n => n.Name == "x.exe").Category);   // the name goes with its file

        ConfigEditor.SetBlockedCategory(c, "X.EXE", RuleCategory.Vpn);
        Assert.Equal(RuleCategory.Vpn, c.BlockedNames.Single(n => n.Name == "x.exe").Category);
        Assert.Equal(ErrorCodes.NotInList, Code(() => ConfigEditor.SetBlockedCategory(c, "none.exe", RuleCategory.Games)));
    }

    [Fact]
    public void Blocked_folder_refuses_duplicates_protected_places_and_unknown_entries()
    {
        var c = NewPreset();
        ConfigEditor.AddBlockedPath(c, @"F:\X", RuleCategory.Games, Sp);
        Assert.Equal(ErrorCodes.AlreadyInList, Code(() => ConfigEditor.AddBlockedPath(c, @"F:\X\", RuleCategory.Games, Sp)));
        Assert.Equal(ErrorCodes.PathProtected, Code(() => ConfigEditor.AddBlockedPath(c, @"C:\Windows", RuleCategory.Games, Sp)));
        Assert.Equal(ErrorCodes.NotInList, Code(() => ConfigEditor.RemoveBlockedPath(c, @"F:\Not There")));
    }

    [Fact]
    public void Allowed_folder_is_added_but_never_a_whole_drive()
    {
        var c = NewPreset();
        ConfigEditor.AddAllowedPath(c, @"C:\Tools");
        Assert.Equal(ErrorCodes.AlreadyInList, Code(() => ConfigEditor.AddAllowedPath(c, @"c:\tools\")));
        Assert.Equal(ErrorCodes.WholeDriveNotAllowed, Code(() => ConfigEditor.AddAllowedPath(c, @"D:\")));
        ConfigEditor.RemoveAllowedPath(c, @"C:\Tools");
        Assert.Empty(c.AllowedPaths);
    }

    [Fact]
    public void Program_names_are_validated()
    {
        var c = NewPreset();
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
        var c = NewPreset();
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
        var c = NewPreset();
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
        var c = NewPreset();
        ConfigEditor.AddExtension(c, BrowserKind.Chrome, ChromeId);
        Assert.Contains(ChromeId, c.Browsers.Chrome.ExtensionsAllowed);
        Assert.Equal(ErrorCodes.AlreadyInList, Code(() => ConfigEditor.AddExtension(c, BrowserKind.Chrome, ChromeId)));
        Assert.Equal(ErrorCodes.InvalidExtensionId, Code(() => ConfigEditor.AddExtension(c, BrowserKind.Edge, "not-an-id")));
        ConfigEditor.AddExtension(c, BrowserKind.Firefox, "uBlock0@raymondhill.net");
        ConfigEditor.AddExtension(c, BrowserKind.Firefox, "{12345678-1234-1234-1234-123456789abc}");
        ConfigEditor.RemoveExtension(c, BrowserKind.Chrome, ChromeId);
        Assert.Empty(c.Browsers.Chrome.ExtensionsAllowed);
    }

    [Fact]
    public void A_new_account_gets_its_own_preset_with_the_recommended_settings()
    {
        var c = ConfigSerializer.LoadDefault();
        Assert.Empty(c.Presets);
        var p = ConfigEditor.AddUser(c, "alpine");
        Assert.Equal("alpine", p.Name);
        Assert.Equal(["alpine"], p.Accounts);
        Assert.Contains(p.BlockedNames, n => n.Name == "steam.exe");
        Assert.Same(p, c.PresetOf("ALPINE"));
    }

    [Fact]
    public void Accounts_can_share_a_preset_and_move_between_presets()
    {
        var c = NewConfig();
        ConfigEditor.AddUser(c, "kid2", "kid");
        Assert.Equal(["kid", "kid2"], c.GetPreset("kid").Accounts);
        var teen = ConfigEditor.AddPreset(c, "Teen", c.GetPreset("kid"));
        Assert.Empty(teen.Accounts);
        ConfigEditor.MoveUser(c, "kid2", "teen");
        Assert.Equal(["kid2"], teen.Accounts);
        Assert.Equal(["kid"], c.GetPreset("kid").Accounts);
        Assert.Equal(ErrorCodes.PresetNotFound, Code(() => ConfigEditor.AddUser(c, "kid3", "nope")));
        Assert.Equal(ErrorCodes.NotRestricted, Code(() => ConfigEditor.MoveUser(c, "nobody", "teen")));
    }

    [Fact]
    public void A_copied_preset_is_independent_of_the_original()
    {
        var c = NewConfig();
        var copy = ConfigEditor.AddPreset(c, "Copy", c.GetPreset("kid"));
        ConfigEditor.AddBlockedName(copy, "only-here.exe", RuleCategory.Games);
        Assert.DoesNotContain(c.GetPreset("kid").BlockedNames, n => n.Name == "only-here.exe");
    }

    [Fact]
    public void Preset_names_are_unique_and_checked()
    {
        var c = NewConfig();
        Assert.Equal(ErrorCodes.PresetExists, Code(() => ConfigEditor.AddPreset(c, "KID")));
        Assert.Equal(ErrorCodes.InvalidPresetName, Code(() => ConfigEditor.AddPreset(c, "  ")));
        Assert.Equal(ErrorCodes.InvalidPresetName, Code(() => ConfigEditor.AddPreset(c, new string('x', 41))));
        ConfigEditor.AddPreset(c, "Other");
        ConfigEditor.RenamePreset(c, "kid", "Kid");   // only the letter case
        Assert.Equal("Kid", c.Presets[0].Name);
        Assert.Equal(ErrorCodes.PresetExists, Code(() => ConfigEditor.RenamePreset(c, "kid", "other")));
    }

    [Fact]
    public void Deleting_a_preset_stops_restricting_its_accounts()
    {
        var c = NewConfig();
        ConfigEditor.SetFileAccess(c, @"D:\Private", FileAccessMode.Deny, FileAccessRights.Full, ["kid"], Sp);
        Assert.Equal(["kid"], ConfigEditor.RemovePreset(c, "kid"));
        Assert.Empty(c.Presets);
        Assert.Empty(c.Users);
        Assert.Empty(c.FileAccess.Single().Users);
    }

    [Fact]
    public void Removing_an_account_keeps_its_preset_for_later()
    {
        var c = NewConfig();
        ConfigEditor.RemoveUser(c, "kid");
        Assert.Empty(c.Users);
        Assert.Single(c.Presets);
        Assert.Same(c.Presets[0], ConfigEditor.AddUser(c, "kid"));   // back with the same settings
    }

    [Fact]
    public void Schedule_windows_are_checked()
    {
        var p = NewPreset();
        Assert.Empty(p.Games.Schedule);
        var w = ConfigEditor.AddGamesWindow(p, [DayOfWeek.Friday, DayOfWeek.Monday], "9:30", "24:00", 90);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], w.Days);
        Assert.Equal("09:30", w.From);
        Assert.Equal(ErrorCodes.WindowEndsBeforeStart, Code(() => ConfigEditor.AddGamesWindow(p, [DayOfWeek.Monday], "20:00", "16:00", 0)));
        Assert.Equal(ErrorCodes.InvalidTime, Code(() => ConfigEditor.AddGamesWindow(p, [DayOfWeek.Monday], "25:00", "26:00", 0)));
        Assert.Equal(ErrorCodes.InvalidTime, Code(() => ConfigEditor.AddGamesWindow(p, [DayOfWeek.Monday], "16:7", "18:00", 0)));
        Assert.Equal(ErrorCodes.NoDays, Code(() => ConfigEditor.AddGamesWindow(p, [], "16:00", "18:00", 0)));
        ConfigEditor.RemoveGamesWindow(p, 0);
        Assert.Empty(p.Games.Schedule);
        Assert.Equal(ErrorCodes.NotInList, Code(() => ConfigEditor.RemoveGamesWindow(p, 0)));
    }
}
