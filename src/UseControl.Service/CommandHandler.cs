using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Ipc;

namespace UseControl.Service;

/// <summary>Turns pipe requests into controller calls.</summary>
public sealed class CommandHandler(UcController controller)
{
    public async Task<Response> HandleAsync(Request request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        object result = request.Command switch
        {
            Commands.Ping => new PingResult(typeof(CommandHandler).Assembly.GetName().Version?.ToString(3) ?? "?", Environment.ProcessId),
            Commands.GetStatus => await controller.GetStatusAsync(ct).ConfigureAwait(false),
            Commands.GetConfig => await controller.GetConfigAsync(ct).ConfigureAwait(false),
            Commands.SetConfig => await controller.SetConfigAsync(Payload<UcConfig>(request), ct).ConfigureAwait(false),
            Commands.Apply => await controller.ApplyAsync(ct).ConfigureAwait(false),
            Commands.Suspend => await controller.SuspendAsync(ct).ConfigureAwait(false),
            Commands.LockGames => await controller.LockGamesAsync(IpcJson.FromElement<GamesRequest>(request.Payload)?.User, ct).ConfigureAwait(false),
            Commands.UnlockGames => await controller.UnlockGamesAsync(IpcJson.FromElement<GamesRequest>(request.Payload)?.User, ct).ConfigureAwait(false),
            Commands.Preview => await Preview(ct).ConfigureAwait(false),
            Commands.Audit => await controller.AuditAsync(ct).ConfigureAwait(false),
            Commands.FindGames => await controller.FindGamesAsync(ct).ConfigureAwait(false),
            _ => throw new UcException(ErrorCodes.UnknownCommand, request.Command),
        };
        return new Response(true, IpcJson.ToElement(result));
    }

    private async Task<PreviewResult> Preview(CancellationToken ct)
    {
        var p = await controller.PreviewAsync(ct).ConfigureAwait(false);
        return new PreviewResult(p.Accounts, p.AutoPaths);
    }

    private static T Payload<T>(Request r)
    {
        try
        {
            return IpcJson.FromElement<T>(r.Payload) ?? throw new UcException(ErrorCodes.InvalidRequest, "missing payload");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new UcException(ErrorCodes.InvalidRequest, e.Message);
        }
    }
}
