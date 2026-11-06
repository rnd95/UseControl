using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Core.Games;
using UseControl.Ipc;
using UseControl.Localization;
using UseControl.Windows.Scanning;

namespace UseControl.Admin.ViewModels.Pages;

public enum BlockKind
{
    /// <summary>A program file: blocked by its fingerprint and by its name.</summary>
    Program,

    /// <summary>Only a program file name.</summary>
    Name,

    /// <summary>Everything in a folder.</summary>
    Folder,
}

/// <summary>A row of a simple folder list (always allowed folders).</summary>
public sealed record FolderRow(string Path, string? Note);

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

/// <summary>A choice with a text, for the time and limit lists.</summary>
public sealed record Choice<T>(T Value, string Text);

/// <summary>One account of the profile: may it play now, how long it played today.</summary>
public sealed record GamesNowRow(string User, string State, string Played, bool Allowed, bool Playing, bool Locked);

/// <summary>A time window of the schedule.</summary>
public sealed record ScheduleRow(int Index, string Days, string Hours, string? Limit);

/// <summary>What happens to a program: it is a game (runs only in games time) or it never runs.</summary>
public sealed record RuleChoice(bool IsGame, string Text);

public enum AppFilter
{
    All,
    Games,
    Blocked,
}

public sealed record FilterChoice(AppFilter Value, string Text, int Count);

/// <summary>One program, program name or folder of the profile, with its rule.</summary>
public sealed partial class AppRow : ObservableObject
{
    private readonly Action<AppRow, RuleChoice>? _ruleChanged;

    public AppRow(BlockKind kind, string key, string text, string detail, RuleCategory category, IReadOnlyList<RuleChoice> rules, Action<AppRow, RuleChoice>? ruleChanged)
    {
        Kind = kind;
        Key = key;
        Text = text;
        Detail = detail;
        Category = category;
        Rules = rules;
        _rule = rules.First(r => r.IsGame == IsGame);
        _ruleChanged = ruleChanged;
    }

    public BlockKind Kind { get; }

    /// <summary>What removing or changing it needs: SHA-256, name or path.</summary>
    public string Key { get; }

    public string Text { get; }

    public string Detail { get; }

    public RuleCategory Category { get; }

    public bool IsGame => Category == RuleCategory.Games;

    /// <summary>Glyph in the Segoe MDL2 Assets font.</summary>
    public string Icon => Kind switch
    {
        BlockKind.Folder => "",
        BlockKind.Name => "",
        _ => IsGame ? "" : "",
    };

    public IReadOnlyList<RuleChoice> Rules { get; }

    [ObservableProperty]
    private RuleChoice _rule;

    partial void OnRuleChanged(RuleChoice value)
    {
        if (value.IsGame != IsGame) _ruleChanged?.Invoke(this, value);
    }
}

/// <summary>
/// Games time of the profile: who may play now (with lock and unlock), the weekly schedule with daily
/// limits (the only time games run) and whether games are closed when the time is over.
/// </summary>
public sealed partial class GameTimeViewModel : ObservableObject
{
    private static readonly int[] LimitMinutes = [0, 30, 45, 60, 90, 120, 180, 240];
    private readonly PanelSession _session;
    private bool _loading;

    public GameTimeViewModel(PanelSession session)
    {
        _session = session;
        BuildChoices();
    }

    public ObservableCollection<GamesNowRow> Now { get; } = [];

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

    /// <summary>The editor of a new time window is open.</summary>
    [ObservableProperty]
    private bool _isAdding;

    [ObservableProperty]
    private bool _closeGamesWhenOver;

    partial void OnCloseGamesWhenOverChanged(bool value)
    {
        if (!_loading) _ = _session.EditPresetAsync(p => p.Games.CloseGamesWhenOver = value);
    }

    internal static string StateText(AccountGames g) =>
        GamesText.State(g.Allowed, g.Reason, g.Until, g.MinutesLeft, g.NextStart, DateTimeOffset.Now);

    /// <summary>Blocks games for the rest of the day (running games are closed after a warning).</summary>
    [RelayCommand]
    private Task Lock(GamesNowRow? row) => row is null ? Task.CompletedTask : _session.RunAsync(async () =>
    {
        await _session.Client.LockGamesAsync(row.User).ConfigureAwait(true);
        await _session.RefreshAsync().ConfigureAwait(true);
        _session.ShowMessage(MessageKind.Success, Loc.T("Ui.Games.Locked", row.User));
    });

    /// <summary>Takes such a block back: the schedule applies again.</summary>
    [RelayCommand]
    private Task Unlock(GamesNowRow? row) => row is null ? Task.CompletedTask : _session.RunAsync(async () =>
    {
        await _session.Client.UnlockGamesAsync(row.User).ConfigureAwait(true);
        await _session.RefreshAsync().ConfigureAwait(true);
        _session.ShowMessage(MessageKind.Success, Loc.T("Ui.Games.Unlocked", row.User));
    });

