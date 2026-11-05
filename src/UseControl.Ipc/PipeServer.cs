using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using UseControl.Core;

namespace UseControl.Ipc;

public sealed class PipeServerOptions
{
    /// <summary>Who may connect. Null: Windows' default (the creator and administrators); the service passes <see cref="PipeServer.AdministratorsOnly"/>.</summary>
    public PipeSecurity? Security { get; init; }

    /// <summary>Check after connecting that the client really is an elevated administrator or SYSTEM.</summary>
    public bool RequireAdministrator { get; init; } = true;

    public int MaxConcurrentClients { get; init; } = 4;
}

/// <summary>Accepts connections on the control pipe and answers each request with the handler.</summary>
public sealed class PipeServer
{
    private readonly string _pipeName;
    private readonly Func<Request, CancellationToken, Task<Response>> _handler;
    private readonly PipeServerOptions _options;
    private readonly Action<string, Exception?> _log;

    public PipeServer(string pipeName, Func<Request, CancellationToken, Task<Response>> handler, PipeServerOptions options, Action<string, Exception?> log)
    {
        _pipeName = pipeName;
        _handler = handler;
        _options = options;
        _log = log;
    }

    /// <summary>Elevated administrators and SYSTEM, local only (access over the network denied).</summary>
    public static PipeSecurity AdministratorsOnly()
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return s;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var first = true;
        using var slots = new SemaphoreSlim(_options.MaxConcurrentClients);
        while (!ct.IsCancellationRequested)
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            NamedPipeServerStream pipe;
            try
            {
                // The first instance must be the first on the system: if another program already owns
                // the name, fail instead of sharing it.
                var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = _options.Security is null
                    ? new NamedPipeServerStream(_pipeName, PipeDirection.InOut, _options.MaxConcurrentClients, PipeTransmissionMode.Byte, options)
                    : NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, _options.MaxConcurrentClients, PipeTransmissionMode.Byte, options, 0, 0, _options.Security);
                first = false;
            }
            catch
            {
                slots.Release();
                throw;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                slots.Release();
                break;
            }

            _ = Task.Run(async () =>
            {
                try { await HandleAsync(pipe, ct).ConfigureAwait(false); }
                finally
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    slots.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var request = await PipeProtocol.ReadAsync<Request>(pipe, timeout.Token).ConfigureAwait(false);

            Response response;
            if (_options.RequireAdministrator && !ClientIsAdministrator(pipe))
            {
                response = Fail(new UcException(ErrorCodes.AdministratorRequired));
            }
            else
            {
                try
                {
                    response = await _handler(request, ct).ConfigureAwait(false);
                }
                catch (UcException e)
                {
                    response = Fail(e);
                }
#pragma warning disable CA1031 // A failing command must answer the client, whatever went wrong.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    _log($"Command {request.Command} failed", e);
                    response = new Response(false, ErrorCode: ErrorCodes.ServiceFailed, Error: e.Message, ErrorArgs: [e.Message]);
                }
            }
            await PipeProtocol.WriteAsync(pipe, response, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _log("A client connection ended badly", e);
        }
    }

    public static Response Fail(UcException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new Response(false, ErrorCode: e.Code, Error: e.Message, ErrorArgs: [.. e.Args.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture) ?? "")]);
    }

    private static bool ClientIsAdministrator(NamedPipeServerStream pipe)
    {
        var ok = false;
        pipe.RunAsClient(() =>
        {
            using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            ok = id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        });
        return ok;
    }
}
