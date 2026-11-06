using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Ipc;

namespace UseControl.Service.Tests;

/// <summary>A real named pipe (with its own name) between <see cref="UcClient"/> and <see cref="PipeServer"/>.</summary>
public sealed class PipeTests : IAsyncLifetime, IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly string _pipe = "UseControl.Test." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;
    private UcClient _client = null!;

    public async Task InitializeAsync()
    {
        await _env.Controller.InitializeAsync(default);
        var handler = new CommandHandler(_env.Controller);
        // Tests may run without elevation, so the administrator check is off here (the service turns it on).
        var server = new PipeServer(_pipe, handler.HandleAsync, new PipeServerOptions { RequireAdministrator = false }, (_, _) => { });
        _server = server.RunAsync(_stop.Token);
        _client = new UcClient(_pipe, requireSessionZero: false);
    }

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_server is not null) await _server;
    }

    public void Dispose()
    {
        _stop.Dispose();
        _env.Dispose();
    }

    [Fact]
    public async Task Ping_answers_with_version_and_process()
    {
        var p = await _client.PingAsync();
        Assert.Equal(Environment.ProcessId, p.ProcessId);
        Assert.False(string.IsNullOrEmpty(p.Version));
    }

    [Fact]
    public async Task Configuration_round_trips_through_the_service()
    {
        var c = await _client.GetConfigAsync();
        var p = ConfigEditor.AddUser(c, "kid");
        p.AllowedPaths.Add(new AllowedPathEntry { Path = @"C:\Tools" });
        ConfigEditor.AddGamesWindow(p, [DayOfWeek.Saturday], "10:00", "12:00", 60);
        var r = await _client.SetConfigAsync(c);
        Assert.NotNull(r.Applied);
        var again = await _client.GetConfigAsync();
        Assert.Equal(["kid"], again.Users);
        Assert.Equal(@"C:\Tools", again.Presets.Single().AllowedPaths.Single().Path);
        Assert.Equal("10:00", again.Presets.Single().Games.Schedule.Single().From);

        var s = await _client.LockGamesAsync("kid");
        Assert.Equal(Core.Games.GamesReason.Locked, s.Games.Single().Reason);
    }

    [Fact]
    public async Task Errors_arrive_with_their_code()
    {
        var e = await Assert.ThrowsAsync<UcException>(() => _client.LockGamesAsync("nobody"));
        Assert.Equal(ErrorCodes.NotRestricted, e.Code);
        Assert.Contains("nobody", e.Message, StringComparison.Ordinal);

        e = await Assert.ThrowsAsync<UcException>(() => _client.SendAsync<object>("noSuchCommand", null));
        Assert.Equal(ErrorCodes.UnknownCommand, e.Code);
    }

    [Fact]
    public async Task Several_clients_are_served()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _client.GetStatusAsync()));
        Assert.All(results, s => Assert.True(s.Enabled));
    }

    [Fact]
    public async Task Missing_service_is_reported_quickly()
    {
        var nobody = new UcClient("UseControl.Test.Nobody." + Guid.NewGuid().ToString("N"), requireSessionZero: false) { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        var e = await Assert.ThrowsAsync<UcException>(() => nobody.PingAsync());
        Assert.Equal(ErrorCodes.ServiceNotRunning, e.Code);
    }

    [Fact]
    public async Task A_server_outside_session_zero_is_not_trusted()
    {
        // This test process is not a service, so a client that insists on session 0 must refuse it.
        var strict = new UcClient(_pipe, requireSessionZero: true);
        var e = await Assert.ThrowsAsync<UcException>(() => strict.PingAsync());
        Assert.Equal(ErrorCodes.ServiceNotTrusted, e.Code);
    }

    [Fact]
    public async Task Administrator_check_answers_instead_of_dropping_the_connection()
    {
        // The same check the service runs. Elevated test runs get an answer; others get "administrator
        // required" — never a broken connection (that was a real bug: the check loaded files while
        // impersonating the client).
        var name = "UseControl.Test.Admin." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var handler = new CommandHandler(_env.Controller);
        var server = new PipeServer(name, handler.HandleAsync, new PipeServerOptions { RequireAdministrator = true }, (_, _) => { });
        var run = server.RunAsync(stop.Token);
        try
        {
            var client = new UcClient(name, requireSessionZero: false);
            var elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            if (elevated) Assert.NotNull(await client.PingAsync());
            else Assert.Equal(ErrorCodes.AdministratorRequired, (await Assert.ThrowsAsync<UcException>(() => client.PingAsync())).Code);
        }
        finally
        {
            await stop.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task Garbage_on_the_pipe_does_not_stop_the_server()
    {
        using (var raw = new System.IO.Pipes.NamedPipeClientStream(".", _pipe, System.IO.Pipes.PipeDirection.InOut))
        {
            await raw.ConnectAsync(2000);
            try
            {
                await raw.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3 });
            }
            catch (IOException)
            {
                // The server may hang up as soon as it sees the bad length: that is the point.
            }
        }
        Assert.NotNull(await _client.PingAsync());
    }
}
