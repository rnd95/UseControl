using UseControl.Core.Configuration;
using UseControl.Core.Games;
using UseControl.Core.Paths;
using UseControl.Core.Rules;
using UseControl.Ipc;
using UseControl.Windows;
using UseControl.Windows.Audit;

namespace UseControl.Cli.Tests;

/// <summary>Behaves like the service for the CLI: keeps a config, validates it, pretends to apply.</summary>
internal sealed class FakeService : IServiceClient
{
    public UcConfig Config { get; private set; } = ConfigSerializer.LoadDefault();
    public bool Enabled { get; private set; } = true;
    public Dictionary<string, GamesState> GameStates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int SetConfigCalls { get; private set; }
    public List<string> RemovedUsers { get; } = [];

    /// <summary>The first preset (most tests have one).</summary>
    public Preset Preset => Config.Presets[0];

    /// <summary>Restricts an account directly, as if done earlier (gives it its own preset).</summary>
    public FakeService Restrict(string user)
    {
        ConfigEditor.AddUser(Config, user);
        return this;
    }

    public Task<PingResult> PingAsync(CancellationToken ct = default) => Task.FromResult(new PingResult("0.1.0", 1234));

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status());

    public Task<UcConfig> GetConfigAsync(CancellationToken ct = default) =>
        Task.FromResult(ConfigSerializer.Parse(ConfigSerializer.Serialize(Config)));

    public Task<SetConfigResult> SetConfigAsync(UcConfig config, CancellationToken ct = default)
    {
        var c = ConfigSerializer.Parse(ConfigSerializer.Serialize(config));
        ConfigValidator.EnsureValid(c, new FixedSystemPaths());
        var removed = Config.Users.Where(u => !c.Users.Contains(u, StringComparer.OrdinalIgnoreCase)).ToList();
        RemovedUsers.AddRange(removed);
        Config = c;
        SetConfigCalls++;
        return Task.FromResult(new SetConfigResult(Enabled ? Applied() : null, [.. removed.Select(u => new AccountResult(u, true, null, 0, 0, 0, 0, false))]));
    }

    public Task<ApplyResult> ApplyAsync(CancellationToken ct = default)
    {
        Enabled = true;
        return Task.FromResult(Applied());
    }

    public Task<ApplyResult> SuspendAsync(CancellationToken ct = default)
    {
        Enabled = false;
        return Task.FromResult(new ApplyResult([.. Config.Users.Select(u => new AccountResult(u, true, null, 0, 0, 0, 0, false))], []));
    }

    public Task<ServiceStatus> UnlockGamesAsync(string? user, CancellationToken ct = default)
    {
        foreach (var u in Targets(user)) GamesClock.Unlock(GamesOf(u));
        return Task.FromResult(Status());
    }

    public Task<ServiceStatus> LockGamesAsync(string? user, CancellationToken ct = default)
    {
        foreach (var u in Targets(user)) GamesClock.Lock(GamesOf(u), DateTimeOffset.Now, TimeZoneInfo.Local);
        return Task.FromResult(Status());
    }

    private IReadOnlyList<string> Targets(string? user) =>
        user is null ? Config.Users : Config.PresetOf(user) is null ? throw new Core.UcException(Core.ErrorCodes.NotRestricted, user) : [user];

    private GamesState GamesOf(string user)
    {
        if (!GameStates.TryGetValue(user, out var s)) GameStates[user] = s = new GamesState();
        return s;
    }

    public Task<PreviewResult> PreviewAsync(CancellationToken ct = default) =>
        Task.FromResult(new PreviewResult(
            [new AccountPreview("kid", new RuleSet([new PathRule(@"D:\", RuleSource.Automatic)], [new PathRule(@"C:\Windows", RuleSource.System)], ["cmd.exe"], []), null)],
            [new AutoPath(@"D:\", "drive writable by ordinary users")]));

    public Task<IReadOnlyList<AuditCheck>> AuditAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditCheck>>([new AuditCheck("uac", AuditStatus.Warning, "EnableLUA=1"), new AuditCheck("services", AuditStatus.Ok, "fine")]);

    public Task<IReadOnlyList<string>> FindGamesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([@"D:\NewGames"]);

    /// <summary>What the update checks answer; tests change it.</summary>
    public UpdateStatus Update { get; set; } = new("0.1.0", "0.1.0", false, DateTimeOffset.Now, null, [], false, null, true);

    public int InstallUpdateCalls { get; private set; }

    public Task<UpdateStatus> CheckUpdateAsync(CancellationToken ct = default) => Task.FromResult(Update);

    public Task<UpdateStatus> InstallUpdateAsync(CancellationToken ct = default)
    {
        if (!Update.Available) throw new Core.UcException(Core.ErrorCodes.NoUpdate);
        InstallUpdateCalls++;
        return Task.FromResult(Update = Update with { Installing = true });
    }

    private ApplyResult Applied() => new([.. Config.Users.Select(u => new AccountResult(u, true, null, 40, 4, Config.PresetOf(u)!.BlockedNames.Count, Config.PresetOf(u)!.BlockedFiles.Count, u == "kid"))], []);

    private List<AccountGames> Games() => [.. Config.Presets.SelectMany(p => p.Accounts.Select(u =>
    {
        var st = GamesOf(u);
        var d = GamesClock.Evaluate(p.Games, st, DateTimeOffset.Now, TimeZoneInfo.Local);
        return new AccountGames(u, p.Name, d.Allowed, d.Reason, d.Until, d.MinutesLeft, d.NextStart, st.PlayedToday, false);
    }))];

    private ServiceStatus Status() => new("0.1.0", Enabled, Games(), DateTimeOffset.Now, [], [.. Config.Users.Select(u => new AccountStatus(u, null, "x"))], true, @"C:\ProgramData\UseControl", Update);
}
