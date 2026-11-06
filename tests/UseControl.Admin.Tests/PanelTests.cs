using System.Globalization;
using UseControl.Admin.ViewModels;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Admin.ViewModels.Pages;
using UseControl.Cli.Tests;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Core.Games;
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

    public string? PromptAnswer { get; set; }

    public string? Prompt(string message, string initial) => PromptAnswer;
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
    public Task<ServiceStatus> AllowGamesAsync(string? user, double hours, bool countPlayOnly, CancellationToken ct = default) => Fail<ServiceStatus>();
    public Task<ServiceStatus> LockGamesAsync(string? user, CancellationToken ct = default) => Fail<ServiceStatus>();
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
    private PanelTheme? _appliedTheme;

    public PanelTests()
    {
        TranslationSource.Instance.Culture = Loc.Pick("en");
        _main = new MainViewModel(
            new PanelSession(_svc, _dialogs, new FixedSystemPaths()),
            () => ["kid", "teen"],
            (path, cat) => new FileHashEntry { FileName = Path.GetFileName(path), Sha256 = new string('C', 64), Md5 = new string('D', 32), Size = 5, Category = cat },
            c => _savedLanguage = c,
            new ThemeChoices(PanelTheme.System, t => _appliedTheme = t));
    }

    private PanelSession Session => _main.Session;

    /// <summary>Starts with "kid" restricted (its own preset), as most pages need a preset.</summary>
    private Task StartWithKid()
    {
        _svc.Restrict("kid");
        return _main.StartAsync();
    }

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
        Assert.Equal("teen", _svc.Config.PresetOf("teen")!.Name);   // its own preset
        Assert.Equal("teen", Session.PresetName);
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
        await Session.EditAsync(c => ConfigEditor.AddUser(c, "gone"));
        var row = Page<AccountsPageViewModel>().Accounts.Single(a => a.Name == "gone");
        Assert.True(row.IsRestricted);
        Assert.False(row.Exists);
        Assert.Equal("not found on this PC", row.StatusText);
    }

    [Fact]
    public async Task Picking_a_program_blocks_it_by_file_and_name_and_removing_takes_both_away()
    {
        await StartWithKid();
        var list = Page<ProgramsPageViewModel>().List;
        _dialogs.File = @"D:\x\portable.exe";
        await list.AddProgramCommand.ExecuteAsync(null);
        var file = _svc.Preset.BlockedFiles.Single();
        Assert.Equal(RuleCategory.Other, file.Category);
        Assert.Contains(_svc.Preset.BlockedNames, n => n.Name == "portable.exe" && n.Category == RuleCategory.Other);
        var row = list.Rows.Single(r => r.Text == "portable.exe");
        Assert.Equal(BlockKind.Program, row.Kind);   // one row for the file and its name

        await list.RemoveCommand.ExecuteAsync(row);
        Assert.Empty(_svc.Preset.BlockedFiles);
        Assert.DoesNotContain(_svc.Preset.BlockedNames, n => n.Name == "portable.exe");
    }

    [Fact]
    public async Task Games_and_programs_pages_each_show_their_own_entries()
    {
        await StartWithKid();
        var games = Page<GamesPageViewModel>().List;
        var programs = Page<ProgramsPageViewModel>().List;
        Assert.Contains(games.Rows, r => r.Text == "steam.exe");
        Assert.DoesNotContain(programs.Rows, r => r.Text == "steam.exe");
        Assert.Contains(programs.Rows, r => r.Text == "cmd.exe");
        Assert.DoesNotContain(games.Rows, r => r.Text == "cmd.exe");

        _dialogs.Folder = @"F:\My Games";
        await games.AddFolderCommand.ExecuteAsync(null);
        Assert.Equal(RuleCategory.Games, _svc.Preset.BlockedPaths.Single(p => p.Path == @"F:\My Games").Category);
        Assert.Contains(games.Rows, r => r.Kind == BlockKind.Folder && r.Key == @"F:\My Games");

        await games.RemoveCommand.ExecuteAsync(games.Rows.Single(r => r.Key == @"F:\My Games"));
        Assert.DoesNotContain(_svc.Preset.BlockedPaths, p => p.Path == @"F:\My Games");
    }

    [Fact]
    public async Task Invalid_input_shows_a_translated_error_and_keeps_the_text()
    {
        await StartWithKid();
        var list = Page<ProgramsPageViewModel>().List;
        TranslationSource.Instance.Culture = Loc.Pick("uk");
        list.NewName = "readme.txt";
        await list.AddNameCommand.ExecuteAsync(null);
        Assert.Equal(MessageKind.Error, Session.MessageKind);
        Assert.Contains("не назва програми", Session.Message, StringComparison.Ordinal);
        Assert.Equal("readme.txt", list.NewName);
        Assert.False(Session.IsBusy);

        list.NewName = "badtool.exe";
        await list.AddNameCommand.ExecuteAsync(null);
        Assert.Contains(_svc.Preset.BlockedNames, n => n.Name == "badtool.exe");
        Assert.Equal("", list.NewName);
    }

    [Fact]
    public async Task File_access_uses_presets_and_ticked_accounts()
    {
        await _main.StartAsync();
        await Session.EditAsync(c =>
        {
            ConfigEditor.AddUser(c, "kid");
            ConfigEditor.AddUser(c, "teen");
        });
        var page = Page<FileAccessPageViewModel>();
        Assert.Equal(2, page.Accounts.Count);

        _dialogs.Folder = @"D:\Private";
        page.PickFolderCommand.Execute(null);
        page.Preset = FileAccessPageViewModel.Presets.Single(p => p.Key == "ReadOnly");
        page.Accounts.Single(a => a.Name == "teen").IsSelected = false;
        await page.AddCommand.ExecuteAsync(null);
        var r = _svc.Config.FileAccess.Single();
        Assert.Equal(FileAccessMode.Deny, r.Mode);
        Assert.Equal(FileAccessRights.Write | FileAccessRights.Delete, r.Rights);
        Assert.Equal(["kid"], r.Users);
        Assert.Equal("Can open, cannot change or delete", page.Rules.Single().Rights);

        // Every account ticked = every restricted account, including ones added later.
        _dialogs.Folder = @"D:\Secret";
        page.PickFolderCommand.Execute(null);
        page.Preset = FileAccessPageViewModel.Presets[0];
        await page.AddCommand.ExecuteAsync(null);
        Assert.Empty(_svc.Config.FileAccess.Single(x => x.Path == @"D:\Secret").Users);

        await page.RemoveCommand.ExecuteAsync(page.Rules.First());
        Assert.Single(_svc.Config.FileAccess);
    }

    [Fact]
    public async Task Exceptions_are_managed_on_the_restrictions_page()
    {
        await StartWithKid();
        var page = Page<RestrictionsPageViewModel>();
        _dialogs.Folder = @"C:\Tools";
        await page.AllowFolderCommand.ExecuteAsync(null);
        Assert.Equal(@"C:\Tools", _svc.Preset.AllowedPaths.Single().Path);
        await page.RemoveAllowedFolderCommand.ExecuteAsync(page.AllowedFolders.Single());
        Assert.Empty(_svc.Preset.AllowedPaths);
    }

    [Fact]
    public async Task Browser_page_follows_the_selected_browser()
    {
        await StartWithKid();
        var page = Page<BrowsersPageViewModel>();
        Assert.Contains(page.Policies, p => p.Name == "IncognitoModeAvailability");

        page.PolicyName = "ForceYouTubeRestrict";
        page.PolicyValue = "2";
        await page.SetPolicyCommand.ExecuteAsync(null);
        Assert.Equal(2, _svc.Preset.Browsers.Chrome.Policies["ForceYouTubeRestrict"].GetInt32());

        page.Browser = BrowserKind.Edge;
        Assert.Contains(page.Policies, p => p.Name == "InPrivateModeAvailability");
        page.NewExtension = "cjpalhdlnbpafiamejdnhcphjbkeiagm";
        await page.AddExtensionCommand.ExecuteAsync(null);
        Assert.Contains("cjpalhdlnbpafiamejdnhcphjbkeiagm", _svc.Preset.Browsers.Edge.ExtensionsAllowed);

        page.Browser = BrowserKind.Firefox;
        Assert.True(page.IsFirefox);
        page.DisablePrivateBrowsing = false;   // switches save at once
        Assert.False(_svc.Preset.Browsers.Firefox.DisablePrivateBrowsing);
    }

    [Fact]
    public async Task Games_page_allows_and_blocks_each_account_and_blocks_found_games()
    {
        await StartWithKid();
        var page = Page<GamesPageViewModel>();
        var kid = page.Now.Single();
        Assert.Equal("kid", kid.User);
        Assert.False(kid.Allowed);
        Assert.Equal("blocked", kid.State);

        page.Hours = 2;
        page.CountPlayOnly = true;
        await page.AllowCommand.ExecuteAsync(kid);
        Assert.Equal(120, _svc.GameStates["kid"].PlayMinutesLeft);
        Assert.True(page.Now.Single().Allowed);
        Assert.Contains("2 h of play left", page.Now.Single().State, StringComparison.Ordinal);

        await page.LockCommand.ExecuteAsync(page.Now.Single());
        Assert.False(page.Now.Single().Allowed);

        await page.FindCommand.ExecuteAsync(null);
        Assert.Single(page.Found);
        await page.BlockSelectedCommand.ExecuteAsync(null);
        Assert.Equal(RuleCategory.Games, _svc.Preset.BlockedPaths.Single(p => p.Path == @"D:\NewGames").Category);
        Assert.Empty(page.Found);
    }

    [Fact]
    public async Task Games_schedule_is_built_from_days_hours_and_a_limit()
    {
        await StartWithKid();
        var page = Page<GamesPageViewModel>();
        Assert.True(page.IsManual);
        Assert.Equal(5, page.Days.Count(d => d.IsSelected));   // Monday to Friday at first

        page.From = page.Times.Single(t => t.Text == "16:00");
        page.To = page.Times.Single(t => t.Text == "20:00");
        page.Limit = page.Limits.Single(l => l.Value == 60);
        await page.AddWindowCommand.ExecuteAsync(null);
        Assert.True(page.IsSchedule);
        Assert.Equal(GamesMode.Schedule, _svc.Preset.Games.Mode);
        Assert.Equal("Mon–Fri  16:00–20:00, up to 1 h", page.Schedule.Single().Text);

        foreach (var d in page.Days) d.IsSelected = d.Day is DayOfWeek.Saturday or DayOfWeek.Sunday;
        page.Limit = page.Limits[0];
        await page.AddWindowCommand.ExecuteAsync(null);
        Assert.Equal("Sat, Sun  16:00–20:00", page.Schedule[1].Text);

        page.From = page.Times.Single(t => t.Text == "21:00");
        await page.AddWindowCommand.ExecuteAsync(null);   // ends before it starts
        Assert.Equal(MessageKind.Error, Session.MessageKind);
        Assert.Equal(2, _svc.Preset.Games.Schedule.Count);

        await page.RemoveWindowCommand.ExecuteAsync(page.Schedule[0]);
        Assert.Single(_svc.Preset.Games.Schedule);

        page.IsManual = true;   // saved at once
        await Task.Delay(50);
        Assert.Equal(GamesMode.Manual, _svc.Preset.Games.Mode);
        page.CloseGamesWhenOver = false;
        await Task.Delay(50);
        Assert.False(_svc.Preset.Games.CloseGamesWhenOver);
    }

    [Fact]
    public async Task Each_account_can_have_its_own_preset_or_share_one()
    {
        _svc.Restrict("kid");
        await _main.StartAsync();
        var accounts = Page<AccountsPageViewModel>();

        // "teen" joins the preset of "kid" instead of getting its own.
        var teen = accounts.Accounts.Single(a => a.Name == "teen");
        Assert.Null(teen.Preset.Name);   // own preset by default
        teen.Preset = teen.Presets.Single(p => p.Name == "kid");
        await accounts.RestrictCommand.ExecuteAsync(teen);
        Assert.Equal(["kid", "teen"], _svc.Config.GetPreset("kid").Accounts);

        // A new preset for teenagers, copied from the one shown; then "teen" moves there.
        _dialogs.PromptAnswer = "Teens";
        await _main.NewPresetCommand.ExecuteAsync(null);
        Assert.Equal("Teens", Session.PresetName);
        Assert.Equal(["kid", "Teens"], Session.PresetNames);
        Assert.Equal("No account uses this preset yet: choose it on the Accounts page.", _main.PresetAccounts);

        var row = accounts.Accounts.Single(a => a.Name == "teen");
        row.Preset = row.Presets.Single(p => p.Name == "Teens");
        await Task.Delay(50);
        Assert.Equal(["teen"], _svc.Config.GetPreset("Teens").Accounts);
        Assert.Equal("For: teen", _main.PresetAccounts);

        // Pages show and change only the chosen preset.
        await Page<ProgramsPageViewModel>().List.AddNameCommand.ExecuteAsync(null);   // nothing typed: nothing happens
        Page<ProgramsPageViewModel>().List.NewName = "tiktok.exe";
        await Page<ProgramsPageViewModel>().List.AddNameCommand.ExecuteAsync(null);
        Assert.Contains(_svc.Config.GetPreset("Teens").BlockedNames, n => n.Name == "tiktok.exe");
        Assert.DoesNotContain(_svc.Config.GetPreset("kid").BlockedNames, n => n.Name == "tiktok.exe");
        Session.PresetName = "kid";
        Assert.DoesNotContain(Page<ProgramsPageViewModel>().List.Rows, r => r.Text == "tiktok.exe");

        _dialogs.PromptAnswer = "Little ones";
        await _main.RenamePresetCommand.ExecuteAsync(null);
        Assert.Equal("Little ones", Session.PresetName);
        Assert.Equal(["kid"], _svc.Config.GetPreset("Little ones").Accounts);

        Session.PresetName = "Teens";
        await _main.DeletePresetCommand.ExecuteAsync(null);
        Assert.Contains(_dialogs.Questions, q => q.Contains("teen will no longer be restricted", StringComparison.Ordinal));
        Assert.Equal(["Little ones"], Session.PresetNames);
        Assert.Equal("Little ones", Session.PresetName);
        Assert.Equal(["kid"], _svc.Config.Users);
    }

    [Fact]
    public async Task Pages_of_a_preset_say_what_to_do_when_there_is_none()
    {
        await _main.StartAsync();
        Assert.Null(Session.PresetName);
        Assert.True(Page<GamesPageViewModel>().UsesPreset);
        Assert.False(Page<AccountsPageViewModel>().UsesPreset);
        Page<ProgramsPageViewModel>().List.NewName = "x.exe";
        await Page<ProgramsPageViewModel>().List.AddNameCommand.ExecuteAsync(null);
        Assert.Equal(MessageKind.Info, Session.MessageKind);
        Assert.Contains("restrict an account", Session.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Theme_is_chosen_on_the_settings_page()
    {
        await _main.StartAsync();
        var page = Page<SettingsPageViewModel>();
        Assert.Equal(PanelTheme.System, page.Theme!.Theme);
        Assert.Equal(["Like Windows", "Light", "Dark"], page.Themes.Select(t => t.Text));
        page.Theme = page.Themes.Single(t => t.Theme == PanelTheme.Dark);
        Assert.Equal(PanelTheme.Dark, _appliedTheme);
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
        await StartWithKid();
        var page = Page<RestrictionsPageViewModel>();
        Assert.True(page.DisableCommandPrompt);
        Assert.False(page.DisableTaskManager);
        page.DisableTaskManager = true;
        page.DisablePasswordChange = true;
        page.OtherDriveLetters = false;
        await page.SaveCommand.ExecuteAsync(null);
        Assert.True(_svc.Preset.Restrictions.DisableTaskManager);
        Assert.True(_svc.Preset.Restrictions.DisablePasswordChange);
        Assert.True(_svc.Preset.Restrictions.DisableCommandPrompt);
        Assert.False(_svc.Preset.Auto.OtherDriveLetters);

        // The Store setting is for the whole PC: on the Accounts page, saved when clicked.
        Page<AccountsPageViewModel>().BlockStoreInstalls = false;
        await Task.Delay(50);
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
        await StartWithKid();
        var browsers = Page<BrowsersPageViewModel>();
        Assert.False(browsers.AddExtensionCommand.CanExecute(null));
        Assert.False(browsers.SetPolicyCommand.CanExecute(null));
        browsers.NewExtension = "x";
        Assert.True(browsers.AddExtensionCommand.CanExecute(null));
        Assert.False(Page<ProgramsPageViewModel>().List.AddNameCommand.CanExecute(null));
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
        Assert.Contains(main.Pages.OfType<GamesPageViewModel>().Single().List.Rows, r => r.Kind == BlockKind.Program);

        var audit = main.Pages.OfType<AuditPageViewModel>().Single();
        await audit.RunCommand.ExecuteAsync(null);
        Assert.Equal(AuditStatus.Fail, audit.Checks[0].Status);
        Assert.Contains(@"BUILTIN\Users", audit.Checks[0].Detail, StringComparison.Ordinal);
    }
}
