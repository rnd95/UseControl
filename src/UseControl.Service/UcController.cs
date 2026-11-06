using Microsoft.Extensions.Logging;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Core.Games;
using UseControl.Core.Paths;
using UseControl.Ipc;
using UseControl.Windows;
using UseControl.Windows.Games;

namespace UseControl.Service;

/// <summary>
/// The service's brain: owns the configuration and the state, applies them through the engine, keeps
/// games time (schedules, time an administrator gave, play counted while a game runs) and puts the
/// restrictions back when something removed them. One operation at a time.
/// </summary>
public sealed partial class UcController : IDisposable
{
    public const double MinGamesHours = 0.25;
    public const double MaxGamesHours = 24;

    private static readonly TimeSpan DriftCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FullApplyInterval = TimeSpan.FromHours(6);

    /// <summary>A warning is shown this long before games time ends.</summary>
    private static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(5);

    /// <summary>Games still running this long after the end of games time are closed.</summary>
    private static readonly TimeSpan CloseAfter = TimeSpan.FromMinutes(1);

    /// <summary>Longest gap between two ticks counted as play (the PC may have slept in between).</summary>
    private static readonly TimeSpan MaxTickGap = TimeSpan.FromMinutes(2);

    private readonly IEngine _engine;
    private readonly ISystemPaths _sp;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _configPath;
    private readonly string _statePath;
    private DateTimeOffset _lastDriftCheck;
    private DateTimeOffset? _lastTick;

    // Accounts whose game rules were left out at the last apply.
    private HashSet<string> _gamesApplied = new(StringComparer.OrdinalIgnoreCase);

    // Games running at the last tick, the accounts already warned, and when to close games.
    private IReadOnlyList<RunningGame> _running = [];
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _closeAt = new(StringComparer.OrdinalIgnoreCase);

    public UcController(string dataDirectory, IEngine engine, ISystemPaths sp, TimeProvider time, ILogger log)
    {
        DataDirectory = dataDirectory;
        _configPath = Path.Combine(dataDirectory, "config.json");
        _statePath = Path.Combine(dataDirectory, "state.json");
        _engine = engine;
        _sp = sp;
        _time = time;
        _log = log;
        Config = new UcConfig();
        State = new ServiceState();
    }

    public string DataDirectory { get; }

    public UcConfig Config { get; private set; }

    public ServiceState State { get; private set; }

    private DateTimeOffset Now => _time.GetUtcNow();

    private TimeZoneInfo Zone => _time.LocalTimeZone;

    private GamesState GamesOf(string user)
    {
        if (!State.Games.TryGetValue(user, out var s)) State.Games[user] = s = new GamesState();
        return s;
    }

    private GamesDecision Decide(string user) => Config.PresetOf(user) is { } p
        ? GamesClock.Evaluate(p.Games, GamesOf(user), Now, Zone)
        : new GamesDecision(false, GamesReason.Blocked, null, null, null);

    private HashSet<string> GamesAllowedNow() => new(Config.Users.Where(u => Decide(u).Allowed), StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads (or creates) the configuration and state and applies them.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (File.Exists(_configPath))
            {
                Config = ConfigSerializer.Load(_configPath);
            }
            else
            {
                Config = ConfigSerializer.LoadDefault();
                ConfigSerializer.Save(Config, _configPath);
            }
            State = ServiceState.Load(_statePath);
            foreach (var g in State.Games.Values) GamesClock.StartDay(g, Now, Zone);
            ApplyLocked("service start");
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct) => Locked(() =>
    {
        var states = Config.Users.Count > 0 ? _engine.ReadStates(Config) : new Dictionary<string, Windows.Restrictions.RestrictionState?>();
        var accounts = Config.Users.Select(u => new AccountStatus(u, states.GetValueOrDefault(u), states.GetValueOrDefault(u) is null ? "Could not read" : null)).ToList();
        return Status(accounts);
    }, ct);

    public Task<UcConfig> GetConfigAsync(CancellationToken ct) =>
        Locked(() => ConfigSerializer.Parse(ConfigSerializer.Serialize(Config)), ct);   // a copy

