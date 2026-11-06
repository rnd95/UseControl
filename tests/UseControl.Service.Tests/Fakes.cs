using Microsoft.Extensions.Logging.Abstractions;
using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Windows;
using UseControl.Windows.Audit;
using UseControl.Windows.Games;
using UseControl.Windows.Restrictions;

namespace UseControl.Service.Tests;

/// <summary>A clock the tests move by hand, in UTC (Friday 6 November 2026, 12:00).</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Records what the controller asks for; never touches Windows.</summary>
internal sealed class FakeEngine : IEngine
{
    /// <summary>Every apply, with the accounts whose games were allowed.</summary>
    public List<(UcConfig Config, HashSet<string> Games)> Applies { get; } = [];
    public List<string> Removed { get; } = [];
    public int RemoveAllCalls { get; private set; }

    /// <summary>The games "running" now.</summary>
    public List<RunningGame> Running { get; } = [];
    public List<RunningGame> Closed { get; } = [];
    public List<(string User, string Key)> Messages { get; } = [];

    /// <summary>What ReadStates returns; by default exactly what the last apply wrote.</summary>
    public Func<string, RestrictionState?>? StateOverride { get; set; }

    public static RunningGame Game(string user) => new(user, "S-1-5-21-1-2-3-1001", 4242, @"D:\Games\racer.exe", 1);

    public ApplyResult Apply(UcConfig config, Func<string, bool> gamesAllowed)
    {
        Applies.Add((config, new HashSet<string>(config.Users.Where(gamesAllowed), StringComparer.OrdinalIgnoreCase)));
        return new ApplyResult([.. config.Users.Select(u => new AccountResult(u, true, null, 10, 3, 5, 1, false))], []);
    }

    public AccountResult RemoveAccount(string user)
    {
        Removed.Add(user);
        return new AccountResult(user, true, null, 0, 0, 0, 0, false);
    }

    public ApplyResult RemoveAll(UcConfig config)
    {
        RemoveAllCalls++;
        return new ApplyResult([.. config.Users.Select(u => new AccountResult(u, true, null, 0, 0, 0, 0, false))], []);
    }

    public PreviewData Preview(UcConfig config, Func<string, bool> gamesAllowed) => new([], []);

    public IReadOnlyDictionary<string, RestrictionState?> ReadStates(UcConfig config) =>
        config.Users.ToDictionary(u => u, u => StateOverride is null
            ? new RestrictionState(true, 10, 3, 2, 5, new Dictionary<string, int>(), true)
            : StateOverride(u), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AuditCheck> Audit(UcConfig config) => [new AuditCheck("uac", AuditStatus.Ok, "fine")];

    public IReadOnlyList<string> FindGames() => [@"D:\Games"];

    public bool StoreInstallsAdminOnly() => true;

    public IReadOnlyList<RunningGame> RunningGames(UcConfig config) => [.. Running];

    public int CloseGames(IReadOnlyList<RunningGame> games)
    {
        Closed.AddRange(games);
        foreach (var g in games) Running.Remove(g);
        return games.Count;
    }

    public void Notify(RunningGame game, string key, params object?[] args) => Messages.Add((game.User, key));
}

internal sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        Directory.CreateDirectory(DataDirectory);
        Controller = NewController();
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "uc-svc-" + Guid.NewGuid().ToString("N"));
    public FakeEngine Engine { get; } = new();
    public ManualTime Time { get; } = new();
    public UcController Controller { get; private set; }

    /// <summary>A new controller on the same data folder, as after a service restart.</summary>
    public UcController Restart()
    {
        Controller.Dispose();
        Controller = NewController();
        return Controller;
    }

    private UcController NewController() => new(DataDirectory, Engine, new FixedSystemPaths(), Time, NullLogger.Instance);

    public void Dispose()
    {
        Controller.Dispose();
        Directory.Delete(DataDirectory, recursive: true);
    }
}
