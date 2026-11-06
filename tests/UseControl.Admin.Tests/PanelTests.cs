using System.Globalization;
using UseControl.Admin.ViewModels;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Admin.ViewModels.Pages;
using UseControl.Cli.Tests;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Validation;
using UseControl.Ipc;
using UseControl.Localization;
using UseControl.Windows;
using UseControl.Windows.Audit;

namespace UseControl.Admin.Tests;

internal sealed class FakeDialogs : IDialogService
{
    public bool Answer { get; set; } = true;
    public string? Folder { get; set; }
    public string? File { get; set; }
    public List<string> Questions { get; } = [];

    public bool Confirm(string message)
    {
        Questions.Add(message);
        return Answer;
    }

    public string? PickFolder(string title) => Folder;

    public string? PickFile(string title, string filter) => File;

    public string? PickSaveFile(string title, string filter, string defaultName) => File;
}

/// <summary>A service that is not there.</summary>
internal sealed class MissingService : IServiceClient
{
    private static Task<T> Fail<T>() => Task.FromException<T>(new UcException(ErrorCodes.ServiceNotRunning));
    public Task<PingResult> PingAsync(CancellationToken ct = default) => Fail<PingResult>();
    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default) => Fail<ServiceStatus>();
    public Task<UcConfig> GetConfigAsync(CancellationToken ct = default) => Fail<UcConfig>();
    public Task<SetConfigResult> SetConfigAsync(UcConfig config, CancellationToken ct = default) => Fail<SetConfigResult>();
    public Task<ApplyResult> ApplyAsync(CancellationToken ct = default) => Fail<ApplyResult>();
    public Task<ApplyResult> SuspendAsync(CancellationToken ct = default) => Fail<ApplyResult>();
    public Task<ServiceStatus> AllowGamesAsync(double hours, CancellationToken ct = default) => Fail<ServiceStatus>();
    public Task<ServiceStatus> LockGamesAsync(CancellationToken ct = default) => Fail<ServiceStatus>();
    public Task<PreviewResult> PreviewAsync(CancellationToken ct = default) => Fail<PreviewResult>();
    public Task<IReadOnlyList<AuditCheck>> AuditAsync(CancellationToken ct = default) => Fail<IReadOnlyList<AuditCheck>>();
    public Task<IReadOnlyList<string>> FindGamesAsync(CancellationToken ct = default) => Fail<IReadOnlyList<string>>();
}

[CollectionDefinition("Culture", DisableParallelization = true)]
public sealed class CultureTests;

[Collection("Culture")]
public sealed class PanelTests
{
    private readonly FakeService _svc = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MainViewModel _main;
    private CultureInfo? _savedLanguage;

    public PanelTests()
    {
        TranslationSource.Instance.Culture = Loc.Pick("en");
        _main = new MainViewModel(
            new PanelSession(_svc, _dialogs, new FixedSystemPaths()),
            () => ["kid", "teen"],
            (path, cat) => new FileHashEntry { FileName = Path.GetFileName(path), Sha256 = new string('C', 64), Md5 = new string('D', 32), Size = 5, Category = cat },
            c => _savedLanguage = c);
    }

    private PanelSession Session => _main.Session;

    private T Page<T>() where T : PageViewModel => _main.Pages.OfType<T>().Single();

    [Fact]
    public async Task Start_connects_and_fills_the_status_page()
    {
        await _main.StartAsync();
        Assert.True(Session.IsConnected);
        Assert.True(Session.CanEdit);
        var status = Page<StatusPageViewModel>();
        Assert.Contains(status.Summary, l => l.Value == "on");
    }