    [RelayCommand]
    private void StartAdding() => IsAdding = true;

    [RelayCommand]
    private void CancelAdding() => IsAdding = false;

    [RelayCommand]
    private async Task AddWindow()
    {
        var days = Days.Where(d => d.IsSelected).Select(d => d.Day).ToList();
        var from = GamesWindow.FormatTime(From?.Value ?? 16 * 60);
        var to = GamesWindow.FormatTime(To?.Value ?? 20 * 60);
        var limit = Limit?.Value ?? 0;
        if (await _session.EditPresetAsync(p => ConfigEditor.AddGamesWindow(p, days, from, to, limit)).ConfigureAwait(true)) IsAdding = false;
    }

    [RelayCommand]
    private Task RemoveWindow(ScheduleRow? row) =>
        row is null ? Task.CompletedTask : _session.EditPresetAsync(p => ConfigEditor.RemoveGamesWindow(p, row.Index));

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

    internal void Rebuild(Preset? preset, ServiceStatus? status)
    {
        BuildChoices();
        _loading = true;
        try
        {
            CloseGamesWhenOver = preset?.Games.CloseGamesWhenOver ?? true;
            Replace(Schedule, (preset?.Games.Schedule ?? []).Select((w, i) => new ScheduleRow(i,
                GamesText.Days(w.Days),
                w.From + "–" + w.To,
                w.LimitMinutes > 0 ? GamesText.Duration(w.LimitMinutes) : null)));
            Replace(Now, (status?.Games ?? [])
                .Where(g => preset is not null && g.Preset.Equals(preset.Name, StringComparison.OrdinalIgnoreCase))
                .Select(g => new GamesNowRow(g.User, StateText(g),
                    Loc.T("Ui.Games.PlayedToday", GamesText.Duration(g.PlayedToday)) + (g.Playing ? " · " + Loc.T("Ui.Games.PlayingNow") : ""),
                    g.Allowed, g.Playing, g.Reason == GamesReason.Locked)));
        }
        finally
        {
            _loading = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}

/// <summary>
/// Every program, program name and folder of the profile in one list. Each has a rule: a game (runs
/// only in games time) or blocked (never runs). Several programs or folders can be added at once.
/// </summary>
public sealed partial class AppListViewModel : ObservableObject
{
    private readonly PanelSession _session;
    private readonly Func<string, RuleCategory, FileHashEntry> _fingerprint;
    private List<AppRow> _all = [];
    private bool _loading;

    public AppListViewModel(PanelSession session, Func<string, RuleCategory, FileHashEntry> fingerprint)
    {
        _session = session;
        _fingerprint = fingerprint;
        BuildChoices();
    }

    /// <summary>The rows the filter and the search let through.</summary>
    public ObservableCollection<AppRow> Rows { get; } = [];

    public IReadOnlyList<RuleChoice> Rules { get; private set; } = [];

    public IReadOnlyList<FilterChoice> Filters { get; private set; } = [];

    [ObservableProperty]
    private FilterChoice? _filter;

    [ObservableProperty]
    private string _search = "";

    /// <summary>The rule new entries get.</summary>
    [ObservableProperty]
    private RuleChoice? _newRule;

    /// <summary>True when the profile has entries at all (otherwise a hint is shown instead of the list).</summary>
    [ObservableProperty]
    private bool _hasEntries;

    partial void OnFilterChanged(FilterChoice? value)
    {
        if (_loading || value is null) return;
        // Adding while looking at games adds games, and the other way round.
        if (value.Value != AppFilter.All) NewRule = Rules.First(r => r.IsGame == (value.Value == AppFilter.Games));
        ApplyFilter();
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    private RuleCategory NewCategory => NewRule?.IsGame == true ? RuleCategory.Games : RuleCategory.Other;

    /// <summary>Pick programs: each is blocked by its fingerprint (renaming or moving does not help) and by its name (new versions too).</summary>
    [RelayCommand]
    private Task AddPrograms()
    {
        var paths = _session.Dialogs.PickFiles(Loc.T("Ui.Apps.PickPrograms"), Loc.T("Ui.Apps.ProgramsFilter") + "|*.exe;*.com;*.bat;*.cmd;*.msi");
        if (paths.Count == 0) return Task.CompletedTask;
        var category = NewCategory;
        return _session.RunAsync(async () =>
        {
            var entries = paths.Select(p => _fingerprint(p, category)).ToList();
            await _session.EditPresetAsync(c =>
            {
                foreach (var entry in entries)
                {
                    if (!c.BlockedFiles.Any(f => f.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))) ConfigEditor.AddBlockedFile(c, entry);
                    if (!c.BlockedNames.Any(n => n.Name.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase)))
                        ConfigEditor.AddBlockedName(c, entry.FileName, category);
                }
            }).ConfigureAwait(true);
        });
    }

    [RelayCommand]
    private Task AddFolders()
    {
        var paths = _session.Dialogs.PickFolders(Loc.T("Ui.Apps.PickFolders"));
        if (paths.Count == 0) return Task.CompletedTask;
        var category = NewCategory;
        return _session.EditPresetAsync(c =>
        {
            foreach (var p in paths) SkipDuplicate(() => ConfigEditor.AddBlockedPath(c, p, category, _session.Paths));
        });
    }

    /// <summary>Program names typed by hand; several can be separated by commas or spaces.</summary>
    [RelayCommand]
    private Task AddNames()
    {
        var text = _session.Dialogs.Prompt(Loc.T("Ui.Apps.NamePrompt"), "");
        var names = (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return Task.CompletedTask;
        var category = NewCategory;
        return _session.EditPresetAsync(c =>
        {
            foreach (var n in names) SkipDuplicate(() => ConfigEditor.AddBlockedName(c, n, category));
        });
    }

    [RelayCommand]
    private Task Remove(AppRow? row)
    {
        if (row is null) return Task.CompletedTask;
        return _session.EditPresetAsync(c =>
        {
            switch (row.Kind)
            {
                case BlockKind.Folder:
                    ConfigEditor.RemoveBlockedPath(c, row.Key);
                    break;
                case BlockKind.Name:
                    ConfigEditor.RemoveBlockedName(c, row.Key);
                    break;
                case BlockKind.Program:
                    var file = c.BlockedFiles.FirstOrDefault(f => f.Sha256.Equals(row.Key, StringComparison.OrdinalIgnoreCase));
                    ConfigEditor.RemoveBlockedFile(c, row.Key);
                    // The name goes too, unless another blocked file has the same name.
                    if (file is not null && !c.BlockedFiles.Any(f => f.FileName.Equals(file.FileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        try { ConfigEditor.RemoveBlockedName(c, file.FileName); }
                        catch (UcException) { }
                    }
                    break;
            }
        });
    }

    private void RuleChanged(AppRow row, RuleChoice rule)
    {
        if (_loading) return;
        var category = rule.IsGame ? RuleCategory.Games : RuleCategory.Other;
        _ = _session.EditPresetAsync(c => ConfigEditor.SetBlockedCategory(c, row.Key, category));
    }

    // ------------------------------------------------------------ finding installed games

    public ObservableCollection<SelectableItem> Found { get; } = [];

    [ObservableProperty]
    private bool _hasFound;

    [RelayCommand]
    private Task Find() => _session.RunAsync(async () =>
    {
        var all = await _session.Client.FindGamesAsync().ConfigureAwait(true);
        var found = _session.Preset is { } p ? GameFinder.NotListed(all, p) : all;
        foreach (var f in Found) f.PropertyChanged -= FoundChanged;
        Found.Clear();
        foreach (var f in found)
        {
            var item = new SelectableItem(f);
            item.PropertyChanged += FoundChanged;
            Found.Add(item);
        }
        HasFound = Found.Count > 0;
        AddFoundCommand.NotifyCanExecuteChanged();
        _session.ShowMessage(MessageKind.Info, found.Count == 0 ? Loc.T("Cli.GamesFoundNone") : Loc.T("Ui.Games.FoundCount", found.Count));
    });

    private void FoundChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => AddFoundCommand.NotifyCanExecuteChanged();

    private bool AnyFoundSelected() => Found.Any(f => f.IsSelected);

    /// <summary>The ticked game folders become games of the profile.</summary>
    [RelayCommand(CanExecute = nameof(AnyFoundSelected))]
    private async Task AddFound()
    {
        var chosen = Found.Where(f => f.IsSelected).Select(f => f.Text).ToList();
        if (await _session.EditPresetAsync(c => { foreach (var p in chosen) SkipDuplicate(() => ConfigEditor.AddBlockedPath(c, p, RuleCategory.Games, _session.Paths)); }).ConfigureAwait(true))
        {
            CloseFound();
        }
    }

    [RelayCommand]
    private void CloseFound()
    {
        Found.Clear();
        HasFound = false;
    }

    // ------------------------------------------------------------ building the list

    private static void SkipDuplicate(Action add)
    {
        try { add(); }
        catch (UcException e) when (e.Code == ErrorCodes.AlreadyInList) { }
    }

    private void BuildChoices()
    {
        var newIsGame = NewRule?.IsGame ?? false;
        Rules = [new RuleChoice(true, Loc.T("Ui.Apps.Rule.Game")), new RuleChoice(false, Loc.T("Ui.Apps.Rule.Blocked"))];
        OnPropertyChanged(nameof(Rules));
        NewRule = Rules.First(r => r.IsGame == newIsGame);
    }

    private static string Detail(BlockKind kind, RuleCategory category, string? note)
    {
        var parts = new List<string> { Loc.T("Ui.Apps.Kind." + kind) };
        if (category is RuleCategory.Vpn or RuleCategory.Tools) parts.Add(Loc.T("Ui.Category." + category));
        if (!string.IsNullOrWhiteSpace(note)) parts.Add(note);
        return string.Join(" · ", parts);
    }

    internal void Rebuild(Preset? c)
    {
        _loading = true;
        try
        {
            BuildChoices();
            var rows = new List<AppRow>();
            if (c is not null)
            {
                static bool Shown(RuleCategory k) => k != RuleCategory.Writable;
                var files = c.BlockedFiles.Where(f => Shown(f.Category)).ToList();
                rows.AddRange(files.Select(f => new AppRow(BlockKind.Program, f.Sha256, f.FileName, Detail(BlockKind.Program, f.Category, f.Note), f.Category, Rules, RuleChanged)));
                rows.AddRange(c.BlockedNames
                    .Where(n => Shown(n.Category) && !files.Any(f => f.FileName.Equals(n.Name, StringComparison.OrdinalIgnoreCase)))   // shown with its file
                    .Select(n => new AppRow(BlockKind.Name, n.Name, n.Name, Detail(BlockKind.Name, n.Category, n.Note), n.Category, Rules, RuleChanged)));
                rows.AddRange(c.BlockedPaths.Where(p => Shown(p.Category))
                    .Select(p => new AppRow(BlockKind.Folder, p.Path, p.Path, Detail(BlockKind.Folder, p.Category, p.Note), p.Category, Rules, RuleChanged)));
            }
            // Games first, then by name.
            _all = [.. rows.OrderByDescending(r => r.IsGame).ThenBy(r => r.Text, StringComparer.OrdinalIgnoreCase)];
            HasEntries = _all.Count > 0;

            var current = Filter?.Value ?? AppFilter.All;
            Filters =
            [
                new FilterChoice(AppFilter.All, Loc.T("Ui.Apps.Filter.All"), _all.Count),
                new FilterChoice(AppFilter.Games, Loc.T("Ui.Apps.Filter.Games"), _all.Count(r => r.IsGame)),
                new FilterChoice(AppFilter.Blocked, Loc.T("Ui.Apps.Filter.Blocked"), _all.Count(r => !r.IsGame)),
            ];
            OnPropertyChanged(nameof(Filters));
            Filter = Filters.First(f => f.Value == current);
        }
        finally
        {
            _loading = false;
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var f = Filter?.Value ?? AppFilter.All;
        var s = Search.Trim();
        Rows.Clear();
        foreach (var r in _all)
        {
            if (f == AppFilter.Games && !r.IsGame) continue;
            if (f == AppFilter.Blocked && r.IsGame) continue;
            if (s.Length > 0 && !r.Text.Contains(s, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(r);
        }
    }
}

/// <summary>
/// Programs and games of one profile in one place, like the "Apps and games" of family safety tools:
/// games time with its schedule, every program with its rule, and folders that always run.
/// </summary>
public sealed partial class AppsPageViewModel : PageViewModel
{
    public AppsPageViewModel(PanelSession session, Func<string, RuleCategory, FileHashEntry> fingerprint)
        : base(session, "Ui.Nav.Apps", "")
    {
        GameTime = new GameTimeViewModel(session);
        Apps = new AppListViewModel(session, fingerprint);
    }

    public override bool UsesPreset => true;

    public GameTimeViewModel GameTime { get; }

    public AppListViewModel Apps { get; }

    /// <summary>Exceptions: program folders that run although they are inside a blocked place.</summary>
    public ObservableCollection<FolderRow> AllowedFolders { get; } = [];

    [RelayCommand]
    private Task AllowFolders()
    {
        var paths = Session.Dialogs.PickFolders(Loc.T("Ui.Apps.PickAllowFolders"));
        return paths.Count == 0 ? Task.CompletedTask : Session.EditPresetAsync(c =>
        {
            foreach (var p in paths)
            {
                try { ConfigEditor.AddAllowedPath(c, p); }
                catch (UcException e) when (e.Code == ErrorCodes.AlreadyInList) { }
            }
        });
    }

    [RelayCommand]
    private Task RemoveAllowedFolder(FolderRow? row) => row is null ? Task.CompletedTask : Session.EditPresetAsync(c => ConfigEditor.RemoveAllowedPath(c, row.Path));

    protected override void Rebuild()
    {
        var preset = Session.Preset;
        GameTime.Rebuild(preset, Session.Status);
        Apps.Rebuild(preset);
        Replace(AllowedFolders, (preset?.AllowedPaths ?? []).Select(e => new FolderRow(e.Path, e.Note)));
    }
}
