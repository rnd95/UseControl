using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

/// <summary>One restricted account on the overview: its profile, whether its restrictions are in place, its games now.</summary>
public sealed record AccountOverview(
    string User,
    string Profile,
    string Restrictions,
    bool RestrictionsWarning,
    string Games,
    bool GamesAllowed,
    string Played,
    bool Playing);

/// <summary>The first page: is protection on, and how is every restricted account doing.</summary>
public sealed partial class StatusPageViewModel : PageViewModel
{
    public StatusPageViewModel(PanelSession session) : base(session, "Ui.Nav.Status", "") { }

    /// <summary>"Protection is on" or "Restrictions are suspended".</summary>
    [ObservableProperty]
    private string _headline = "";

    /// <summary>Service version and when the restrictions were last applied.</summary>
    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    private bool _isEnabled = true;

    public ObservableCollection<AccountOverview> Accounts { get; } = [];

    /// <summary>Accounts the last apply failed for, with the reason.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    public override Task ActivateAsync() => Session.RefreshAsync();

    [RelayCommand]
    private Task Refresh() => Session.RefreshAsync();

    [RelayCommand]
    private Task Apply() => Session.RunAsync(async () =>
    {
        var r = await Session.Client.ApplyAsync().ConfigureAwait(true);
        await Session.RefreshAsync().ConfigureAwait(true);
        Session.ShowApplyResult(r);
    });

    [RelayCommand]
    private async Task Suspend()
    {
        if (!Session.Dialogs.Confirm(Loc.T("Ui.Status.ConfirmSuspend"))) return;
        await Session.RunAsync(async () =>
        {
            await Session.Client.SuspendAsync().ConfigureAwait(true);
            await Session.RefreshAsync().ConfigureAwait(true);
            Session.ShowMessage(MessageKind.Info, Loc.T("Cli.Suspended"));
        }).ConfigureAwait(true);
    }

    protected override void Rebuild()
    {
        var s = Session.Status;
        if (s is null)
        {
            Headline = "";
            Details = "";
            Accounts.Clear();
            Problems.Clear();
            return;
        }
        IsEnabled = s.Enabled;
        Headline = Loc.T(s.Enabled ? "Ui.Status.On" : "Ui.Status.Suspended");
        Details = Loc.T("Ui.Status.Details", s.Version, s.LastApplied is { } la ? Local(la) : Loc.T("Cli.StatusNever"));

        Replace(Accounts, s.Accounts.Select(a =>
        {
            var (text, warning) = a.State switch
            {
                null => (Loc.T("Ui.Status.Unreadable"), true),
                { Applied: false } => (Loc.T("Ui.Status.NotApplied"), true),
                { KeysLocked: false } => (Loc.T("Ui.Status.Unlocked"), true),
                _ => (Loc.T("Ui.Status.Applied"), false),
            };
            var g = s.Games.FirstOrDefault(x => x.User.Equals(a.User, StringComparison.OrdinalIgnoreCase));
            return new AccountOverview(
                a.User,
                g?.Preset ?? Session.Config?.PresetOf(a.User)?.Name ?? "",
                text,
                warning,
                g is null ? "" : GameTimeViewModel.StateText(g),
                g?.Allowed ?? false,
                g is null ? "" : Loc.T("Ui.Games.PlayedToday", GamesText.Duration(g.PlayedToday)),
                g?.Playing ?? false);
        }));
        Replace(Problems, s.LastResults.Where(r => !r.Success).Select(r => r.User + ": " + (r.Error ?? "?")));
    }

    internal static string Local(DateTimeOffset t) => t.ToLocalTime().ToString("g", Loc.Culture ?? CultureInfo.CurrentCulture);
}
