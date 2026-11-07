using UseControl.Core.Configuration;
using UseControl.Windows;
using UseControl.Windows.Audit;

namespace UseControl.Ipc;

/// <summary>What clients (uc.exe, the panel) can ask the service. <see cref="UcClient"/> is the real one.</summary>
public interface IServiceClient
{
    Task<PingResult> PingAsync(CancellationToken ct = default);

    Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default);

    Task<UcConfig> GetConfigAsync(CancellationToken ct = default);

    Task<SetConfigResult> SetConfigAsync(UcConfig config, CancellationToken ct = default);

    Task<ApplyResult> ApplyAsync(CancellationToken ct = default);

    Task<ApplyResult> SuspendAsync(CancellationToken ct = default);

    /// <summary>Blocks games until the end of the day, for one account (or every one, when <paramref name="user"/> is null).</summary>
    Task<ServiceStatus> LockGamesAsync(string? user, CancellationToken ct = default);

    /// <summary>Takes such a block back: the schedule applies again.</summary>
    Task<ServiceStatus> UnlockGamesAsync(string? user, CancellationToken ct = default);

    Task<PreviewResult> PreviewAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AuditCheck>> AuditAsync(CancellationToken ct = default);

    /// <summary>Game folders found on the PC (whether a preset lists them already is up to the caller).</summary>
    Task<IReadOnlyList<string>> FindGamesAsync(CancellationToken ct = default);

    /// <summary>Looks for a new release now.</summary>
    Task<UpdateStatus> CheckUpdateAsync(CancellationToken ct = default);

    /// <summary>Downloads and checks the new release, then starts its installer (which restarts the service).</summary>
    Task<UpdateStatus> InstallUpdateAsync(CancellationToken ct = default);
}
