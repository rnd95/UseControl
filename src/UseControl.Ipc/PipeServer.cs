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
        if (_options.RequireAdministrator) WarmUp();
        var first = true;
        using var slots = new SemaphoreSlim(_options.MaxConcurrentClients);
        while (!ct.IsCancellationRequested)
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            NamedPipeServerStream pipe;
            try
            {
                pipe = first ? await CreateFirstAsync(ct).ConfigureAwait(false) : Create(PipeOptions.None);
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

    private NamedPipeServerStream Create(PipeOptions extra)
    {
        var options = PipeOptions.Asynchronous | extra;
        return _options.Security is null
            ? new NamedPipeServerStream(_pipeName, PipeDirection.InOut, _options.MaxConcurrentClients, PipeTransmissionMode.Byte, options)
            : NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, _options.MaxConcurrentClients, PipeTransmissionMode.Byte, options, 0, 0, _options.Security);
    }

    /// <summary>
    /// The first instance must be the first on the system: if another program owns the name, the server
    /// does not share it. Right after an update the previous service process may still be closing its
    /// pipe, so this waits for it a little before giving up.
    /// </summary>
    private async Task<NamedPipeServerStream> CreateFirstAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return Create(PipeOptions.FirstPipeInstance);
            }
            catch (Exception e) when ((e is UnauthorizedAccessException or IOException) && DateTime.UtcNow < deadline)
            {
                _log("The control pipe is still taken; trying again", e);
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
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

    /// <summary>
    /// While impersonating, the thread has the client's identification-level token, which cannot open
    /// files, so nothing may be loaded from disk there. Only the token is taken under impersonation; the
    /// group check runs afterwards with the service's own rights.
    /// </summary>
    private static bool ClientIsAdministrator(NamedPipeServerStream pipe)
    {
        WindowsIdentity? client = null;
        pipe.RunAsClient(() => client = WindowsIdentity.GetCurrent(TokenAccessLevels.Query));
        using (client)
        {
            return client is not null && (client.IsSystem || new WindowsPrincipal(client).IsInRole(WindowsBuiltInRole.Administrator));
        }
    }

    /// <summary>Loads the assemblies the client check needs before any impersonation happens.</summary>
    private static void WarmUp()
    {
        using var me = WindowsIdentity.GetCurrent();
        _ = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        _ = me.Claims.Count();
    }
}
