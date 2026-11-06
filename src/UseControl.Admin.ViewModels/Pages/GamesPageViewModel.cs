using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Core.Games;
using UseControl.Ipc;
using UseControl.Localization;
using UseControl.Windows.Scanning;

namespace UseControl.Admin.ViewModels.Pages;

public sealed partial class SelectableItem(string text) : ObservableObject
{
    public string Text { get; } = text;

    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>A day button of the schedule editor.</summary>
public sealed partial class DayChoice(DayOfWeek day, string text, bool selected) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Text { get; } = text;

    [ObservableProperty]
    private bool _isSelected = selected;
}

/// <summary>A choice with a text, for the hour, time and limit lists.</summary>
public sealed record Choice<T>(T Value, string Text);

/// <summary>One account of the preset: may it play now, how long it played today.</summary>
public sealed record GamesNowRow(string User, string State, string Played, bool Allowed, bool Playing);

/// <summary>A time window of the schedule.</summary>
public sealed record ScheduleRow(int Index, string Text);

/// <summary>
/// Everything about games of one preset: who may play now, when games are allowed (by hand or by a
/// schedule with daily limits), the blocked games, finding installed ones.
/// </summary>
public sealed partial class GamesPageViewModel : PageViewModel
{
    private bool _loading;

    public GamesPageViewModel(PanelSession session, Func<string, RuleCategory, FileHashEntry> fingerprint)
        : base(session, "Ui.Nav.Games", "")
    {
        List = new BlockListViewModel(session, c => c == RuleCategory.Games, RuleCategory.Games,
            fingerprint, "Ui.Games.PickGame", "Ui.Games.PickFolder");
        BuildChoices();
    }

    public override bool UsesPreset => true;

    public BlockListViewModel List { get; }

    // ------------------------------------------------------------ now

    public ObservableCollection<GamesNowRow> Now { get; } = [];

    public static IReadOnlyList<double> HourChoices { get; } = [0.5, 1, 1.5, 2, 3, 4, 6, 8, 12, 24];

    private static readonly int[] LimitMinutes = [0, 30, 45, 60, 90, 120, 180, 240];

    [ObservableProperty]
    private double _hours = 1;

    /// <summary>True: the time counts only while a game runs (the timer starts when a game starts).</summary>
    [ObservableProperty]
    private bool _countPlayOnly = true;

    [RelayCommand]
    private Task Allow(GamesNowRow? row) => row is null ? Task.CompletedTask : Session.RunAsync(async () =>
    {
        var s = await Session.Client.AllowGamesAsync(row.User, Hours, CountPlayOnly).ConfigureAwait(true);
        await Session.RefreshAsync().ConfigureAwait(true);
        var g = s.Games.FirstOrDefault(x => x.User.Equals(row.User, StringComparison.OrdinalIgnoreCase));
        Session.ShowMessage(MessageKind.Success, g is null ? Loc.T("Ui.Saved") : $"{g.User}: {StateText(g)}");
    });

    [RelayCommand]
    private Task Lock(GamesNowRow? row) => row is null ? Task.CompletedTask : Session.RunAsync(async () =>
    {
        await Session.Client.LockGamesAsync(row.User).ConfigureAwait(true);
        await Session.RefreshAsync().ConfigureAwait(true);
        Session.ShowMessage(MessageKind.Success, Loc.T("Ui.Games.Locked", row.User));
    });

    // ------------------------------------------------------------ when

    /// <summary>False: only when an administrator allows; true: by the schedule.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManual))]
    private bool _isSchedule;

    public bool IsManual
    {
        get => !IsSchedule;
        set => IsSchedule = !value;
    }

    [ObservableProperty]
    private bool _closeGamesWhenOver;

    public ObservableCollection<ScheduleRow> Schedule { get; } = [];

    /// <summary>Monday to Sunday, named in the current language; Monday to Friday chosen at first.</summary>
    public IReadOnlyList<DayChoice> Days { get; private set; } = [];

    public IReadOnlyList<Choice<int>> Times { get; private set; } = [];

    public IReadOnlyList<Choice<int>> Limits { get; private set; } = [];

    [ObservableProperty]
    private Choice<int>? _from;

    [ObservableProperty]
    private Choice<int>? _to;

    [ObservableProperty]
    private Choice<int>? _limit;

    partial void OnIsScheduleChanged(bool value)
    {
        if (!_loading) _ = Session.EditPresetAsync(p => ConfigEditor.SetGamesMode(p, value ? GamesMode.Schedule : GamesMode.Manual));
    }

