using System.Text.Json;
using UseControl.Core.Configuration;
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
        Assert.Contains("Restricted accounts: kid", Out, StringComparison.Ordinal);
        Assert.Contains("guest2", Out, StringComparison.Ordinal);

        Assert.Equal(CliApp.Ok, await Run("users remove kid"));
        Assert.Equal(["kid"], _svc.RemovedUsers);
        Assert.Contains("restrictions removed", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folders_with_spaces_are_blocked_with_a_category()
    {
        Assert.Equal(CliApp.Ok, await Run("block folder \"F:\\My Games\" --category games"));
        Assert.Equal(RuleCategory.Games, _svc.Config.BlockedPaths.Single(p => p.Path == @"F:\My Games").Category);
        Assert.Equal(CliApp.Ok, await Run("unblock folder F:\\My Games"));   // unquoted rest of the line works too
        Assert.DoesNotContain(_svc.Config.BlockedPaths, p => p.Path == @"F:\My Games");
    }

    [Fact]
    public async Task Service_errors_are_shown_in_the_chosen_language()
    {
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
        Assert.Equal(CliApp.Ok, await Run("block file D:\\games\\x\\game.exe --category games"));
        var f = _svc.Config.BlockedFiles.Single();
        Assert.Equal("game.exe", f.FileName);
        Assert.Contains("fingerprint", Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, await Run("unblock file " + new string('A', 64)));
        Assert.Empty(_svc.Config.BlockedFiles);
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
        Assert.Equal(CliApp.Ok, await Run("ext allow chrome cjpalhdlnbpafiamejdnhcphjbkeiagm"));
        Assert.Contains("cjpalhdlnbpafiamejdnhcphjbkeiagm", _svc.Config.Browsers.Chrome.ExtensionsAllowed);
        Assert.Equal(CliApp.Ok, await Run("policy set edge HomepageLocation https://example.org"));
        Assert.Equal("https://example.org", _svc.Config.Browsers.Edge.Policies["HomepageLocation"].GetString());
        Assert.Equal(CliApp.Ok, await Run("policy set firefox DisableTelemetry true"));
        Assert.True(_svc.Config.Browsers.Firefox.Policies["DisableTelemetry"].GetBoolean());
        Assert.Equal(CliApp.Ok, await Run("policy remove edge HomepageLocation"));
        Assert.False(_svc.Config.Browsers.Edge.Policies.ContainsKey("HomepageLocation"));
    }

    [Fact]
    public async Task Games_are_allowed_with_comma_or_point_and_locked()
    {
        Assert.Equal(CliApp.Ok, await Run("games allow 1,5"));
        Assert.NotNull(_svc.GamesUntil);
        Assert.Equal(CliApp.Failed, await Run("games allow 99"));
        Assert.Equal(CliApp.Failed, await Run("games allow lots"));
        Assert.Equal(CliApp.Ok, await Run("games lock"));
        Assert.Null(_svc.GamesUntil);
    }

    [Fact]
    public async Task Found_games_are_added_only_with_add()
    {
        await Run("games find");
        Assert.Contains(@"D:\NewGames", Out, StringComparison.Ordinal);
        Assert.DoesNotContain(_svc.Config.BlockedPaths, p => p.Path == @"D:\NewGames");
        await Run("games find --add");
        Assert.Equal(RuleCategory.Games, _svc.Config.BlockedPaths.Single(p => p.Path == @"D:\NewGames").Category);
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
            c.Users = ["kid", "teen"];
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
