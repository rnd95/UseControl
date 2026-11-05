using Microsoft.Extensions.Logging;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Ipc;
using UseControl.Windows;

namespace UseControl.Service;

/// <summary>
/// The service's brain: owns the configuration and the state, applies them through the engine, runs the
/// games timer and puts the restrictions back when something removed them. One operation at a time.
/// </summary>
public sealed partial class UcController : IDisposable
{
    public const double MinGamesHours = 0.25;
    public const double MaxGamesHours = 24;

    private static readonly TimeSpan DriftCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FullApplyInterval = TimeSpan.FromHours(6);

    private readonly IEngine _engine;
    private readonly ISystemPaths _sp;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _configPath;
    private readonly string _statePath;
    private DateTimeOffset _lastDriftCheck;

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

    private bool GamesAllowedNow => State.GamesAllowedUntil is { } u && u > Now;

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
            if (State.GamesAllowedUntil is { } u && u <= Now) State.GamesAllowedUntil = null;
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
        State.GamesAllowedUntil = null;
        var r = _engine.RemoveAll(Config);
        State.LastResults = [.. r.Accounts];
        State.Save(_statePath);
        LogSuspended(_log);
        return r;
    }, ct);

    public Task<ServiceStatus> AllowGamesAsync(double hours, CancellationToken ct) => Locked(() =>
    {
        if (double.IsNaN(hours) || hours < MinGamesHours || hours > MaxGamesHours)
            throw new UcException(ErrorCodes.HoursOutOfRange, MinGamesHours, MaxGamesHours);
        State.GamesAllowedUntil = Now.AddHours(hours);
        ApplyLocked($"games allowed until {State.GamesAllowedUntil:u}");
        return Status([]);
    }, ct);

    public Task<ServiceStatus> LockGamesAsync(CancellationToken ct) => Locked(() =>
    {
        State.GamesAllowedUntil = null;
        ApplyLocked("games blocked");
        return Status([]);
    }, ct);

    public Task<PreviewData> PreviewAsync(CancellationToken ct) => Locked(() => _engine.Preview(Config, GamesAllowedNow), ct);

    public Task<IReadOnlyList<Windows.Audit.AuditCheck>> AuditAsync(CancellationToken ct) => Locked(() => _engine.Audit(Config), ct);

    public Task<IReadOnlyList<string>> FindGamesAsync(CancellationToken ct) => Locked(() => _engine.FindNewGames(Config), ct);

    /// <summary>
    /// Called every minute: ends games time when it is over, and every few minutes checks that the
    /// restrictions are still in place (an account's registry could have been changed while the
    /// service was stopped, or restored from a backup).
    /// </summary>
    public Task TickAsync(CancellationToken ct) => Locked(() =>
    {
        if (State.GamesAllowedUntil is { } until && until <= Now)
        {
            State.GamesAllowedUntil = null;
            ApplyLocked("games time is over");
            return true;
        }
        if (!State.Enabled || Config.Users.Count == 0) return false;
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
        var r = _engine.Apply(Config, GamesAllowedNow);
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
        GamesAllowedNow ? State.GamesAllowedUntil : null,
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
}