    partial void OnCloseGamesWhenOverChanged(bool value)
    {
        if (!_loading) _ = Session.EditPresetAsync(p => p.Games.CloseGamesWhenOver = value);
    }

    [RelayCommand]
    private Task AddWindow()
    {
        var days = Days.Where(d => d.IsSelected).Select(d => d.Day).ToList();
        var from = GamesWindow.FormatTime(From?.Value ?? 16 * 60);
        var to = GamesWindow.FormatTime(To?.Value ?? 20 * 60);
        var limit = Limit?.Value ?? 0;
        return Session.EditPresetAsync(p => ConfigEditor.AddGamesWindow(p, days, from, to, limit));
    }

    [RelayCommand]
    private Task RemoveWindow(ScheduleRow? row) =>
        row is null ? Task.CompletedTask : Session.EditPresetAsync(p => ConfigEditor.RemoveGamesWindow(p, row.Index));

    // ------------------------------------------------------------ find

    public ObservableCollection<SelectableItem> Found { get; } = [];

    [ObservableProperty]
    private bool _searched;

    [RelayCommand]
    private Task Find() => Session.RunAsync(async () =>
    {
        var all = await Session.Client.FindGamesAsync().ConfigureAwait(true);
        var found = Session.Preset is { } p ? GameFinder.NotListed(all, p) : all;
        Replace(Found, found.Select(f => new SelectableItem(f)));
        Searched = true;
        Session.ShowMessage(MessageKind.Info, found.Count == 0 ? Loc.T("Cli.GamesFoundNone") : Loc.T("Ui.Games.FoundCount", found.Count));
    });

    [RelayCommand]
    private async Task BlockSelected()
    {
        var chosen = Found.Where(f => f.IsSelected).Select(f => f.Text).ToList();
        if (chosen.Count == 0) return;
        if (await Session.EditPresetAsync(c => { foreach (var p in chosen) ConfigEditor.AddBlockedPath(c, p, RuleCategory.Games, Session.Paths); }).ConfigureAwait(true))
        {
            foreach (var f in Found.Where(f => f.IsSelected).ToList()) Found.Remove(f);
        }
    }

    // ------------------------------------------------------------ rebuild

    internal static string StateText(AccountGames g) =>
        GamesText.State(g.Allowed, g.Reason, g.Until, g.MinutesLeft, g.NextStart, DateTimeOffset.Now);

    private void BuildChoices()
    {
        var from = From?.Value ?? 16 * 60;
        var to = To?.Value ?? 20 * 60;
        var limit = Limit?.Value ?? 0;
        Times = [.. Enumerable.Range(0, 49).Select(i => new Choice<int>(i * 30, GamesWindow.FormatTime(i * 30)))];
        Limits = [.. LimitMinutes.Select(m => new Choice<int>(m, m == 0 ? Loc.T("Ui.Games.NoLimit") : GamesText.Duration(m)))];
        OnPropertyChanged(nameof(Times));
        OnPropertyChanged(nameof(Limits));
        From = Times.First(t => t.Value == from);
        To = Times.First(t => t.Value == to);
        Limit = Limits.FirstOrDefault(l => l.Value == limit) ?? Limits[0];
        var names = Loc.Culture.DateTimeFormat;
        var chosen = Days.Count == 0 ? GamesText.Week.Take(5).ToHashSet() : Days.Where(d => d.IsSelected).Select(d => d.Day).ToHashSet();
        Days = [.. GamesText.Week.Select(d => new DayChoice(d, names.GetAbbreviatedDayName(d), chosen.Contains(d)))];
        OnPropertyChanged(nameof(Days));
    }

    protected override void Rebuild()
    {
        BuildChoices();
        var preset = Session.Preset;
        List.Rebuild(preset);
        _loading = true;
        try
        {
            IsSchedule = preset?.Games.Mode == GamesMode.Schedule;
            CloseGamesWhenOver = preset?.Games.CloseGamesWhenOver ?? true;
            Replace(Schedule, (preset?.Games.Schedule ?? []).Select((w, i) => new ScheduleRow(i, GamesText.Window(w))));
            var games = Session.Status?.Games ?? [];
            Replace(Now, games
                .Where(g => preset is not null && g.Preset.Equals(preset.Name, StringComparison.OrdinalIgnoreCase))
                .Select(g => new GamesNowRow(g.User, StateText(g), Loc.T("Ui.Games.PlayedToday", GamesText.Duration(g.PlayedToday)) + (g.Playing ? " · " + Loc.T("Ui.Games.PlayingNow") : ""), g.Allowed, g.Playing)));
        }
        finally
        {
            _loading = false;
        }
    }
}
