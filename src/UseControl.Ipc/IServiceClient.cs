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

    Task<ServiceStatus> AllowGamesAsync(double hours, CancellationToken ct = default);

    Task<ServiceStatus> LockGamesAsync(CancellationToken ct = default);

    Task<PreviewResult> PreviewAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AuditCheck>> AuditAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> FindGamesAsync(CancellationToken ct = default);
}