    [Fact]
    public async Task Missing_service_is_shown_in_the_message_bar()
    {
        var main = new MainViewModel(new PanelSession(new MissingService(), _dialogs, new FixedSystemPaths()), () => [], (p, c) => null!, _ => { });
        await main.StartAsync();
        Assert.False(main.Session.IsConnected);
        Assert.False(main.Session.CanEdit);
        Assert.Equal(MessageKind.Error, main.Session.MessageKind);
        Assert.Contains("not running", main.Session.Message, StringComparison.Ordinal);
        Assert.False(main.Session.IsBusy);
    }

    [Fact]
    public async Task Every_account_has_its_own_button_and_restricted_ones_come_first()
    {
        await _main.StartAsync();
        var page = Page<AccountsPageViewModel>();
        Assert.Equal(["kid", "teen"], page.Accounts.Select(a => a.Name));
        Assert.All(page.Accounts, a => Assert.False(a.IsRestricted));

        await page.RestrictCommand.ExecuteAsync(page.Accounts.Single(a => a.Name == "teen"));
        Assert.Equal(["teen"], _svc.Config.Users);
        Assert.Equal("teen", page.Accounts[0].Name);
        Assert.True(page.Accounts[0].IsRestricted);
        Assert.Equal("restricted", page.Accounts[0].StatusText);
        Assert.Equal(MessageKind.Success, Session.MessageKind);

        _dialogs.Answer = false;
        await page.UnrestrictCommand.ExecuteAsync(page.Accounts[0]);
        Assert.Equal(["teen"], _svc.Config.Users);
        _dialogs.Answer = true;
        await page.UnrestrictCommand.ExecuteAsync(page.Accounts[0]);
        Assert.Empty(_svc.Config.Users);
    }

    [Fact]
    public async Task Restricted_accounts_missing_on_the_PC_are_shown_so_they_can_be_removed()
    {
        await _main.StartAsync();
        await Session.EditAsync(c => c.Users.Add("gone"));
        var row = Page<AccountsPageViewModel>().Accounts.Single(a => a.Name == "gone");
        Assert.True(row.IsRestricted);
        Assert.False(row.Exists);
        Assert.Equal("not found on this PC", row.StatusText);
    }

    [Fact]
    public async Task Programs_page_blocks_and_unblocks_with_the_chosen_category()
    {
        await _main.StartAsync();
        var page = Page<ProgramsPageViewModel>();
        page.Category = page.Categories.Single(c => c.Value == RuleCategory.Vpn);

        _dialogs.Folder = @"F:\Some VPN";
        await page.BlockFolderCommand.ExecuteAsync(null);
        Assert.Equal(RuleCategory.Vpn, _svc.Config.BlockedPaths.Single(p => p.Path == @"F:\Some VPN").Category);
        var row = page.BlockedFolders.Single(r => r.Key == @"F:\Some VPN");
        await page.RemoveBlockedFolderCommand.ExecuteAsync(row);
        Assert.DoesNotContain(_svc.Config.BlockedPaths, p => p.Path == @"F:\Some VPN");

        page.NewName = "badgame.exe";
        await page.BlockNameCommand.ExecuteAsync(null);
        Assert.Contains(_svc.Config.BlockedNames, n => n.Name == "badgame.exe");
        Assert.Equal("", page.NewName);

        _dialogs.File = @"D:\x\portable.exe";
        await page.BlockFileCommand.ExecuteAsync(null);
        Assert.Equal("portable.exe", _svc.Config.BlockedFiles.Single().FileName);
        Assert.Single(page.BlockedFiles);
    }

    [Fact]
    public async Task Invalid_input_shows_a_translated_error_and_keeps_the_text()
    {
        await _main.StartAsync();
        var page = Page<ProgramsPageViewModel>();
        TranslationSource.Instance.Culture = Loc.Pick("uk");
        page.NewName = "readme.txt";
        await page.BlockNameCommand.ExecuteAsync(null);
        Assert.Equal(MessageKind.Error, Session.MessageKind);
        Assert.Contains("не назва програми", Session.Message, StringComparison.Ordinal);
        Assert.Equal("readme.txt", page.NewName);
        Assert.False(Session.IsBusy);
    }

