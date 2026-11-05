using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UseControl.Ipc;

namespace UseControl.Service;

/// <summary>Starts the controller, the control pipe and the one-minute timer.</summary>
public sealed partial class ServiceWorker(UcController controller, ILogger<ServiceWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await controller.InitializeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Core.UcException or IOException or UnauthorizedAccessException)
        {
            // Keep running: the administrator must still be able to connect and fix the configuration.
            LogStartFailed(log, e);
        }

        var handler = new CommandHandler(controller);
        var server = new PipeServer(PipeProtocol.PipeName, handler.HandleAsync,
            new PipeServerOptions { Security = PipeServer.AdministratorsOnly(), RequireAdministrator = true },
            (message, e) => LogPipe(log, message, e));

        await Task.WhenAll(server.RunAsync(stoppingToken), TimerAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task TimerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await controller.TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is Core.UcException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    LogTickFailed(log, e);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(10, LogLevel.Error, "Could not start with the saved configuration")]
    private static partial void LogStartFailed(ILogger log, Exception e);

    [LoggerMessage(11, LogLevel.Warning, "Control pipe: {Message}")]
    private static partial void LogPipe(ILogger log, string message, Exception? e);

    [LoggerMessage(12, LogLevel.Error, "Regular check failed")]
    private static partial void LogTickFailed(ILogger log, Exception e);
}
