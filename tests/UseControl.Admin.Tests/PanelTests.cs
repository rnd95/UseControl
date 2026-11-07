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
    public List<string> Folders { get; set; } = [];
    public List<string> Files { get; set; } = [];
    public string? File { get; set; }
    public List<string> Questions { get; } = [];

    public bool Confirm(string message)
    {
        Questions.Add(message);
        return Answer;
    }

    public IReadOnlyList<string> PickFolders(string title) => Folders;

    public string? PickFile(string title, string filter) => File;

    public IReadOnlyList<string> PickFiles(string title, string filter) => Files;

    public string? PickSaveFile(string title, string filter, string defaultName) => File;

    public List<string> OpenedUrls { get; } = [];

    public void OpenUrl(string url) => OpenedUrls.Add(url);

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
    public Task<ServiceStatus> UnlockGamesAsync(string? user, CancellationToken ct = default) => Fail<ServiceStatus>();
    public Task<ServiceStatus> LockGamesAsync(string? user, CancellationToken ct = default) => Fail<ServiceStatus>();
    public Task<PreviewResult> PreviewAsync(CancellationToken ct = default) => Fail<PreviewResult>();
    public Task<IReadOnlyList<AuditCheck>> AuditAsync(CancellationToken ct = default) => Fail<IReadOnlyList<AuditCheck>>();
    public Task<IReadOnlyList<string>> FindGamesAsync(CancellationToken ct = default) => Fail<IReadOnlyList<string>>();
    public Task<UpdateStatus> CheckUpdateAsync(CancellationToken ct = default) => Fail<UpdateStatus>();
    public Task<UpdateStatus> InstallUpdateAsync(CancellationToken ct = default) => Fail<UpdateStatus>();
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
            (path, cat) => new FileHashEntry { FileName = Path.GetFileName(path), Sha256 = Hash(path), Md5 = new string('D', 32), Size = 5, Category = cat },
            c => _savedLanguage = c,
            new ThemeChoices(PanelTheme.System, t => _appliedTheme = t));
    }

    /// <summary>A fake SHA-256 that differs per file.</summary>
    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)));

    private PanelSession Session => _main.Session;

    /// <summary>Starts with "kid" restricted (its own preset), as most pages need a preset.</summary>
    private Task StartWithKid()
    {
        _svc.Restrict("kid");
        return _main.StartAsync();
    }

    private T Page<T>() where T : PageViewModel => _main.Pages.OfType<T>().Single();

    private AppListViewModel Apps => Page<AppsPageViewModel>().Apps;

    private GameTimeViewModel GameTime => Page<AppsPageViewModel>().GameTime;

    [Fact]
    public async Task Start_connects_and_fills_the_overview()
    {
        await StartWithKid();
        Assert.True(Session.IsConnected);
        Assert.True(Session.CanEdit);
        var status = Page<StatusPageViewModel>();
        Assert.Equal("Protection is on", status.Headline);
        Assert.StartsWith("Service 0.1.0", status.Details, StringComparison.Ordinal);
        var kid = status.Accounts.Single();
        Assert.Equal("kid", kid.User);
        Assert.Equal("kid", kid.Profile);
        Assert.Equal("blocked: the schedule is empty", kid.Games);
        Assert.False(kid.GamesAllowed);
    }

    [Fact]
    public async Task Navigation_is_grouped_into_general_profile_and_this_PC()
    {
        await _main.StartAsync();
        Assert.Equal(
            [NavSection.General, NavSection.General, NavSection.Profile, NavSection.Profile, NavSection.Profile, NavSection.Computer, NavSection.Computer, NavSection.Computer],
            _main.Pages.Select(p => p.Section));
        Assert.Equal("PROFILE", NavSection.Profile.Title);
        Assert.Equal("", NavSection.General.Title);

        // The line under the title: what the page is for, or whom the profile applies to.
        Assert.Equal("Protection status of this PC", _main.PageSubtitle);
        _main.SelectedPage = Page<AppsPageViewModel>();
        Assert.Equal(_main.PresetAccounts, _main.PageSubtitle);
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
        Assert.Equal("Restricted", page.Accounts[0].StatusText);
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
        Assert.Equal("Not found on this PC", row.StatusText);
    }

    [Fact]
    public async Task Several_programs_are_blocked_at_once_by_file_and_name_and_removing_takes_both_away()
    {
        await StartWithKid();
        _dialogs.Files = [@"D:\x\portable.exe", @"D:\y\other.exe"];
        await Apps.AddProgramsCommand.ExecuteAsync(null);
        Assert.Equal(2, _svc.Preset.BlockedFiles.Count);
        Assert.All(_svc.Preset.BlockedFiles, f => Assert.Equal(RuleCategory.Other, f.Category));   // "always blocked" unless chosen otherwise
        Assert.Contains(_svc.Preset.BlockedNames, n => n.Name == "portable.exe" && n.Category == RuleCategory.Other);
        var row = Apps.Rows.Single(r => r.Text == "portable.exe");
        Assert.Equal(BlockKind.Program, row.Kind);   // one row for the file and its name
        Assert.False(row.IsGame);

        await Apps.AddProgramsCommand.ExecuteAsync(null);   // the same files again: nothing new, no error
        Assert.Equal(2, _svc.Preset.BlockedFiles.Count);
        Assert.NotEqual(MessageKind.Error, Session.MessageKind);

        await Apps.RemoveCommand.ExecuteAsync(row);
        Assert.Single(_svc.Preset.BlockedFiles);
        Assert.DoesNotContain(_svc.Preset.BlockedNames, n => n.Name == "portable.exe");
    }

    [Fact]
    public async Task One_list_holds_games_and_blocked_programs_each_with_its_rule()
    {
        await StartWithKid();
        Assert.True(Apps.Rows.Single(r => r.Text == "steam.exe").IsGame);
        Assert.False(Apps.Rows.Single(r => r.Text == "cmd.exe").IsGame);
        Assert.True(Apps.Rows[0].IsGame);   // games first

        // The filter shows one kind; adding while it is on adds that kind.
        Apps.Filter = Apps.Filters.Single(f => f.Value == AppFilter.Games);
        Assert.All(Apps.Rows, r => Assert.True(r.IsGame));
        Assert.True(Apps.NewRule!.IsGame);
        _dialogs.Folders = [@"F:\My Games", @"F:\More Games"];
        await Apps.AddFoldersCommand.ExecuteAsync(null);
        Assert.All(_svc.Preset.BlockedPaths.Where(p => p.Path.StartsWith(@"F:\", StringComparison.Ordinal)), p => Assert.Equal(RuleCategory.Games, p.Category));
        Assert.Contains(Apps.Rows, r => r.Kind == BlockKind.Folder && r.Key == @"F:\More Games");
        Assert.Equal(AppFilter.Games, Apps.Filter!.Value);   // kept after the reload

        // Changing the rule of a row moves it to the other kind.
        var row = Apps.Rows.Single(r => r.Key == @"F:\My Games");
        row.Rule = row.Rules.Single(r => !r.IsGame);
        await Task.Delay(50);
        Assert.Equal(RuleCategory.Other, _svc.Preset.BlockedPaths.Single(p => p.Path == @"F:\My Games").Category);
        Assert.DoesNotContain(Apps.Rows, r => r.Key == @"F:\My Games");

        Apps.Filter = Apps.Filters.Single(f => f.Value == AppFilter.All);
        Apps.Search = "my games";
        Assert.Equal(@"F:\My Games", Apps.Rows.Single().Key);
    }

    [Fact]
    public async Task Names_typed_by_hand_are_validated_and_several_can_be_added()
    {
        await StartWithKid();
        TranslationSource.Instance.Culture = Loc.Pick("uk");
        _dialogs.PromptAnswer = "readme.txt";
        await Apps.AddNamesCommand.ExecuteAsync(null);
        Assert.Equal(MessageKind.Error, Session.MessageKind);
        Assert.Contains("не назва програми", Session.Message, StringComparison.Ordinal);
        Assert.False(Session.IsBusy);

        _dialogs.PromptAnswer = "badtool.exe, other.exe";
        await Apps.AddNamesCommand.ExecuteAsync(null);
        Assert.Contains(_svc.Preset.BlockedNames, n => n.Name == "badtool.exe");
        Assert.Contains(_svc.Preset.BlockedNames, n => n.Name == "other.exe");
    }

    [Fact]
    public async Task File_access_adds_a_rule_per_chosen_folder_for_the_ticked_accounts()
    {
        await _main.StartAsync();
        await Session.EditAsync(c =>
        {
            ConfigEditor.AddUser(c, "kid");
            ConfigEditor.AddUser(c, "teen");
        });
        var page = Page<FileAccessPageViewModel>();
        Assert.Equal(2, page.Accounts.Count);
        Assert.DoesNotContain(FileAccessPageViewModel.Choices, p => p.Key == "NoRun");   // that is done by the profile

        _dialogs.Folders = [@"D:\Private", @"D:\Photos"];
        page.Access = FileAccessPageViewModel.Choices.Single(p => p.Key == "ReadOnly");
        page.Accounts.Single(a => a.Name == "teen").IsSelected = false;
        await page.AddFoldersCommand.ExecuteAsync(null);
        Assert.Equal(2, _svc.Config.FileAccess.Count);
        var r = _svc.Config.FileAccess.Single(x => x.Path == @"D:\Private");
        Assert.Equal(FileAccessMode.Deny, r.Mode);
        Assert.Equal(FileAccessRights.Write | FileAccessRights.Delete, r.Rights);
        Assert.Equal(["kid"], r.Users);
        Assert.Equal("Read only", page.Rules[0].Rights);

        // Every account ticked = every restricted account, including ones added later.
        _dialogs.Folders = [@"D:\Secret"];
        _dialogs.Files = [];
        page.Access = FileAccessPageViewModel.Choices[0];
        page.Accounts.Single(a => a.Name == "teen").IsSelected = true;
        await page.AddFoldersCommand.ExecuteAsync(null);
        Assert.Empty(_svc.Config.FileAccess.Single(x => x.Path == @"D:\Secret").Users);

        // Nobody ticked: nothing is added.
        foreach (var a in page.Accounts) a.IsSelected = false;
        _dialogs.Folders = [@"D:\Other"];
        await page.AddFoldersCommand.ExecuteAsync(null);
        Assert.DoesNotContain(_svc.Config.FileAccess, x => x.Path == @"D:\Other");

        await page.RemoveCommand.ExecuteAsync(page.Rules.First());
        Assert.Equal(2, _svc.Config.FileAccess.Count);
    }

    [Fact]
    public async Task Always_allowed_folders_are_on_the_apps_page()
    {
        await StartWithKid();
        var page = Page<AppsPageViewModel>();
        _dialogs.Folders = [@"C:\Tools", @"C:\School"];
        await page.AllowFoldersCommand.ExecuteAsync(null);
        Assert.Equal([@"C:\Tools", @"C:\School"], _svc.Preset.AllowedPaths.Select(p => p.Path));
        await page.RemoveAllowedFolderCommand.ExecuteAsync(page.AllowedFolders.First());
        Assert.Single(_svc.Preset.AllowedPaths);
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
    public async Task Game_time_locks_and_unlocks_each_account_and_found_games_are_added_as_games()
    {
        await StartWithKid();
        var kid = GameTime.Now.Single();
        Assert.Equal("kid", kid.User);
        Assert.False(kid.Allowed);
        Assert.False(kid.Locked);
        Assert.Equal("blocked: the schedule is empty", kid.State);

        await GameTime.LockCommand.ExecuteAsync(kid);
        Assert.True(GameTime.Now.Single().Locked);
        Assert.Equal("blocked until the end of the day", GameTime.Now.Single().State);
        await GameTime.UnlockCommand.ExecuteAsync(GameTime.Now.Single());
        Assert.False(GameTime.Now.Single().Locked);
        Assert.Null(_svc.GameStates["kid"].LockedUntil);

        Assert.False(Apps.AddFoundCommand.CanExecute(null));
        await Apps.FindCommand.ExecuteAsync(null);
        Assert.True(Apps.HasFound);
        Assert.True(Apps.AddFoundCommand.CanExecute(null));
        Apps.Found.Single().IsSelected = false;
        Assert.False(Apps.AddFoundCommand.CanExecute(null));
        Apps.Found.Single().IsSelected = true;
        await Apps.AddFoundCommand.ExecuteAsync(null);
        Assert.Equal(RuleCategory.Games, _svc.Preset.BlockedPaths.Single(p => p.Path == @"D:\NewGames").Category);
        Assert.Empty(Apps.Found);
        Assert.False(Apps.HasFound);
    }

    [Fact]
    public async Task Games_schedule_is_built_from_days_hours_and_a_limit()
    {
        await StartWithKid();
        var g = GameTime;
        Assert.Equal(5, g.Days.Count(d => d.IsSelected));   // Monday to Friday at first

        g.StartAddingCommand.Execute(null);
        Assert.True(g.IsAdding);
        g.From = g.Times.Single(t => t.Text == "16:00");
        g.To = g.Times.Single(t => t.Text == "20:00");
        g.Limit = g.Limits.Single(l => l.Value == 60);
        await g.AddWindowCommand.ExecuteAsync(null);
        Assert.False(g.IsAdding);   // closed after adding
        var w = g.Schedule.Single();
        Assert.Equal(("Mon–Fri", "16:00–20:00", "1 h"), (w.Days, w.Hours, w.Limit));

        foreach (var d in g.Days) d.IsSelected = d.Day is DayOfWeek.Saturday or DayOfWeek.Sunday;
        g.Limit = g.Limits[0];
        await g.AddWindowCommand.ExecuteAsync(null);
        Assert.Equal(("Sat, Sun", (string?)null), (g.Schedule[1].Days, g.Schedule[1].Limit));

        g.StartAddingCommand.Execute(null);
        g.From = g.Times.Single(t => t.Text == "21:00");
        await g.AddWindowCommand.ExecuteAsync(null);   // ends before it starts
        Assert.Equal(MessageKind.Error, Session.MessageKind);
        Assert.True(g.IsAdding);   // stays open to correct it
        Assert.Equal(2, _svc.Preset.Games.Schedule.Count);
        g.CancelAddingCommand.Execute(null);
        Assert.False(g.IsAdding);

        await g.RemoveWindowCommand.ExecuteAsync(g.Schedule[0]);
        Assert.Single(_svc.Preset.Games.Schedule);

        g.CloseGamesWhenOver = false;   // saved at once
        await Task.Delay(50);
        Assert.False(_svc.Preset.Games.CloseGamesWhenOver);
    }

    [Fact]
    public async Task Each_account_can_have_its_own_profile_or_share_one()
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
        Assert.Equal("Not assigned to any account yet. Assign it on the Accounts page.", _main.PresetAccounts);

        var row = accounts.Accounts.Single(a => a.Name == "teen");
        row.Preset = row.Presets.Single(p => p.Name == "Teens");
        await Task.Delay(50);
        Assert.Equal(["teen"], _svc.Config.GetPreset("Teens").Accounts);
        Assert.Equal("Applies to: teen", _main.PresetAccounts);

        // Pages show and change only the chosen preset.
        _dialogs.PromptAnswer = null;
        await Apps.AddNamesCommand.ExecuteAsync(null);   // cancelled: nothing happens
        _dialogs.PromptAnswer = "tiktok.exe";
        await Apps.AddNamesCommand.ExecuteAsync(null);
        Assert.Contains(_svc.Config.GetPreset("Teens").BlockedNames, n => n.Name == "tiktok.exe");
        Assert.DoesNotContain(_svc.Config.GetPreset("kid").BlockedNames, n => n.Name == "tiktok.exe");
        Session.PresetName = "kid";
        Assert.DoesNotContain(Apps.Rows, r => r.Text == "tiktok.exe");

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
    public async Task Pages_of_a_profile_say_what_to_do_when_there_is_none()
    {
        await _main.StartAsync();
        Assert.Null(Session.PresetName);
        Assert.True(Page<AppsPageViewModel>().UsesPreset);
        Assert.False(Page<AccountsPageViewModel>().UsesPreset);
        _dialogs.PromptAnswer = "x.exe";
        await Apps.AddNamesCommand.ExecuteAsync(null);
        Assert.Equal(MessageKind.Info, Session.MessageKind);
        Assert.Contains("restrict an account", Session.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_messages_hide_by_themselves_and_errors_stay()
    {
        Session.MessageLifetime = TimeSpan.FromMilliseconds(300);
        Session.ShowMessage(MessageKind.Success, "done");
        Session.ShowMessage(MessageKind.Info, "newer");   // the newer one gets its own full time
        await Task.Delay(100);
        Assert.Equal("newer", Session.Message);
        for (var i = 0; i < 100 && Session.Message is not null; i++) await Task.Delay(20);
        Assert.Null(Session.Message);

        Session.ShowMessage(MessageKind.Error, "broken");
        await Task.Delay(600);
        Assert.Equal("broken", Session.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Theme_is_chosen_on_the_settings_page()
    {
        await _main.StartAsync();
        var page = Page<SettingsPageViewModel>();
        Assert.Equal(PanelTheme.System, page.Theme!.Theme);
        Assert.Equal(["Use Windows setting", "Light", "Dark"], page.Themes.Select(t => t.Text));
        page.Theme = page.Themes.Single(t => t.Theme == PanelTheme.Dark);
        Assert.Equal(PanelTheme.Dark, _appliedTheme);
        Assert.Contains("Rate Software", page.Copyright, StringComparison.Ordinal);
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
        Assert.Equal("Restrictions are suspended", page.Headline);
        await page.ApplyCommand.ExecuteAsync(null);
        Assert.True(_svc.Enabled);
    }

    [Fact]
    public async Task A_new_version_is_shown_and_installed_after_confirmation_and_then_the_panel_closes()
    {
        var exits = 0;
        Session.ExitRequested += (_, _) => exits++;
        await _main.StartAsync();
        var update = _main.Update;
        Assert.Equal("You have the latest version", update.StatusText);
        Assert.False(update.ShowBanner);

        _svc.Update = _svc.Update with { Latest = "0.2.0", Available = true, PageUrl = "https://github.com/rnd95/UseControl/releases/tag/v0.2.0" };
        await update.CheckCommand.ExecuteAsync(null);
        Assert.Equal("Version 0.2.0 is available", update.StatusText);
        Assert.True(update.ShowBanner);
        Assert.True(update.CanUpdateNow);
        Assert.Same(update, Page<StatusPageViewModel>().Update);   // the overview banner and the settings card are one

        update.OpenPageCommand.Execute(null);
        Assert.Equal(["https://github.com/rnd95/UseControl/releases/tag/v0.2.0"], _dialogs.OpenedUrls);

        _dialogs.Answer = false;
        await update.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, _svc.InstallUpdateCalls);
        _dialogs.Answer = true;
        await update.InstallCommand.ExecuteAsync(null);
        Assert.Equal(1, _svc.InstallUpdateCalls);
        Assert.True(update.Installing);
        Assert.False(update.CanUpdateNow);
        Assert.Equal(1, exits);

        // The switches are saved in the configuration.
        update.InstallAutomatically = true;
        await Task.Delay(50);
        Assert.True(_svc.Config.Updates.InstallAutomatically);
    }

    [Fact]
    public async Task Windows_restrictions_are_saved_as_soon_as_a_switch_is_clicked()
    {
        await StartWithKid();
        var page = Page<RestrictionsPageViewModel>();
        Assert.True(page.DisableCommandPrompt);
        Assert.False(page.DisableTaskManager);
        var before = _svc.SetConfigCalls;

        page.DisableTaskManager = true;
        await Task.Delay(50);
        Assert.True(_svc.Preset.Restrictions.DisableTaskManager);
        Assert.Equal(before + 1, _svc.SetConfigCalls);

        page.DisablePasswordChange = true;
        page.OtherDriveLetters = false;
        await Task.Delay(50);
        Assert.True(_svc.Preset.Restrictions.DisablePasswordChange);
        Assert.True(_svc.Preset.Restrictions.DisableCommandPrompt);
        Assert.False(_svc.Preset.Auto.OtherDriveLetters);

        // Showing another profile does not save anything.
        before = _svc.SetConfigCalls;
        Session.PresetName = "kid";
        await Task.Delay(50);
        Assert.Equal(before, _svc.SetConfigCalls);

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
    }

    [Fact]
    public async Task The_language_switches_live()
    {
        await _main.StartAsync();
        var page = Page<SettingsPageViewModel>();
        Assert.Equal("0.1.0", page.ServiceVersion);
        Assert.Equal(["en", "uk", "de", "pl"], page.Languages.Select(l => l.Culture.Name));

        var status = Page<StatusPageViewModel>();
        Assert.Equal("Overview", status.Title);
        page.Language = page.Languages.Single(l => l.Culture.Name == "uk");
        Assert.Equal("Огляд", status.Title);
        Assert.Equal("Захист увімкнено", status.Headline);
        Assert.Equal("uk", _savedLanguage?.Name);
        Assert.Equal("Огляд", TranslationSource.Instance["Ui.Nav.Status"]);
        Assert.Equal("ПРОФІЛЬ", NavSection.Profile.Title);

        page.Language = page.Languages.Single(l => l.Culture.Name == "de");
        Assert.Equal("Apps und Spiele", Page<AppsPageViewModel>().Title);
        page.Language = page.Languages.Single(l => l.Culture.Name == "pl");
        Assert.Equal("Ochrona jest włączona", status.Headline);
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
        Assert.Contains(main.Pages.OfType<AppsPageViewModel>().Single().Apps.Rows, r => r.Kind == BlockKind.Program);
        Assert.NotEmpty(main.Pages.OfType<StatusPageViewModel>().Single().Accounts);

        var audit = main.Pages.OfType<AuditPageViewModel>().Single();
        await audit.RunCommand.ExecuteAsync(null);
        Assert.Equal(AuditStatus.Fail, audit.Checks[0].Status);
        Assert.Contains(@"BUILTIN\Users", audit.Checks[0].Detail, StringComparison.Ordinal);
    }
}