    [Fact]
    public async Task File_access_rules_are_built_from_the_form()
    {
        await _main.StartAsync();
        var page = Page<FileAccessPageViewModel>();
        _dialogs.Folder = @"D:\Private";
        page.PickFolderCommand.Execute(null);
        page.Execute = false;
        page.Delete = false;
        page.Users = "kid, teen";
        await page.AddCommand.ExecuteAsync(null);
        var r = _svc.Config.FileAccess.Single();
        Assert.Equal(FileAccessMode.Deny, r.Mode);
        Assert.Equal(FileAccessRights.Read | FileAccessRights.Write, r.Rights);
        Assert.Equal(["kid", "teen"], r.Users);
        Assert.Equal("", page.Path);

        await page.RemoveCommand.ExecuteAsync(page.Rules.Single());
        Assert.Empty(_svc.Config.FileAccess);
    }

    [Fact]
    public async Task Browser_page_follows_the_selected_browser()
    {
        await _main.StartAsync();
        var page = Page<BrowsersPageViewModel>();
        Assert.Contains(page.Policies, p => p.Name == "IncognitoModeAvailability");

        page.PolicyName = "ForceYouTubeRestrict";
        page.PolicyValue = "2";
        await page.SetPolicyCommand.ExecuteAsync(null);
        Assert.Equal(2, _svc.Config.Browsers.Chrome.Policies["ForceYouTubeRestrict"].GetInt32());

        page.Browser = BrowserKind.Edge;
        Assert.Contains(page.Policies, p => p.Name == "InPrivateModeAvailability");
        page.NewExtension = "cjpalhdlnbpafiamejdnhcphjbkeiagm";
        await page.AddExtensionCommand.ExecuteAsync(null);
        Assert.Contains("cjpalhdlnbpafiamejdnhcphjbkeiagm", _svc.Config.Browsers.Edge.ExtensionsAllowed);

        page.Browser = BrowserKind.Firefox;
        Assert.True(page.IsFirefox);
        page.DisablePrivateBrowsing = false;   // switches save at once
        Assert.False(_svc.Config.Browsers.Firefox.DisablePrivateBrowsing);
    }

    [Fact]
    public async Task Games_page_allows_locks_and_blocks_found_games()
    {
        await _main.StartAsync();
        var page = Page<GamesPageViewModel>();
        page.Hours = 2;
        await page.AllowCommand.ExecuteAsync(null);
        Assert.NotNull(_svc.GamesUntil);
        Assert.True(page.GamesAllowed);
        await page.LockCommand.ExecuteAsync(null);
        Assert.False(page.GamesAllowed);

        await page.FindCommand.ExecuteAsync(null);
        Assert.Single(page.Found);
        await page.BlockSelectedCommand.ExecuteAsync(null);
        Assert.Equal(RuleCategory.Games, _svc.Config.BlockedPaths.Single(p => p.Path == @"D:\NewGames").Category);
        Assert.Empty(page.Found);
    }

    [Fact]
    public async Task Status_page_suspends_only_after_confirmation()
    {
        await _main.StartAsync();
        var page = Page<StatusPageViewModel>();
        _dialogs.Answer = false;
        await page.SuspendCommand.ExecuteAsync(null);
        Assert.True(_svc.Enabled);
        _dialogs.Answer = true;
        await page.SuspendCommand.ExecuteAsync(null);
        Assert.False(_svc.Enabled);
        Assert.False(page.IsEnabled);
        await page.ApplyCommand.ExecuteAsync(null);
        Assert.True(_svc.Enabled);
    }

