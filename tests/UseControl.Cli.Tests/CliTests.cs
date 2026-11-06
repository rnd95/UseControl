using System.Text.Json;
using UseControl.Core.Configuration;
using UseControl.Core.Games;
using UseControl.Core.Paths;
using UseControl.Localization;

namespace UseControl.Cli.Tests;

[Collection("Culture")]   // Loc.Culture is global; these tests set it
public sealed class CliTests : IDisposable
{
    private readonly FakeService _svc = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public CliTests() => Loc.Culture = Loc.Pick("en");

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
    }

    private async Task<int> Run(string args, string input = "")
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var app = new CliApp(_svc, _out, _err, new StringReader(input), new FixedSystemPaths())
        {
            Fingerprint = (p, c) => new FileHashEntry { FileName = Path.GetFileName(p), Sha256 = new string('A', 64), Md5 = new string('B', 32), Size = 9, Category = c },
            StandardAccounts = () => ["kid", "guest2"],
        };
        return await app.RunAsync(Split(args));
    }

    // Splits like a shell: spaces separate, "double quotes" group.
    private static string[] Split(string s) =>
        [.. System.Text.RegularExpressions.Regex.Matches(s, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)];

    private string Out => _out.ToString();

    [Fact]
    public async Task Help_is_shown_without_a_command()
    {
        Assert.Equal(CliApp.Ok, await Run(""));
        Assert.Contains("uc games allow", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_commands_and_missing_arguments_are_usage_errors()
    {
        Assert.Equal(CliApp.Usage, await Run("frobnicate"));
        Assert.Equal(CliApp.Usage, await Run("block folder"));
        Assert.Equal(CliApp.Usage, await Run("users dance"));
    }

    [Fact]
    public async Task Users_are_added_listed_and_removed()
    {
        Assert.Equal(CliApp.Ok, await Run("users add kid"));
        Assert.Equal(["kid"], _svc.Config.Users);
        Assert.Contains("kid: 40 blocked", Out, StringComparison.Ordinal);
        Assert.Contains("signed in", Out, StringComparison.Ordinal);

        await Run("users list");
        Assert.Contains("Restricted accounts: kid (kid)", Out, StringComparison.Ordinal);
        Assert.Contains("guest2", Out, StringComparison.Ordinal);

        Assert.Equal(CliApp.Ok, await Run("users remove kid"));
        Assert.Equal(["kid"], _svc.RemovedUsers);
        Assert.Contains("restrictions removed", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folders_with_spaces_are_blocked_with_a_category()
    {
        _svc.Restrict("kid");
        Assert.Equal(CliApp.Ok, await Run("block folder \"F:\\My Games\" --category games"));
        Assert.Equal(RuleCategory.Games, _svc.Preset.BlockedPaths.Single(p => p.Path == @"F:\My Games").Category);
        Assert.Equal(CliApp.Ok, await Run("unblock folder F:\\My Games"));   // unquoted rest of the line works too
        Assert.DoesNotContain(_svc.Preset.BlockedPaths, p => p.Path == @"F:\My Games");
    }

    [Fact]
    public async Task Service_errors_are_shown_in_the_chosen_language()
    {
        _svc.Restrict("kid");
        Assert.Equal(CliApp.Failed, await Run("block folder C:\\Windows --lang uk"));
        Assert.Contains("Помилка", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("потрібна Windows", _err.ToString(), StringComparison.Ordinal);
        Loc.Culture = Loc.Pick("en");
        Assert.Equal(CliApp.Failed, await Run("block folder C:\\Windows"));
        Assert.Contains("needed by Windows", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_option_values_are_refused()
    {
        Assert.Equal(CliApp.Failed, await Run("block name x.exe --category nonsense"));
        Assert.Equal(CliApp.Failed, await Run("block name x.exe --category writable"));
        Assert.Equal(CliApp.Failed, await Run("access deny D:\\X --rights read,fly"));
        Assert.Equal(CliApp.Failed, await Run("ext allow opera abc"));
        Assert.Equal(0, _svc.SetConfigCalls);
    }

    [Fact]
    public async Task Program_files_are_blocked_by_fingerprint()
    {
        _svc.Restrict("kid");
        Assert.Equal(CliApp.Ok, await Run("block file D:\\games\\x\\game.exe --category games"));
        var f = _svc.Preset.BlockedFiles.Single();
        Assert.Equal("game.exe", f.FileName);
        Assert.Contains("fingerprint", Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, await Run("unblock file " + new string('A', 64)));
        Assert.Empty(_svc.Preset.BlockedFiles);
    }

    [Fact]
    public async Task File_access_rules_take_rights_and_accounts()
    {
        Assert.Equal(CliApp.Ok, await Run("access deny D:\\Private --rights read,write --users kid,teen"));
        var r = _svc.Config.FileAccess.Single();
        Assert.Equal(FileAccessRights.Read | FileAccessRights.Write, r.Rights);
        Assert.Equal(["kid", "teen"], r.Users);
        Assert.Equal(CliApp.Ok, await Run("access allow D:\\Shared"));
        Assert.Equal(FileAccessRights.Full, _svc.Config.FileAccess.Single(x => x.Mode == FileAccessMode.Allow).Rights);
        Assert.Equal(CliApp.Ok, await Run("access remove D:\\Private"));
        Assert.Single(_svc.Config.FileAccess);
    }

    [Fact]
    public async Task Extensions_and_policies_are_managed_per_browser()
    {
        _svc.Restrict("kid");
        Assert.Equal(CliApp.Ok, await Run("ext allow chrome cjpalhdlnbpafiamejdnhcphjbkeiagm"));
        Assert.Contains("cjpalhdlnbpafiamejdnhcphjbkeiagm", _svc.Preset.Browsers.Chrome.ExtensionsAllowed);
        Assert.Equal(CliApp.Ok, await Run("policy set edge HomepageLocation https://example.org"));
        Assert.Equal("https://example.org", _svc.Preset.Browsers.Edge.Policies["HomepageLocation"].GetString());
        Assert.Equal(CliApp.Ok, await Run("policy set firefox DisableTelemetry true"));
        Assert.True(_svc.Preset.Browsers.Firefox.Policies["DisableTelemetry"].GetBoolean());
        Assert.Equal(CliApp.Ok, await Run("policy remove edge HomepageLocation"));
        Assert.False(_svc.Preset.Browsers.Edge.Policies.ContainsKey("HomepageLocation"));
    }

    [Fact]
    public async Task Games_are_allowed_as_play_time_or_from_now_and_locked()
    {
        _svc.Restrict("kid").Restrict("teen");
        Assert.Equal(CliApp.Ok, await Run("games allow 1,5"));
        Assert.Equal(90, _svc.GameStates["kid"].PlayMinutesLeft);
        Assert.Equal(90, _svc.GameStates["teen"].PlayMinutesLeft);
        Assert.Contains("1 h 30 min of play left", Out, StringComparison.Ordinal);

        Assert.Equal(CliApp.Ok, await Run("games allow 0.5 --now --user teen"));
        Assert.NotNull(_svc.GameStates["teen"].AllowedUntil);
        Assert.DoesNotContain("[kid]", Out, StringComparison.Ordinal);

        Assert.Equal(CliApp.Failed, await Run("games allow 99"));
        Assert.Equal(CliApp.Failed, await Run("games allow lots"));
        Assert.Equal(CliApp.Failed, await Run("games allow 1 --user nobody"));
        Assert.Equal(CliApp.Ok, await Run("games lock --user kid"));
        Assert.Equal(0, _svc.GameStates["kid"].PlayMinutesLeft);
        Assert.Equal(CliApp.Ok, await Run("games"));
        Assert.Contains("[kid] games blocked", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schedule_windows_are_added_listed_and_removed()
    {
        _svc.Restrict("kid");
        Assert.Equal(CliApp.Ok, await Run("games schedule add mon-fri 16:00-20:00 --limit 60"));
        Assert.Equal(CliApp.Ok, await Run("games schedule add weekend 10:00-21:00"));
        Assert.Equal(GamesMode.Schedule, _svc.Preset.Games.Mode);
        var w = _svc.Preset.Games.Schedule[0];
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], w.Days);
        Assert.Equal(60, w.LimitMinutes);

        Assert.Equal(CliApp.Ok, await Run("games schedule"));
        Assert.Contains("1. Mon–Fri  16:00–20:00, up to 1 h", Out, StringComparison.Ordinal);
        Assert.Contains("2. Sat, Sun  10:00–21:00", Out, StringComparison.Ordinal);

        Assert.Equal(CliApp.Failed, await Run("games schedule add fri-mon 10:00-11:00"));
        Assert.Equal(CliApp.Failed, await Run("games schedule add mon 18:00-17:00"));
        Assert.Equal(CliApp.Ok, await Run("games schedule remove 1"));
        Assert.Single(_svc.Preset.Games.Schedule);
        Assert.Equal(CliApp.Ok, await Run("games mode manual"));
        Assert.Equal(GamesMode.Manual, _svc.Preset.Games.Mode);
        Assert.Equal(CliApp.Ok, await Run("games close off"));
        Assert.False(_svc.Preset.Games.CloseGamesWhenOver);
    }

    [Theory]
    [InlineData("mon-fri", 5)]
    [InlineData("sat,sun", 2)]
    [InlineData("mo,we,fr", 3)]
    [InlineData("all", 7)]
    [InlineData("weekdays,sat", 6)]
    public void Days_are_read_in_several_ways(string text, int count) => Assert.Equal(count, CliApp.ParseDays(text).Count);

    [Fact]
    public async Task Presets_are_created_shared_and_required_when_there_are_several()
    {
        Assert.Equal(CliApp.Failed, await Run("block name x.exe"));   // no preset yet
        Assert.Contains("no presets yet", _err.ToString(), StringComparison.OrdinalIgnoreCase);

        await Run("users add kid");
        Assert.Equal(CliApp.Ok, await Run("preset add Teens --from kid"));
        Assert.Equal(CliApp.Ok, await Run("users add teen --preset teens"));
        Assert.Equal(["teen"], _svc.Config.GetPreset("Teens").Accounts);

        Assert.Equal(CliApp.Failed, await Run("block name x.exe"));
        Assert.Contains("--preset", _err.ToString(), StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, await Run("block name x.exe --preset teens"));
        Assert.Contains(_svc.Config.GetPreset("Teens").BlockedNames, n => n.Name == "x.exe");
        Assert.DoesNotContain(_svc.Config.GetPreset("kid").BlockedNames, n => n.Name == "x.exe");

        Assert.Equal(CliApp.Ok, await Run("users move teen kid"));
        Assert.Equal(["kid", "teen"], _svc.Config.GetPreset("kid").Accounts);
        Assert.Equal(CliApp.Ok, await Run("preset rename Teens Older"));
        Assert.Equal(CliApp.Ok, await Run("preset list"));
        Assert.Contains("Older: accounts (none)", Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, await Run("preset remove older"));
        Assert.Single(_svc.Config.Presets);
    }

    [Fact]
    public async Task Found_games_are_added_only_with_add()
    {
        _svc.Restrict("kid");
        await Run("games find");
        Assert.Contains(@"D:\NewGames", Out, StringComparison.Ordinal);
        Assert.DoesNotContain(_svc.Preset.BlockedPaths, p => p.Path == @"D:\NewGames");
        await Run("games find --add");
        Assert.Equal(RuleCategory.Games, _svc.Preset.BlockedPaths.Single(p => p.Path == @"D:\NewGames").Category);
        await Run("games find");
        Assert.Contains("No new game folders", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suspend_asks_for_confirmation()
    {
        Assert.Equal(CliApp.Failed, await Run("suspend", "no\n"));
        Assert.True(_svc.Enabled);
        Assert.Equal(CliApp.Ok, await Run("suspend", "YES\n"));
        Assert.False(_svc.Enabled);
        await Run("users add kid");
        Assert.Contains("suspended", Out, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CliApp.Ok, await Run("apply"));
        Assert.True(_svc.Enabled);
        Assert.Equal(CliApp.Ok, await Run("suspend --yes"));
    }

    [Fact]
    public async Task Audit_fails_only_on_failures_and_can_print_json()
    {
        Assert.Equal(CliApp.Ok, await Run("audit"));
        Assert.Contains("[WARN", Out, StringComparison.Ordinal);
        Assert.Contains("2 checks: 0 failed, 1 warnings", Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, await Run("audit --json"));
        using var doc = JsonDocument.Parse(Out);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Status_and_preview_print_text_and_json()
    {
        await Run("users add kid");
        Assert.Equal(CliApp.Ok, await Run("status"));
        Assert.Contains("Restrictions: on", Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, await Run("status --json"));
        Assert.True(JsonDocument.Parse(Out).RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(CliApp.Ok, await Run("preview"));
        Assert.Contains(@"D:\", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_is_exported_and_imported()
    {
        var file = Path.Combine(Path.GetTempPath(), $"uc-cli-{Guid.NewGuid():N}.json");
        try
        {
            await Run("users add kid");
            Assert.Equal(CliApp.Ok, await Run($"config export \"{file}\""));
            var c = ConfigSerializer.Load(file);
            ConfigEditor.AddUser(c, "teen", "kid");
            ConfigSerializer.Save(c, file);
            Assert.Equal(CliApp.Ok, await Run($"config import \"{file}\""));
            Assert.Equal(["kid", "teen"], _svc.Config.Users);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
