using UseControl.Core.Configuration;
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
    public DateTimeOffset? GamesUntil { get; private set; }
    public int SetConfigCalls { get; private set; }
    public List<string> RemovedUsers { get; } = [];

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

    public Task<ServiceStatus> AllowGamesAsync(double hours, CancellationToken ct = default)
    {
        if (hours < 0.25 || hours > 24) throw new Core.UcException(Core.ErrorCodes.HoursOutOfRange, 0.25, 24);
        GamesUntil = DateTimeOffset.Now.AddHours(hours);
        return Task.FromResult(Status());
    }

    public Task<ServiceStatus> LockGamesAsync(CancellationToken ct = default)
    {
        GamesUntil = null;
        return Task.FromResult(Status());
    }

    public Task<PreviewResult> PreviewAsync(CancellationToken ct = default) =>
        Task.FromResult(new PreviewResult(
            [new AccountPreview("kid", new RuleSet([new PathRule(@"D:\", RuleSource.Automatic)], [new PathRule(@"C:\Windows", RuleSource.System)], ["cmd.exe"], []), null)],
            [new AutoPath(@"D:\", "drive writable by ordinary users")]));

    public Task<IReadOnlyList<AuditCheck>> AuditAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditCheck>>([new AuditCheck("uac", AuditStatus.Warning, "EnableLUA=1"), new AuditCheck("services", AuditStatus.Ok, "fine")]);

    public Task<IReadOnlyList<string>> FindGamesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([@"D:\NewGames"]);

    private ApplyResult Applied() => new([.. Config.Users.Select(u => new AccountResult(u, true, null, 40, 4, Config.BlockedNames.Count, Config.BlockedFiles.Count, u == "kid"))], []);

    private ServiceStatus Status() => new("0.1.0", Enabled, GamesUntil, DateTimeOffset.Now, [], [.. Config.Users.Select(u => new AccountStatus(u, null, "x"))], true, @"C:\ProgramData\UseControl");
}
