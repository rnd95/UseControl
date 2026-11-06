using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Validation;

namespace UseControl.Core.Tests;

public class ConfigSerializerTests
{
    private static readonly FixedSystemPaths Sp = new();

    [Fact]
    public void Default_config_loads_without_accounts_and_the_preset_template_is_complete()
    {
        var cfg = ConfigSerializer.LoadDefault();
        Assert.Equal(ConfigSerializer.CurrentSchemaVersion, cfg.SchemaVersion);
        Assert.Empty(cfg.Users);
        Assert.Empty(cfg.Presets);
        var c = ConfigSerializer.DefaultPreset("x");
        Assert.True(c.BlockedPaths.Count > 5);
        Assert.True(c.BlockedNames.Count > 10);
        Assert.True(c.Auto.SystemDriveRoot);
        Assert.True(c.Restrictions.DisableCommandPrompt);
        Assert.Equal(Games.GamesMode.Manual, c.Games.Mode);
        Assert.Equal("off", c.Browsers.Chrome.Policies["DnsOverHttpsMode"].GetString());
    }

    [Fact]
    public void Default_config_has_only_valid_and_allowed_entries()
    {
        var c = ConfigSerializer.DefaultPreset("x");
        foreach (var e in c.BlockedPaths) PathRules.EnsureCanBlock(e.Path, WindowsSystemPaths.Current);
        foreach (var e in c.BlockedNames) Assert.True(Validators.IsProgramName(e.Name), e.Name);
    }

    [Fact]
    public void Default_config_has_no_duplicates()
    {
        var c = ConfigSerializer.DefaultPreset("x");
        Assert.Equal(c.BlockedPaths.Count, c.BlockedPaths.Select(e => PathRules.Normalize(e.Path).ToUpperInvariant()).Distinct().Count());
        Assert.Equal(c.BlockedNames.Count, c.BlockedNames.Select(e => e.Name.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void PowerShell_version_config_imports()
    {
        var cfg = ConfigSerializer.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "legacy.config.sample.json"));
        Assert.Equal(ConfigSerializer.CurrentSchemaVersion, cfg.SchemaVersion);
        var c = Assert.Single(cfg.Presets);
        Assert.Contains(c.BlockedPaths, e => e.Category == RuleCategory.Vpn);
        Assert.Contains(c.BlockedNames, e => e.Name == "steam.exe" && e.Category == RuleCategory.Games);
        Assert.Empty(c.BlockedFiles);
        Assert.Empty(cfg.FileAccess);
    }

    [Fact]
    public void Oldest_format_with_single_user_is_migrated()
    {
        var c = ConfigSerializer.Parse("""{ "user": "alpine", "blockedPaths": [], "blockedNames": [], "browsers": {} }""");
        Assert.Equal(["alpine"], c.Users);
        Assert.Equal("alpine", c.Presets.Single().Name);
        Assert.Equal(ConfigSerializer.CurrentSchemaVersion, c.SchemaVersion);
    }

    [Fact]
    public void Schema_1_becomes_one_preset_with_every_account_and_its_settings()
    {
        var c = ConfigSerializer.Parse("""
            {
              "schemaVersion": 1,
              "users": ["kid", "kid2"],
              "blockStoreInstalls": false,
              "restrictions": { "disableTaskManager": true },
              "blockedNames": [ { "name": "game.exe", "category": "games" } ],
              "allowedPaths": [ { "path": "C:\\Tools" } ],
              "browsers": { "chrome": { "policies": { "IncognitoModeAvailability": 1 } } },
              "fileAccess": [ { "path": "D:\\Private", "users": ["kid"] } ]
            }
            """);
        var p = Assert.Single(c.Presets);
        Assert.Equal("Default", p.Name);
        Assert.Equal(["kid", "kid2"], p.Accounts);
        Assert.True(p.Restrictions.DisableTaskManager);
        Assert.Equal("game.exe", p.BlockedNames.Single().Name);
        Assert.Equal(@"C:\Tools", p.AllowedPaths.Single().Path);
        Assert.Equal(1, p.Browsers.Chrome.Policies["IncognitoModeAvailability"].GetInt32());
        Assert.False(c.BlockStoreInstalls);
        Assert.Equal(["kid"], c.FileAccess.Single().Users);
    }

    [Fact]
    public void Schema_1_without_accounts_or_settings_gets_no_preset()
    {
        Assert.Empty(ConfigSerializer.Parse("""{ "schemaVersion": 1, "users": [] }""").Presets);
    }

    [Fact]
    public void Missing_and_null_sections_get_defaults()
    {
        var cfg = ConfigSerializer.Parse("""{ "users": ["a"], "blockedPaths": null, "auto": null, "browsers": { "chrome": null }, "audit": { "admins": null } }""");
        var c = cfg.Presets.Single();
        Assert.Empty(c.BlockedPaths);
        Assert.True(c.Auto.WritableDriveRoots);
        Assert.True(c.Browsers.Chrome.BlockExtensions);
        Assert.Empty(cfg.Audit.Admins);
        Assert.Empty(c.AllowedPaths);
        Assert.Empty(c.Games.Schedule);
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
        var p = ConfigEditor.AddUser(c, "kid");
        c.Audit.PrivateFolders.Add(@"D:\Стас");
        ConfigEditor.AddAllowedPath(p, @"C:\Tools");
        ConfigEditor.SetFileAccess(c, @"D:\Стас", FileAccessMode.Deny, FileAccessRights.Read | FileAccessRights.Write, ["kid"], Sp);
        ConfigEditor.AddGamesWindow(p, [DayOfWeek.Saturday, DayOfWeek.Sunday], "10:00", "21:00", 180);
        ConfigEditor.AddBlockedFile(p, new FileHashEntry { FileName = "game.exe", Sha256 = new string('a', 64), Md5 = new string('b', 32), Size = 10, Category = RuleCategory.Games });

        var path = Path.Combine(Path.GetTempPath(), $"uc-test-{Guid.NewGuid():N}.json");
        try
        {
            ConfigSerializer.Save(c, path);
            var text = File.ReadAllText(path);
            Assert.Contains(@"D:\\Стас", text, StringComparison.Ordinal);   // readable, not \u0421...
            Assert.Contains("\"games\"", text, StringComparison.Ordinal);    // categories in lower case, as before

            Assert.Contains("\"saturday\"", text, StringComparison.Ordinal);
            var cfg = ConfigSerializer.Load(path);
            var d = cfg.Presets.Single();
            Assert.Equal(p.BlockedPaths.Count, d.BlockedPaths.Count);
            Assert.Equal(p.BlockedNames.Count, d.BlockedNames.Count);
            Assert.Equal(["kid"], cfg.Users);
            Assert.Equal(@"D:\Стас", cfg.Audit.PrivateFolders[0]);
            Assert.Equal(FileAccessRights.Read | FileAccessRights.Write, cfg.FileAccess[0].Rights);
            Assert.Equal(@"C:\Tools", d.AllowedPaths[0].Path);
            Assert.Equal(1, d.Browsers.Chrome.Policies["IncognitoModeAvailability"].GetInt32());
            Assert.Single(d.BlockedFiles);
            Assert.Equal(180, d.Games.Schedule.Single().LimitMinutes);
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
