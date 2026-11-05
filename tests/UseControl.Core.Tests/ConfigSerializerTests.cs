using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Validation;

namespace UseControl.Core.Tests;

public class ConfigSerializerTests
{
    private static readonly FixedSystemPaths Sp = new();

    [Fact]
    public void Default_config_loads_without_accounts()
    {
        var c = ConfigSerializer.LoadDefault();
        Assert.Equal(ConfigSerializer.CurrentSchemaVersion, c.SchemaVersion);
        Assert.Empty(c.Users);
        Assert.True(c.BlockedPaths.Count > 5);
        Assert.True(c.BlockedNames.Count > 10);
        Assert.True(c.Auto.SystemDriveRoot);
        Assert.Equal("off", c.Browsers.Chrome.Policies["DnsOverHttpsMode"].GetString());
    }

    [Fact]
    public void Default_config_has_only_valid_and_allowed_entries()
    {
        var c = ConfigSerializer.LoadDefault();
        foreach (var e in c.BlockedPaths) PathRules.EnsureCanBlock(e.Path, WindowsSystemPaths.Current);
        foreach (var e in c.BlockedNames) Assert.True(Validators.IsProgramName(e.Name), e.Name);
    }

    [Fact]
    public void Default_config_has_no_duplicates()
    {
        var c = ConfigSerializer.LoadDefault();
        Assert.Equal(c.BlockedPaths.Count, c.BlockedPaths.Select(e => PathRules.Normalize(e.Path).ToUpperInvariant()).Distinct().Count());
        Assert.Equal(c.BlockedNames.Count, c.BlockedNames.Select(e => e.Name.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void PowerShell_version_config_imports()
    {
        var c = ConfigSerializer.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "legacy.config.sample.json"));
        Assert.Equal(1, c.SchemaVersion);
        Assert.Contains(c.BlockedPaths, e => e.Category == RuleCategory.Vpn);
        Assert.Contains(c.BlockedNames, e => e.Name == "steam.exe" && e.Category == RuleCategory.Games);
        Assert.Empty(c.BlockedFiles);
        Assert.Empty(c.FileAccess);
    }

    [Fact]
    public void Oldest_format_with_single_user_is_migrated()
    {
        var c = ConfigSerializer.Parse("""{ "user": "alpine", "blockedPaths": [], "blockedNames": [], "browsers": {} }""");
        Assert.Equal(["alpine"], c.Users);
        Assert.Equal(ConfigSerializer.CurrentSchemaVersion, c.SchemaVersion);
    }

    [Fact]
    public void Missing_and_null_sections_get_defaults()
    {
        var c = ConfigSerializer.Parse("""{ "users": ["a"], "blockedPaths": null, "auto": null, "browsers": { "chrome": null }, "audit": { "admins": null } }""");
        Assert.Empty(c.BlockedPaths);
        Assert.True(c.Auto.WritableDriveRoots);
        Assert.True(c.Browsers.Chrome.BlockExtensions);
        Assert.Empty(c.Audit.Admins);
        Assert.Empty(c.AllowedPaths);
    }

    [Fact]
    public void Newer_schema_is_refused()
    {
        var e = Assert.Throws<UcException>(() => ConfigSerializer.Parse("""{ "schemaVersion": 999 }"""));
        Assert.Equal(ErrorCodes.ConfigTooNew, e.Code);
    }

    [Fact]
    public void Broken_json_gives_a_readable_error()
    {
        var e = Assert.Throws<UcException>(() => ConfigSerializer.Parse("{ users: ", "my.json"));
        Assert.Equal(ErrorCodes.ConfigInvalid, e.Code);
        Assert.Contains("my.json", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted()
    {
        var c = ConfigSerializer.Parse("""
            {
              // restricted accounts
              "users": ["kid",],
            }
            """);
        Assert.Equal(["kid"], c.Users);
    }

    [Fact]
    public void Save_and_load_keep_everything_including_cyrillic()
    {
        var c = ConfigSerializer.LoadDefault();
        c.Users.Add("kid");
        c.Audit.PrivateFolders.Add(@"D:\Стас");
        ConfigEditor.AddAllowedPath(c, @"C:\Tools");
        ConfigEditor.SetFileAccess(c, @"D:\Стас", FileAccessMode.Deny, FileAccessRights.Read | FileAccessRights.Write, ["kid"], Sp);
        ConfigEditor.AddBlockedFile(c, new FileHashEntry { FileName = "game.exe", Sha256 = new string('a', 64), Md5 = new string('b', 32), Size = 10, Category = RuleCategory.Games });

        var path = Path.Combine(Path.GetTempPath(), $"uc-test-{Guid.NewGuid():N}.json");
        try
        {
            ConfigSerializer.Save(c, path);
            var text = File.ReadAllText(path);
            Assert.Contains(@"D:\\Стас", text, StringComparison.Ordinal);   // readable, not \u0421...
            Assert.Contains("\"games\"", text, StringComparison.Ordinal);    // categories in lower case, as before

            var d = ConfigSerializer.Load(path);
            Assert.Equal(c.BlockedPaths.Count, d.BlockedPaths.Count);
            Assert.Equal(c.BlockedNames.Count, d.BlockedNames.Count);
            Assert.Equal(["kid"], d.Users);
            Assert.Equal(@"D:\Стас", d.Audit.PrivateFolders[0]);
            Assert.Equal(FileAccessRights.Read | FileAccessRights.Write, d.FileAccess[0].Rights);
            Assert.Equal(@"C:\Tools", d.AllowedPaths[0].Path);
            Assert.Equal(1, d.Browsers.Chrome.Policies["IncognitoModeAvailability"].GetInt32());
            Assert.Single(d.BlockedFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_file_is_reported()
    {
        var e = Assert.Throws<UcException>(() => ConfigSerializer.Load(@"C:\does\not\exist.json"));
        Assert.Equal(ErrorCodes.ConfigNotFound, e.Code);
    }
}