    /// <summary>Validates, saves and applies a new configuration. Accounts no longer listed lose their restrictions.</summary>
    public Task<SetConfigResult> SetConfigAsync(UcConfig newConfig, CancellationToken ct) => Locked(() =>
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        var normalized = ConfigSerializer.Parse(ConfigSerializer.Serialize(newConfig));
        ConfigValidator.EnsureValid(normalized, _sp);

        var removedUsers = Config.Users.Where(u => !normalized.Users.Contains(u, StringComparer.OrdinalIgnoreCase)).ToList();
        var removed = removedUsers.Select(_engine.RemoveAccount).ToList();
        foreach (var u in removedUsers) State.Games.Remove(u);

        ConfigSerializer.Save(normalized, _configPath);
        Config = normalized;
        LogConfigSaved(_log, Config.Users.Count, removedUsers.Count);
        var applied = State.Enabled ? ApplyLocked("configuration changed") : null;
        return new SetConfigResult(applied, removed);
    }, ct);

    /// <summary>Turns the restrictions on (again, after a suspend) and applies them now.</summary>
    public Task<ApplyResult> ApplyAsync(CancellationToken ct) => Locked(() =>
    {
        State.Enabled = true;
        return ApplyLocked("requested") ?? new ApplyResult([], []);
    }, ct);

    /// <summary>Removes all restrictions and keeps them off until <see cref="ApplyAsync"/>. The configuration is kept.</summary>
    public Task<ApplyResult> SuspendAsync(CancellationToken ct) => Locked(() =>
    {
        State.Enabled = false;
        foreach (var g in State.Games.Values)
        {
            // Time an administrator gave does not survive a suspend; what was played today stays counted.
            g.AllowedUntil = null;
            g.PlayMinutesLeft = 0;
        }
        var r = _engine.RemoveAll(Config);
        State.LastResults = [.. r.Accounts];
        State.Save(_statePath);
        LogSuspended(_log);
        return r;
    }, ct);

    /// <summary>Allows games for one account or every one: for some hours from now, or for some hours of play.</summary>
    public Task<ServiceStatus> AllowGamesAsync(string? user, double hours, bool countPlayOnly, CancellationToken ct) => Locked(() =>
    {
        if (double.IsNaN(hours) || hours < MinGamesHours || hours > MaxGamesHours)
            throw new UcException(ErrorCodes.HoursOutOfRange, MinGamesHours, MaxGamesHours);
        foreach (var u in Targets(user))
        {
            GamesClock.Allow(GamesOf(u), Now, hours, countPlayOnly);
            _closeAt.Remove(u);
        }
        ApplyLocked(countPlayOnly ? $"{hours} h of play allowed" : $"games allowed for {hours} h");
        return Status([]);
    }, ct);

    /// <summary>Blocks games now (inside a schedule: until the end of the day) and closes running games.</summary>
    public Task<ServiceStatus> LockGamesAsync(string? user, CancellationToken ct) => Locked(() =>
    {
        var targets = Targets(user);
        foreach (var u in targets) GamesClock.Lock(GamesOf(u), Now, Zone);
        var before = _gamesApplied;
        ApplyLocked("games blocked");
        foreach (var u in targets.Where(before.Contains)) GamesEnded(u);
        return Status([]);
    }, ct);

    private List<string> Targets(string? user)
    {
        if (user is null) return [.. Config.Users];
        var p = Config.PresetOf(user) ?? throw new UcException(ErrorCodes.NotRestricted, user);
        return [p.Accounts.First(a => a.Equals(user.Trim(), StringComparison.OrdinalIgnoreCase))];
    }

    public Task<PreviewData> PreviewAsync(CancellationToken ct) => Locked(() =>
    {
        var allowed = GamesAllowedNow();
        return _engine.Preview(Config, allowed.Contains);
    }, ct);

    public Task<IReadOnlyList<Windows.Audit.AuditCheck>> AuditAsync(CancellationToken ct) => Locked(() => _engine.Audit(Config), ct);

    public Task<IReadOnlyList<string>> FindGamesAsync(CancellationToken ct) => Locked(() => _engine.FindGames(), ct);

    /// <summary>
    /// Called every minute: counts play, warns before games time ends, blocks games when it is over and
    /// closes those still running a minute later; every few minutes checks that the restrictions are
    /// still in place (an account's registry could have been changed while the service was stopped,
    /// or restored from a backup).
    /// </summary>
    public Task TickAsync(CancellationToken ct) => Locked(() =>
    {
        var gap = _lastTick is { } last ? Now - last : TimeSpan.FromMinutes(1);
        _lastTick = Now;
        if (!State.Enabled || Config.Users.Count == 0) return false;

        var dirty = false;
        foreach (var u in Config.Users) dirty |= GamesClock.StartDay(GamesOf(u), Now, Zone);
        if (TickGames(gap)) return true;
        if (dirty) State.Save(_statePath);

        if (State.LastApplied is null || Now - State.LastApplied >= FullApplyInterval)
        {
            ApplyLocked("regular re-apply");
            return true;
        }
        if (Now - _lastDriftCheck < DriftCheckInterval) return false;
        _lastDriftCheck = Now;
        var drift = FindDrift();
        if (drift is not null) ApplyLocked("restrictions changed: " + drift);
        return drift is not null;
    }, ct);

    /// <summary>The games part of a tick. True when the restrictions were applied again.</summary>
    private bool TickGames(TimeSpan gap)
    {
        _running = FindRunningGames();
        var minutes = Math.Clamp(gap.TotalMinutes, 0, MaxTickGap.TotalMinutes);
        var playing = _running.Select(g => g.User).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var u in playing)
        {
            if (Config.PresetOf(u) is { } p) GamesClock.RecordPlay(p.Games, GamesOf(u), Now, Zone, minutes);
        }

        foreach (var u in Config.Users)
        {
            var d = Decide(u);
            var left = d.Remaining(Now);
            if (!d.Allowed || left is null || left > WarnBefore)
            {
                _warned.Remove(u);
                continue;
            }
            if (playing.Contains(u, StringComparer.OrdinalIgnoreCase) && _warned.Add(u))
                Notify(u, "Notify.GamesEndSoon", Math.Max(1, (int)Math.Ceiling(left.Value.TotalMinutes)));
        }

        var allowed = GamesAllowedNow();
        if (!allowed.SetEquals(_gamesApplied))
        {
            var ended = _gamesApplied.Where(u => !allowed.Contains(u)).ToList();
            ApplyLocked("games time changed");
            foreach (var u in ended) GamesEnded(u);
            return true;
        }

        foreach (var (u, at) in _closeAt.ToList())
        {
            if (at > Now) continue;
            _closeAt.Remove(u);
            if (allowed.Contains(u)) continue;
            var games = _running.Where(g => g.User.Equals(u, StringComparison.OrdinalIgnoreCase)).ToList();
            if (games.Count > 0) LogGamesClosed(_log, u, _engine.CloseGames(games));
        }
        if (playing.Count > 0) State.Save(_statePath);
        return false;
    }

    /// <summary>Games time of an account just ended: tell it, and close its games a minute later if the preset says so.</summary>
    private void GamesEnded(string user)
    {
        var close = Config.PresetOf(user)?.Games.CloseGamesWhenOver ?? false;
        if (!_running.Any(g => g.User.Equals(user, StringComparison.OrdinalIgnoreCase))) return;
        Notify(user, close ? "Notify.GamesOverClosing" : "Notify.GamesOver");
        if (close) _closeAt[user] = Now + CloseAfter;
    }

    private IReadOnlyList<RunningGame> FindRunningGames()
    {
        try
        {
            return _engine.RunningGames(Config);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            LogGamesFailed(_log, e.Message);
            return [];
        }
    }

    private void Notify(string user, string key, params object?[] args)
    {
        var game = _running.FirstOrDefault(g => g.User.Equals(user, StringComparison.OrdinalIgnoreCase));
        if (game is null) return;
        try
        {
            _engine.Notify(game, key, args);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            LogGamesFailed(_log, e.Message);
        }
    }

    /// <summary>Games time of every restricted account, for the panel and uc.exe.</summary>
    private List<AccountGames> GamesStatus()
    {
        var today = TimeZoneInfo.ConvertTime(Now, Zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var list = new List<AccountGames>();
        foreach (var p in Config.Presets)
        {
            foreach (var u in p.Accounts)
            {
                var st = GamesOf(u);
                var d = GamesClock.Evaluate(p.Games, st, Now, Zone);
                var playing = _running.Any(g => g.User.Equals(u, StringComparison.OrdinalIgnoreCase));
                list.Add(new AccountGames(u, p.Name, d.Allowed, d.Reason, d.Until, d.MinutesLeft, d.NextStart, st.Day == today ? st.PlayedToday : 0, playing));
            }
        }
        return list;
    }

    /// <summary>What differs between the last apply and the registry now, or null.</summary>
    private string? FindDrift()
    {
        var states = _engine.ReadStates(Config);
        foreach (var last in State.LastResults.Where(r => r.Success))
        {
            if (!states.TryGetValue(last.User, out var s) || s is null) continue;   // cannot read: nothing to compare
            if (!s.Applied) return $"{last.User}: removed";
            if (!s.KeysLocked) return $"{last.User}: keys unlocked";
            if (s.BlockedPathRules != last.BlockedPaths || s.AllowedPathRules != last.AllowedPaths || s.BlockedNames != last.BlockedNames || s.HashRules != last.BlockedFiles * 2)
                return $"{last.User}: rules differ";
        }
        return State.LastResults.Count(r => r.Success) < Config.Users.Count ? "an account was not restricted yet" : null;
    }

    private ApplyResult? ApplyLocked(string reason)
    {
        if (!State.Enabled)
        {
            State.Save(_statePath);
            return null;
        }
        var allowed = GamesAllowedNow();
        var r = _engine.Apply(Config, allowed.Contains);
        _gamesApplied = allowed;
        State.LastApplied = Now;
        State.LastResults = [.. r.Accounts];
        State.Save(_statePath);
        _lastDriftCheck = Now;
        LogApplied(_log, reason, r.Accounts.Count(a => a.Success), r.Accounts.Count);
        foreach (var f in r.Accounts.Where(a => !a.Success)) LogAccountFailed(_log, f.User, f.Error);
        foreach (var f in r.FileAccess.Where(c => c.Error is not null)) LogFileAccessFailed(_log, f.Path, f.Account, f.Error);
        return r;
    }

    private ServiceStatus Status(IReadOnlyList<AccountStatus> accounts) => new(
        typeof(UcController).Assembly.GetName().Version?.ToString(3) ?? "?",
        State.Enabled,
        GamesStatus(),
        State.LastApplied,
        State.LastResults,
        accounts,
        _engine.StoreInstallsAdminOnly(),
        DataDirectory);

    private async Task<T> Locked<T>(Func<T> action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return action();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    [LoggerMessage(1, LogLevel.Information, "Restrictions applied ({Reason}): {Ok} of {Total} accounts")]
    private static partial void LogApplied(ILogger log, string reason, int ok, int total);

    [LoggerMessage(2, LogLevel.Warning, "Could not restrict {User}: {Error}")]
    private static partial void LogAccountFailed(ILogger log, string user, string? error);

    [LoggerMessage(3, LogLevel.Warning, "File access for {Account} on {Path} failed: {Error}")]
    private static partial void LogFileAccessFailed(ILogger log, string path, string account, string? error);

    [LoggerMessage(4, LogLevel.Information, "Configuration saved: {Users} restricted accounts, {Removed} removed")]
    private static partial void LogConfigSaved(ILogger log, int users, int removed);

    [LoggerMessage(5, LogLevel.Warning, "Restrictions suspended by an administrator")]
    private static partial void LogSuspended(ILogger log);

    [LoggerMessage(6, LogLevel.Information, "Games time of {User} is over: {Count} games closed")]
    private static partial void LogGamesClosed(ILogger log, string user, int count);

    [LoggerMessage(7, LogLevel.Warning, "Games time: {Error}")]
    private static partial void LogGamesFailed(ILogger log, string error);
}