    [Fact]
    public async Task Restrictions_page_switches_windows_tools_and_automatic_blocking()
    {
        await _main.StartAsync();
        var page = Page<RestrictionsPageViewModel>();
        Assert.True(page.DisableCommandPrompt);
        Assert.False(page.DisableTaskManager);
        page.DisableTaskManager = true;
        page.DisablePasswordChange = true;
        page.OtherDriveLetters = false;
        page.BlockStoreInstalls = false;
        await page.SaveCommand.ExecuteAsync(null);
        Assert.True(_svc.Config.Restrictions.DisableTaskManager);
        Assert.True(_svc.Config.Restrictions.DisablePasswordChange);
        Assert.True(_svc.Config.Restrictions.DisableCommandPrompt);
        Assert.False(_svc.Config.Auto.OtherDriveLetters);
        Assert.False(_svc.Config.BlockStoreInstalls);
    }

    [Fact]
    public async Task Security_check_expectations_are_saved_one_per_line()
    {
        await _main.StartAsync();
        var page = Page<AuditPageViewModel>();
        page.Admins = "Stanislav\r\nOtherAdmin";
        page.KnownRemoteServices = "Mesh Agent, TeamViewer";
        await page.SaveExpectationsCommand.ExecuteAsync(null);
        Assert.Equal(["Stanislav", "OtherAdmin"], _svc.Config.Audit.Admins);
        Assert.Equal(["Mesh Agent", "TeamViewer"], _svc.Config.Audit.KnownRemoteServices);
    }

    [Fact]
    public async Task Empty_fields_keep_their_buttons_disabled()
    {
        await _main.StartAsync();
        var browsers = Page<BrowsersPageViewModel>();
        Assert.False(browsers.AddExtensionCommand.CanExecute(null));
        Assert.False(browsers.SetPolicyCommand.CanExecute(null));
        browsers.NewExtension = "x";
        Assert.True(browsers.AddExtensionCommand.CanExecute(null));
        Assert.False(Page<ProgramsPageViewModel>().BlockNameCommand.CanExecute(null));
        Assert.False(Page<FileAccessPageViewModel>().AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_language_switches_live()
    {
        await _main.StartAsync();
        var page = Page<SettingsPageViewModel>();
        Assert.Equal("0.1.0", page.ServiceVersion);

        var status = Page<StatusPageViewModel>();
        Assert.Equal("Status", status.Title);
        page.Language = page.Languages.Single(l => l.Culture.Name == "uk");
        Assert.Equal("Стан", status.Title);
        Assert.Contains(status.Summary, l => l.Value == "увімкнено");
        Assert.Equal("uk", _savedLanguage?.Name);
        Assert.Equal("Стан", TranslationSource.Instance["Ui.Nav.Status"]);
    }

    [Fact]
    public async Task Audit_rows_are_sorted_worst_first_with_translated_names()
    {
        await _main.StartAsync();
        var page = Page<AuditPageViewModel>();
        await page.RunCommand.ExecuteAsync(null);
        Assert.Equal(AuditStatus.Warning, page.Checks[0].Status);
        Assert.Equal("UAC", page.Checks[0].Name);
        Assert.Equal("EnableLUA=1", page.Checks[0].Detail);   // no translation key: the English text is shown
        Assert.Contains("1 warnings", page.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demo_service_fills_every_page()
    {
        var main = new MainViewModel(new PanelSession(new ViewModels.Demo.DemoServiceClient(), _dialogs, WindowsSystemPaths.Current), () => ["kids"], (p, c) => null!, _ => { });
        await main.StartAsync();
        Assert.True(main.Session.IsConnected);
        Assert.NotEmpty(main.Pages.OfType<FileAccessPageViewModel>().Single().Rules);
        Assert.NotEmpty(main.Pages.OfType<ProgramsPageViewModel>().Single().BlockedFiles);

        var audit = main.Pages.OfType<AuditPageViewModel>().Single();
        await audit.RunCommand.ExecuteAsync(null);
        Assert.Equal(AuditStatus.Fail, audit.Checks[0].Status);
        Assert.Contains(@"BUILTIN\Users", audit.Checks[0].Detail, StringComparison.Ordinal);
    }
}
