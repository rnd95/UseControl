using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

public sealed record StatusLine(string Label, string Value, bool Warning = false);

public sealed partial class StatusPageViewModel : PageViewModel
{
    public StatusPageViewModel(PanelSession session) : base(session, "Ui.Nav.Status", "") { }

    public ObservableCollection<StatusLine> Summary { get; } = [];

    public ObservableCollection<StatusLine> Accounts { get; } = [];

    [ObservableProperty]
    private bool _isEnabled = true;

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
            Summary.Clear();
            Accounts.Clear();
            return;
        }
        IsEnabled = s.Enabled;
        Replace(Summary,
        [
            new(Loc.T("Ui.Status.Service"), s.Version),
            new(Loc.T("Ui.Status.Restrictions"), Loc.T(s.Enabled ? "Ui.Status.On" : "Ui.Status.Suspended"), !s.Enabled),
            new(Loc.T("Ui.Status.Games"), s.GamesAllowedUntil is { } u ? Loc.T("Ui.Status.GamesUntil", Local(u)) : Loc.T("Ui.Status.GamesBlocked"), s.GamesAllowedUntil is not null),
            new(Loc.T("Ui.Status.LastApplied"), s.LastApplied is { } la ? Local(la) : Loc.T("Cli.StatusNever")),
            new(Loc.T("Ui.Status.Store"), Loc.T(s.StoreInstallsAdminOnly ? "Ui.Status.StoreAdmins" : "Ui.Status.StoreEveryone")),
        ]);
        Replace(Accounts, s.Accounts.Select(a =>
        {
            if (a.State is not { } st) return new StatusLine(a.User, Loc.T("Ui.Status.Unreadable"), true);
            if (!st.Applied) return new StatusLine(a.User, Loc.T("Ui.Status.NotApplied"), true);
            return new StatusLine(a.User, Loc.T("Ui.Status.AccountRules", st.BlockedPathRules, st.AllowedPathRules, st.HashRules / 2, st.BlockedNames)
                + (st.KeysLocked ? "" : " · " + Loc.T("Ui.Status.Unlocked")), !st.KeysLocked);
        }));
        foreach (var f in s.LastResults.Where(r => !r.Success)) Accounts.Add(new StatusLine(f.User, f.Error ?? "?", true));
    }

    internal static string Local(DateTimeOffset t) => t.ToLocalTime().ToString("g", Loc.Culture ?? CultureInfo.CurrentCulture);
}
