using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Admin.ViewModels.Pages;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels;

/// <summary>The window: the list of pages, the selected page and the shared session.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(
        PanelSession session,
        Func<IReadOnlyList<string>> standardAccounts,
        Func<string, RuleCategory, FileHashEntry> fingerprint,
        Action<CultureInfo> saveLanguage,
        ThemeChoices? themes = null)
    {
        Session = session;
        session.Reloaded += (_, _) => OnSubtitleChanged();
        TranslationSource.Instance.CultureChanged += (_, _) => OnSubtitleChanged();
        Pages =
        [
            new StatusPageViewModel(session, Update = new UpdateViewModel(session)),
            new AccountsPageViewModel(session, standardAccounts),
            new AppsPageViewModel(session, fingerprint),
            new RestrictionsPageViewModel(session),
            new BrowsersPageViewModel(session),
            new FileAccessPageViewModel(session),
            new AuditPageViewModel(session),
            new SettingsPageViewModel(session, saveLanguage, themes ?? ThemeChoices.None, Update),
        ];
        _selectedPage = Pages[0];
    }

    public PanelSession Session { get; }

    /// <summary>Updates of UseControl (settings page and overview banner).</summary>
    public UpdateViewModel Update { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty]
    private PageViewModel _selectedPage;

    partial void OnSelectedPageChanged(PageViewModel value)
    {
        OnPropertyChanged(nameof(PageSubtitle));
        _ = value.ActivateAsync();
    }

    private void OnSubtitleChanged()
    {
        OnPropertyChanged(nameof(PresetAccounts));
        OnPropertyChanged(nameof(PageSubtitle));
    }

    /// <summary>The line under the page title: whom the profile applies to, or what the page is for.</summary>
    public string PageSubtitle => SelectedPage.UsesPreset ? PresetAccounts : SelectedPage.Subtitle;

    /// <summary>"For: alpine, kid" under the page title of a profile page.</summary>
    public string PresetAccounts => Session.Preset is not { } p
        ? Loc.T("Ui.Preset.NoneYet")
        : p.Accounts.Count == 0 ? Loc.T("Ui.Preset.NoAccounts") : Loc.T("Ui.Preset.For", string.Join(", ", p.Accounts));

    /// <summary>A new preset: a copy of the one shown, or the recommended settings when there is none.</summary>
    [RelayCommand]
    private Task NewPreset()
    {
        var name = Session.Dialogs.Prompt(Loc.T("Ui.Preset.NewPrompt"), "");
        if (string.IsNullOrWhiteSpace(name)) return Task.CompletedTask;
        var from = Session.PresetName;
        return EditAndShow(c => ConfigEditor.AddPreset(c, name, from is null ? null : c.GetPreset(from)).Name);
    }

    [RelayCommand]
    private Task RenamePreset()
    {
        if (Session.PresetName is not { } old) return Task.CompletedTask;
        var name = Session.Dialogs.Prompt(Loc.T("Ui.Preset.RenamePrompt", old), old);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == old) return Task.CompletedTask;
        return EditAndShow(c =>
        {
            ConfigEditor.RenamePreset(c, old, name);
            return name.Trim();
        });
    }

    [RelayCommand]
    private Task DeletePreset()
    {
        if (Session.Preset is not { } p) return Task.CompletedTask;
        var question = p.Accounts.Count == 0
            ? Loc.T("Ui.Preset.ConfirmDelete", p.Name)
            : Loc.T("Ui.Preset.ConfirmDeleteAccounts", p.Name, string.Join(", ", p.Accounts));
        if (!Session.Dialogs.Confirm(question)) return Task.CompletedTask;
        var name = p.Name;
        return Session.EditAsync(c => ConfigEditor.RemovePreset(c, name));
    }

    // Changes the configuration, then shows the preset whose name the change returns.
    private async Task EditAndShow(Func<UcConfig, string> change)
    {
        string? show = null;
        if (await Session.EditAsync(c => show = change(c)).ConfigureAwait(true) && show is not null) Session.PresetName = show;
    }

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
