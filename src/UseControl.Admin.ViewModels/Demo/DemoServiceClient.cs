using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Core.Games;
using UseControl.Localization;
using UseControl.Core.Paths;
using UseControl.Core.Rules;
using UseControl.Ipc;
using UseControl.Windows;
using UseControl.Windows.Audit;
using UseControl.Windows.Restrictions;

namespace UseControl.Admin.ViewModels.Demo;

/// <summary>
/// A pretend service with sample data, for "UseControl.exe --demo": shows every page filled in without
/// installing anything. Useful for screenshots and for working on the look of the panel.
/// Changes are kept in memory only.
/// </summary>
public sealed class DemoServiceClient : IServiceClient
{
    private UcConfig _config;
    private bool _enabled = true;
    private readonly Dictionary<string, GamesState> _games = new(StringComparer.OrdinalIgnoreCase);

    public DemoServiceClient()
    {
        _config = ConfigSerializer.LoadDefault();
        var kids = ConfigEditor.AddUser(_config, "kids");
        kids.BlockedPaths.Add(new PathEntry { Path = @"D:\Games", Category = RuleCategory.Games });
        kids.AllowedPaths.Add(new AllowedPathEntry { Path = @"C:\Tools", Note = "school software" });
        kids.BlockedFiles.Add(new FileHashEntry { FileName = "minecraft-portable.exe", Sha256 = new string('A', 64), Md5 = new string('B', 32), Size = 1024, Category = RuleCategory.Games });
        ConfigEditor.AddGamesWindow(kids, GamesText.Week.Take(5), "16:00", "20:00", 60);
        ConfigEditor.AddGamesWindow(kids, GamesText.Week.Skip(5), "10:00", "21:00", 180);
        kids.Browsers.Chrome.ExtensionsAllowed.Add("cjpalhdlnbpafiamejdnhcphjbkeiagm");
        ConfigEditor.AddUser(_config, "guest");
        _games["kids"] = new GamesState { Day = DateTimeOffset.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), PlayedToday = 25 };
        _config.FileAccess.Add(new FileAccessRule { Path = @"D:\Private", Mode = FileAccessMode.Deny, Rights = FileAccessRights.Full });
        _config.FileAccess.Add(new FileAccessRule { Path = @"D:\Homework", Mode = FileAccessMode.Allow, Rights = FileAccessRights.Read | FileAccessRights.Write, Users = ["kids"] });
        _config.Audit.PrivateFolders.Add(@"D:\Private");
    }

    public Task<PingResult> PingAsync(CancellationToken ct = default) => Task.FromResult(new PingResult("0.1.0", 0));

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status());

    public Task<UcConfig> GetConfigAsync(CancellationToken ct = default) => Task.FromResult(Copy(_config));

    public Task<SetConfigResult> SetConfigAsync(UcConfig config, CancellationToken ct = default)
    {
        var c = Copy(config);
        ConfigValidator.EnsureValid(c, WindowsSystemPaths.Current);
        var removed = _config.Users.Where(u => !c.Users.Contains(u, StringComparer.OrdinalIgnoreCase)).Select(u => new AccountResult(u, true, null, 0, 0, 0, 0, false)).ToList();
        _config = c;
        return Task.FromResult(new SetConfigResult(_enabled ? Applied() : null, removed));
    }

    public Task<ApplyResult> ApplyAsync(CancellationToken ct = default)
    {
        _enabled = true;
        return Task.FromResult(Applied());
    }

    public Task<ApplyResult> SuspendAsync(CancellationToken ct = default)
    {
        _enabled = false;
        return Task.FromResult(new ApplyResult([.. _config.Users.Select(u => new AccountResult(u, true, null, 0, 0, 0, 0, false))], []));
    }

    public Task<ServiceStatus> AllowGamesAsync(string? user, double hours, bool countPlayOnly, CancellationToken ct = default)
    {
        if (hours < 0.25 || hours > 24) throw new UcException(ErrorCodes.HoursOutOfRange, 0.25, 24);
        foreach (var u in user is null ? _config.Users : [user]) GamesClock.Allow(GamesOf(u), DateTimeOffset.Now, hours, countPlayOnly);
        return Task.FromResult(Status());
    }

    public Task<ServiceStatus> LockGamesAsync(string? user, CancellationToken ct = default)
    {
        foreach (var u in user is null ? _config.Users : [user]) GamesClock.Lock(GamesOf(u), DateTimeOffset.Now, TimeZoneInfo.Local);
        return Task.FromResult(Status());
    }

    private GamesState GamesOf(string user)
    {
        if (!_games.TryGetValue(user, out var s)) _games[user] = s = new GamesState();
        return s;
    }

    public Task<PreviewResult> PreviewAsync(CancellationToken ct = default) =>
        Task.FromResult(new PreviewResult([], [new AutoPath(@"D:\", "drive writable by ordinary users")]));

    public Task<IReadOnlyList<AuditCheck>> AuditAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditCheck>>(
    [
        Check("services", AuditStatus.Ok, "ServicesOk", null, "94"),
        Check("uac", AuditStatus.Warning, "UacWeak", null, "1", "0", "0", "3"),
        Check("privateFolder", AuditStatus.Fail, "PrivateOpen", @"D:\Private", @"BUILTIN\Users ReadAndExecute"),
        Check("remoteTools", AuditStatus.Ok, "RemoteKnown", null, "Mesh Agent, TeamViewer"),
        Check("restrictions", AuditStatus.Ok, "RestrictionsOk", "kids", "47", "4", "1", "41"),
    ]);

    private static AuditCheck Check(string id, AuditStatus status, string key, string? subject, params string[] args) =>
        new(id, status, Localization.Loc.English("AuditDetail." + key, args), subject) { DetailKey = "AuditDetail." + key, DetailArgs = args };

    public Task<IReadOnlyList<string>> FindGamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([@"C:\Program Files (x86)\Steam", @"E:\SteamLibrary"]);

    private ApplyResult Applied() => new([.. _config.Users.Select(u => new AccountResult(u, true, null, 47, 4, _config.PresetOf(u)!.BlockedNames.Count, _config.PresetOf(u)!.BlockedFiles.Count, false))], []);

    private List<AccountGames> Games() => [.. _config.Presets.SelectMany(p => p.Accounts.Select(u =>
    {
        var st = GamesOf(u);
        var d = GamesClock.Evaluate(p.Games, st, DateTimeOffset.Now, TimeZoneInfo.Local);
        return new AccountGames(u, p.Name, d.Allowed, d.Reason, d.Until, d.MinutesLeft, d.NextStart, st.PlayedToday, d.Allowed && u == "kids");
    }))];

    private ServiceStatus Status() => new(
        "0.1.0",
        _enabled,
        Games(),
        DateTimeOffset.Now.AddMinutes(-3),
        [],
        [.. _config.Users.Select(u => new AccountStatus(u,
            _enabled ? new RestrictionState(true, 47, 4, _config.PresetOf(u)!.BlockedFiles.Count * 2, _config.PresetOf(u)!.BlockedNames.Count, new Dictionary<string, int>(), true) : new RestrictionState(false, 0, 0, 0, 0, new Dictionary<string, int>(), false),
            null))],
        true,
        @"C:\ProgramData\UseControl");

    private static UcConfig Copy(UcConfig c) => ConfigSerializer.Parse(ConfigSerializer.Serialize(c));
}
