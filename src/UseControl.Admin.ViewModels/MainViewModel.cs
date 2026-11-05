using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Admin.ViewModels.Pages;
using UseControl.Core.Configuration;

namespace UseControl.Admin.ViewModels;

/// <summary>The window: the list of pages, the selected page and the shared session.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(
        PanelSession session,
        Func<IReadOnlyList<string>> standardAccounts,
        Func<string, RuleCategory, FileHashEntry> fingerprint,
        Action<CultureInfo> saveLanguage)
    {
        Session = session;
        Pages =
        [
            new StatusPageViewModel(session),
            new AccountsPageViewModel(session, standardAccounts),
            new RestrictionsPageViewModel(session),
            new GamesPageViewModel(session),
            new ProgramsPageViewModel(session, fingerprint),
            new FileAccessPageViewModel(session),
            new BrowsersPageViewModel(session),
            new AuditPageViewModel(session),
            new SettingsPageViewModel(session, saveLanguage),
        ];
        _selectedPage = Pages[0];
    }

    public PanelSession Session { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty]
    private PageViewModel _selectedPage;

    partial void OnSelectedPageChanged(PageViewModel value) => _ = value.ActivateAsync();

    /// <summary>Called once the window is shown: connects to the service and loads everything.</summary>
    public async Task StartAsync()
    {
        await Session.RefreshAsync().ConfigureAwait(true);
        if (SelectedPage is not StatusPageViewModel) await SelectedPage.ActivateAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissMessage() => Session.ClearMessage();

    [RelayCommand]
    private async Task Retry()
    {
        if (await Session.RefreshAsync().ConfigureAwait(true)) Session.ClearMessage();
    }
}
