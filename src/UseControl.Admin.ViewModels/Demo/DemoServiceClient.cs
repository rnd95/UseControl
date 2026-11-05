using UseControl.Core;
using UseControl.Core.Configuration;
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
    private DateTimeOffset? _gamesUntil;

    public DemoServiceClient()
    {
        _config = ConfigSerializer.LoadDefault();
        _config.Users = ["kids", "guest"];
        _config.BlockedPaths.Add(new PathEntry { Path = @"D:\Games", Category = RuleCategory.Games });
        _config.AllowedPaths.Add(new AllowedPathEntry { Path = @"C:\Tools", Note = "school software" });
        _config.BlockedFiles.Add(new FileHashEntry { FileName = "minecraft-portable.exe", Sha256 = new string('A', 64), Md5 = new string('B', 32), Size = 1024, Category = RuleCategory.Games });
        _config.FileAccess.Add(new FileAccessRule { Path = @"D:\Private", Mode = FileAccessMode.Deny, Rights = FileAccessRights.Full });
        _config.FileAccess.Add(new FileAccessRule { Path = @"D:\Homework", Mode = FileAccessMode.Allow, Rights = FileAccessRights.Read | FileAccessRights.Write, Users = ["kids"] });
        _config.Browsers.Chrome.ExtensionsAllowed.Add("cjpalhdlnbpafiamejdnhcphjbkeiagm");
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

    public Task<ServiceStatus> AllowGamesAsync(double hours, CancellationToken ct = default)
    {
        if (hours < 0.25 || hours > 24) throw new UcException(ErrorCodes.HoursOutOfRange, 0.25, 24);
        _gamesUntil = DateTimeOffset.Now.AddHours(hours);
        return Task.FromResult(Status());
    }

    public Task<ServiceStatus> LockGamesAsync(CancellationToken ct = default)
    {
        _gamesUntil = null;
        return Task.FromResult(Status());
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

    private ApplyResult Applied() => new([.. _config.Users.Select(u => new AccountResult(u, true, null, 47, 4, _config.BlockedNames.Count, _config.BlockedFiles.Count, false))], []);

    private ServiceStatus Status() => new(
        "0.1.0",
        _enabled,
        _gamesUntil > DateTimeOffset.Now ? _gamesUntil : null,
        DateTimeOffset.Now.AddMinutes(-3),
        [],
        [.. _config.Users.Select(u => new AccountStatus(u,
            _enabled ? new RestrictionState(true, 47, 4, _config.BlockedFiles.Count * 2, _config.BlockedNames.Count, new Dictionary<string, int>(), true) : new RestrictionState(false, 0, 0, 0, 0, new Dictionary<string, int>(), false),
            null))],
        true,
        @"C:\ProgramData\UseControl");

    private static UcConfig Copy(UcConfig c) => ConfigSerializer.Parse(ConfigSerializer.Serialize(c));
}
