using System.IO.Pipes;
using System.Security.Principal;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Windows;
using UseControl.Windows.Audit;

namespace UseControl.Ipc;

/// <summary>
/// Talks to the UseControl service. Used by uc.exe and the admin panel; needs administrator rights
/// (the pipe only accepts elevated administrators and SYSTEM).
/// </summary>
public sealed class UcClient : IServiceClient
{
    private readonly string _pipeName;
    private readonly bool _requireSessionZero;

    public UcClient() : this(PipeProtocol.PipeName, requireSessionZero: true) { }

    /// <param name="pipeName">Pipe name (tests use their own).</param>
    /// <param name="requireSessionZero">Refuse a server that is not a service (protection against a program squatting the name).</param>
    internal UcClient(string pipeName, bool requireSessionZero)
    {
        _pipeName = pipeName;
        _requireSessionZero = requireSessionZero;
    }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Applying or checking can take a while (a scan of the drives); this is the limit for one request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public Task<PingResult> PingAsync(CancellationToken ct = default) => SendAsync<PingResult>(Commands.Ping, null, ct);

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default) => SendAsync<ServiceStatus>(Commands.GetStatus, null, ct);

    public Task<UcConfig> GetConfigAsync(CancellationToken ct = default) => SendAsync<UcConfig>(Commands.GetConfig, null, ct);

    /// <summary>Saves the configuration in the service and applies it; accounts no longer listed lose their restrictions.</summary>
    public Task<SetConfigResult> SetConfigAsync(UcConfig config, CancellationToken ct = default) => SendAsync<SetConfigResult>(Commands.SetConfig, config, ct);

    public Task<ApplyResult> ApplyAsync(CancellationToken ct = default) => SendAsync<ApplyResult>(Commands.Apply, null, ct);

    /// <summary>Removes all restrictions and keeps them off (the configuration stays) until <see cref="ApplyAsync"/>.</summary>
    public Task<ApplyResult> SuspendAsync(CancellationToken ct = default) => SendAsync<ApplyResult>(Commands.Suspend, null, ct);

    public Task<ServiceStatus> LockGamesAsync(string? user, CancellationToken ct = default) => SendAsync<ServiceStatus>(Commands.LockGames, new GamesRequest(user), ct);

    public Task<ServiceStatus> UnlockGamesAsync(string? user, CancellationToken ct = default) => SendAsync<ServiceStatus>(Commands.UnlockGames, new GamesRequest(user), ct);

    public Task<PreviewResult> PreviewAsync(CancellationToken ct = default) => SendAsync<PreviewResult>(Commands.Preview, null, ct);

    public Task<IReadOnlyList<AuditCheck>> AuditAsync(CancellationToken ct = default) => SendAsync<IReadOnlyList<AuditCheck>>(Commands.Audit, null, ct);

    public Task<IReadOnlyList<string>> FindGamesAsync(CancellationToken ct = default) => SendAsync<IReadOnlyList<string>>(Commands.FindGames, null, ct);

    public Task<UpdateStatus> CheckUpdateAsync(CancellationToken ct = default) => SendAsync<UpdateStatus>(Commands.CheckUpdate, null, ct);

    public Task<UpdateStatus> InstallUpdateAsync(CancellationToken ct = default) => SendAsync<UpdateStatus>(Commands.InstallUpdate, null, ct);

    public async Task<T> SendAsync<T>(string command, object? payload, CancellationToken ct = default)
    {
        var response = await SendRawAsync(new Request(command, payload is null ? null : IpcJson.ToElement(payload)), ct).ConfigureAwait(false);
        if (!response.Ok)
        {
            throw response.ErrorCode is { } code && code != ErrorCodes.Unknown
                ? new UcException(code, [.. response.ErrorArgs ?? []])
                : new UcException(ErrorCodes.ServiceFailed, response.Error ?? "?");
        }
        return IpcJson.FromElement<T>(response.Result) ?? throw new UcException(ErrorCodes.ServiceFailed, "empty answer");
    }

    public async Task<Response> SendRawAsync(Request request, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UcException(ErrorCodes.ServiceNotRunning);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UcException(ErrorCodes.AdministratorRequired);
        }
        catch (IOException)
        {
            throw new UcException(ErrorCodes.ServiceNotRunning);
        }

        if (_requireSessionZero && !PipeNative.IsServedFromSessionZero(pipe.SafePipeHandle)) throw new UcException(ErrorCodes.ServiceNotTrusted);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        await PipeProtocol.WriteAsync(pipe, request, cts.Token).ConfigureAwait(false);
        return await PipeProtocol.ReadAsync<Response>(pipe, cts.Token).ConfigureAwait(false);
    }
}
