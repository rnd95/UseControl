using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

public sealed partial class SelectableItem(string text) : ObservableObject
{
    public string Text { get; } = text;

    [ObservableProperty]
    private bool _isSelected = true;
}

public sealed partial class GamesPageViewModel : PageViewModel
{
    public GamesPageViewModel(PanelSession session) : base(session, "Ui.Nav.Games", "") { }

    public static IReadOnlyList<double> HourChoices { get; } = [0.5, 1, 1.5, 2, 3, 4, 6, 8, 12, 24];

    [ObservableProperty]
    private double _hours = 1;

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private bool _gamesAllowed;

    public ObservableCollection<SelectableItem> Found { get; } = [];

    [ObservableProperty]
    private bool _searched;

    [RelayCommand]
    private Task Allow() => Session.RunAsync(async () =>
    {
        var s = await Session.Client.AllowGamesAsync(Hours).ConfigureAwait(true);
        await Session.RefreshAsync().ConfigureAwait(true);
        Session.ShowMessage(MessageKind.Success, Loc.T("Cli.GamesAllowed", s.GamesAllowedUntil is { } u ? StatusPageViewModel.Local(u) : "?"));
    });

    [RelayCommand]
    private Task Lock() => Session.RunAsync(async () =>
    {
        await Session.Client.LockGamesAsync().ConfigureAwait(true);
        await Session.RefreshAsync().ConfigureAwait(true);
        Session.ShowMessage(MessageKind.Success, Loc.T("Cli.GamesLocked"));
    });

    [RelayCommand]
    private Task Find() => Session.RunAsync(async () =>
    {
        var found = await Session.Client.FindGamesAsync().ConfigureAwait(true);
        Replace(Found, found.Select(f => new SelectableItem(f)));
        Searched = true;
        Session.ShowMessage(MessageKind.Info, found.Count == 0 ? Loc.T("Cli.GamesFoundNone") : Loc.T("Ui.Games.FoundCount", found.Count));
    });

    [RelayCommand]
    private async Task BlockSelected()
    {
        var chosen = Found.Where(f => f.IsSelected).Select(f => f.Text).ToList();
        if (chosen.Count == 0) return;
        if (await Session.EditAsync(c => { foreach (var p in chosen) ConfigEditor.AddBlockedPath(c, p, RuleCategory.Games, Session.Paths); }).ConfigureAwait(true))
        {
            foreach (var f in Found.Where(f => f.IsSelected).ToList()) Found.Remove(f);
        }
    }

    protected override void Rebuild()
    {
        var until = Session.Status?.GamesAllowedUntil;
        GamesAllowed = until is not null;
        State = until is { } u ? Loc.T("Ui.Status.GamesUntil", StatusPageViewModel.Local(u)) : Loc.T("Ui.Status.GamesBlocked");
    }
}
