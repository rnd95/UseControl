using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Windows.Restrictions;

namespace UseControl.Service.Tests;

public sealed class ControllerTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private UcController C => _env.Controller;
    private FakeEngine Engine => _env.Engine;

    public void Dispose() => _env.Dispose();

    private async Task<UcConfig> WithUsers(params string[] users)
    {
        await C.InitializeAsync(default);
        var c = await C.GetConfigAsync(default);
        c.Users = [.. users];
        await C.SetConfigAsync(c, default);
        Engine.Applies.Clear();
        return c;
    }

    [Fact]
    public async Task First_start_creates_the_default_configuration()
    {
        await C.InitializeAsync(default);
        Assert.True(File.Exists(Path.Combine(_env.DataDirectory, "config.json")));
        Assert.Empty(C.Config.Users);
        Assert.NotEmpty(C.Config.BlockedNames);
        Assert.True(C.State.Enabled);
    }

    [Fact]
    public async Task Invalid_configuration_is_refused_and_nothing_changes()
    {
        await WithUsers("kid");
        var bad = await C.GetConfigAsync(default);
        bad.BlockedPaths.Add(new PathEntry { Path = @"C:\Windows" });
        var e = await Assert.ThrowsAsync<UcException>(() => C.SetConfigAsync(bad, default));
        Assert.Equal(ErrorCodes.PathProtected, e.Code);
        Assert.Empty(Engine.Applies);
        Assert.DoesNotContain(@"C:\Windows", File.ReadAllText(Path.Combine(_env.DataDirectory, "config.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accounts_removed_from_the_list_lose_their_restrictions()
    {
        var c = await WithUsers("kid", "teen");
        c.Users = ["kid"];
        var r = await C.SetConfigAsync(c, default);
        Assert.Equal(["teen"], Engine.Removed);
        Assert.Single(r.Removed);
        Assert.Single(Engine.Applies);
    }

    [Fact]
    public async Task Suspend_keeps_restrictions_off_until_apply()
    {
        var c = await WithUsers("kid");
        await C.SuspendAsync(default);
        Assert.Equal(1, Engine.RemoveAllCalls);
        Assert.False(C.State.Enabled);

        await C.SetConfigAsync(c, default);
        _env.Time.Advance(TimeSpan.FromHours(7));
        await C.TickAsync(default);
        Assert.Empty(Engine.Applies);

        await C.ApplyAsync(default);
        Assert.True(C.State.Enabled);
        Assert.Single(Engine.Applies);
    }

    [Fact]
    public async Task Games_time_applies_with_games_allowed_and_ends_by_itself()
    {
        await WithUsers("kid");
        var status = await C.AllowGamesAsync(1.5, default);
        Assert.Equal(_env.Time.Now.AddHours(1.5), status.GamesAllowedUntil);
        Assert.True(Engine.Applies[^1].Games);

        _env.Time.Advance(TimeSpan.FromMinutes(89));
        await C.TickAsync(default);
        Assert.Single(Engine.Applies);   // still games time, nothing re-applied

        _env.Time.Advance(TimeSpan.FromMinutes(2));
        await C.TickAsync(default);
        Assert.False(Engine.Applies[^1].Games);
        Assert.Null(C.State.GamesAllowedUntil);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    [InlineData(25)]
    [InlineData(double.NaN)]
    public async Task Games_hours_out_of_range_are_refused(double hours)
    {
        await WithUsers("kid");
        var e = await Assert.ThrowsAsync<UcException>(() => C.AllowGamesAsync(hours, default));
        Assert.Equal(ErrorCodes.HoursOutOfRange, e.Code);
    }

    [Fact]
    public async Task Lock_games_ends_games_time_now()
    {
        await WithUsers("kid");
        await C.AllowGamesAsync(2, default);
        var s = await C.LockGamesAsync(default);
        Assert.Null(s.GamesAllowedUntil);
        Assert.False(Engine.Applies[^1].Games);
    }

    [Fact]
    public async Task Changed_restrictions_are_put_back()
    {
        await WithUsers("kid");
        await C.ApplyAsync(default);
        Engine.Applies.Clear();

        _env.Time.Advance(TimeSpan.FromMinutes(6));
        await C.TickAsync(default);
        Assert.Empty(Engine.Applies);   // registry matches: nothing to do

        Engine.StateOverride = _ => new RestrictionState(true, 10, 3, 2, 5, new Dictionary<string, int>(), KeysLocked: false);
        _env.Time.Advance(TimeSpan.FromMinutes(6));
        await C.TickAsync(default);
        Assert.Single(Engine.Applies);

        Engine.StateOverride = _ => new RestrictionState(false, 0, 0, 0, 0, new Dictionary<string, int>(), false);
        _env.Time.Advance(TimeSpan.FromMinutes(6));
        await C.TickAsync(default);
        Assert.Equal(2, Engine.Applies.Count);
    }

    [Fact]
    public async Task Restrictions_are_re_applied_regularly()
    {
        await WithUsers("kid");
        await C.ApplyAsync(default);
        Engine.Applies.Clear();
        _env.Time.Advance(TimeSpan.FromHours(6));
        await C.TickAsync(default);
        Assert.Single(Engine.Applies);
    }

    [Fact]
    public async Task State_survives_a_restart_and_expired_games_time_is_dropped()
    {
        await WithUsers("kid");
        await C.AllowGamesAsync(1, default);
        await C.SuspendAsync(default);
        await C.ApplyAsync(default);
        await C.AllowGamesAsync(1, default);

        _env.Time.Advance(TimeSpan.FromHours(2));
        var restarted = _env.Restart();
        Engine.Applies.Clear();
        await restarted.InitializeAsync(default);
        Assert.Null(restarted.State.GamesAllowedUntil);
        Assert.False(Engine.Applies.Single().Games);
        Assert.Equal(["kid"], restarted.Config.Users);
    }

    [Fact]
    public async Task Damaged_state_file_falls_back_to_protection_on()
    {
        await WithUsers("kid");
        await C.SuspendAsync(default);
        File.WriteAllText(Path.Combine(_env.DataDirectory, "state.json"), "{ broken");
        var restarted = _env.Restart();
        await restarted.InitializeAsync(default);
        Assert.True(restarted.State.Enabled);
    }

    [Fact]
    public async Task GetConfig_returns_a_copy()
    {
        await WithUsers("kid");
        var c = await C.GetConfigAsync(default);
        c.Users.Add("someone");
        Assert.Equal(["kid"], C.Config.Users);
    }
}
