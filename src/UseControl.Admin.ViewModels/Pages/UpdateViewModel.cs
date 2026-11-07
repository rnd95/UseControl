using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Ipc;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

/// <summary>
/// Updates of UseControl, shown on the settings page (with its switches) and as a banner on the overview
/// when a new version is out. Installing closes the panel: the installer replaces it and restarts the service.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly PanelSession _session;
    private bool _loading;

    public UpdateViewModel(PanelSession session)
    {
        _session = session;
        session.Reloaded += (_, _) => Rebuild();
        TranslationSource.Instance.CultureChanged += (_, _) => Rebuild();
    }

    [ObservableProperty] private string _current = "—";
    [ObservableProperty] private string? _latest;
    [ObservableProperty] private bool _available;
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty] private bool _installing;

    /// <summary>"Version 0.2.0 is available" / "You have the latest version" / an error.</summary>
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private bool _isError;

    /// <summary>"Checked 07.11.2026 12:00".</summary>
    [ObservableProperty] private string _lastCheckText = "";

    [ObservableProperty] private string? _pageUrl;

    /// <summary>The overview banner: a newer version that this copy can install.</summary>
    public bool ShowBanner => Available && !Installing;

    /// <summary>The "Update" button: a newer version that this copy can install, not being installed yet.</summary>
    public bool CanUpdateNow => Available && CanInstall && !Installing;

    public string BannerText => Latest is null ? "" : Loc.T("Ui.Update.Banner", Latest);

    [ObservableProperty] private bool _checkDaily = true;
    [ObservableProperty] private bool _installAutomatically;

    partial void OnCheckDailyChanged(bool value)
    {
        if (!_loading) _ = _session.EditAsync(c => c.Updates.CheckDaily = value);
    }

    partial void OnInstallAutomaticallyChanged(bool value)
    {
        if (!_loading) _ = _session.EditAsync(c => c.Updates.InstallAutomatically = value);
    }

    [RelayCommand]
    private Task Check() => _session.RunAsync(async () =>
    {
        await _session.Client.CheckUpdateAsync().ConfigureAwait(true);
        await _session.RefreshAsync().ConfigureAwait(true);
        if (IsError) _session.ShowMessage(MessageKind.Error, StatusText);
        else _session.ShowMessage(MessageKind.Info, StatusText);
    });

    [RelayCommand]
    private async Task Install()
    {
        if (Latest is null || !_session.Dialogs.Confirm(Loc.T("Ui.Update.Confirm", Latest))) return;
        var started = await _session.RunAsync(async () =>
        {
            Apply(await _session.Client.InstallUpdateAsync().ConfigureAwait(true));
        }).ConfigureAwait(true);
        if (!started) return;
        _session.ShowMessage(MessageKind.Success, Loc.T("Ui.Update.Started"));
        _session.RequestExit();
    }

    [RelayCommand]
    private void OpenPage()
    {
        if (PageUrl is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) _session.Dialogs.OpenUrl(url);
    }

    private void Rebuild()
    {
        _loading = true;
        try
        {
            CheckDaily = _session.Config?.Updates.CheckDaily ?? true;
            InstallAutomatically = _session.Config?.Updates.InstallAutomatically ?? false;
        }
        finally
        {
            _loading = false;
        }
        if (_session.Status?.Update is { } u) Apply(u);
    }

    private void Apply(UpdateStatus u)
    {
        Current = u.Current;
        Latest = u.Latest;
        Available = u.Available;
        CanInstall = u.CanInstall;
        Installing = u.Installing;
        PageUrl = u.PageUrl;
        IsError = u.ErrorCode is not null;
        StatusText = u.ErrorCode is { } code ? Loc.Error(code, u.ErrorArgs, code)
            : u.Installing ? Loc.T("Ui.Update.Installing")
            : u.Available ? Loc.T("Ui.Update.Available", u.Latest)
            : u.LastCheck is null ? Loc.T("Ui.Update.NotChecked")
            : Loc.T("Ui.Update.UpToDate");
        LastCheckText = u.LastCheck is { } t ? Loc.T("Ui.Update.LastCheck", StatusPageViewModel.Local(t)) : "";
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(CanUpdateNow));
        OnPropertyChanged(nameof(BannerText));
    }
}
