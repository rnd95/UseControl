using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Core.Games;
using UseControl.Windows.Restrictions;

namespace UseControl.Service.Tests;

public sealed class ControllerTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private UcController C => _env.Controller;
    private FakeEngine Engine => _env.Engine;

    public void Dispose() => _env.Dispose();

    /// <summary>Restricts the accounts, each with its own preset.</summary>
    private async Task<UcConfig> WithUsers(params string[] users)
    {
        await C.InitializeAsync(default);
        var c = await C.GetConfigAsync(default);
        foreach (var u in users) ConfigEditor.AddUser(c, u);
        await C.SetConfigAsync(c, default);
        Engine.Applies.Clear();
        return c;
    }

    private async Task Minutes(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _env.Time.Advance(TimeSpan.FromMinutes(1));
            await C.TickAsync(default);
        }
    }

    private bool GamesAllowed(string user) => Engine.Applies[^1].Games.Contains(user);

    [Fact]
    public async Task First_start_creates_the_default_configuration()
    {
        await C.InitializeAsync(default);
        Assert.True(File.Exists(Path.Combine(_env.DataDirectory, "config.json")));
        Assert.Empty(C.Config.Users);
        Assert.Empty(C.Config.Presets);
        Assert.True(C.State.Enabled);
    }

    [Fact]
    public async Task Invalid_configuration_is_refused_and_nothing_changes()
    {
        await WithUsers("kid");
        var bad = await C.GetConfigAsync(default);
        bad.Presets[0].BlockedPaths.Add(new PathEntry { Path = @"C:\Windows" });
        var e = await Assert.ThrowsAsync<UcException>(() => C.SetConfigAsync(bad, default));
        Assert.Equal(ErrorCodes.PathProtected, e.Code);
        Assert.Empty(Engine.Applies);
        Assert.DoesNotContain(@"C:\Windows", File.ReadAllText(Path.Combine(_env.DataDirectory, "config.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accounts_removed_from_the_list_lose_their_restrictions()
    {
        var c = await WithUsers("kid", "teen");
        ConfigEditor.RemoveUser(c, "teen");
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
    public async Task Games_allowed_from_now_end_by_themselves()
    {
        await WithUsers("kid");
        var status = await C.AllowGamesAsync("kid", 1.5, countPlayOnly: false, default);
        Assert.Equal(_env.Time.Now.AddHours(1.5), status.Games.Single().Until);
        Assert.True(GamesAllowed("kid"));

        await Minutes(89);
        Assert.Single(Engine.Applies);   // still games time, nothing re-applied

        await Minutes(1);
        Assert.False(GamesAllowed("kid"));
        Assert.Null(C.State.Games["kid"].AllowedUntil);
    }

    [Fact]
    public async Task Play_time_runs_only_while_a_game_runs_then_warns_blocks_and_closes()
    {
        await WithUsers("kid");
        await C.AllowGamesAsync("kid", 1, countPlayOnly: true, default);
        Assert.True(GamesAllowed("kid"));

        await Minutes(180);   // nobody plays: the hour is still there
        Assert.True(GamesAllowed("kid"));
        Assert.Equal(60, C.State.Games["kid"].PlayMinutesLeft);

        Engine.Running.Add(FakeEngine.Game("kid"));
        await Minutes(54);
        Assert.Empty(Engine.Messages);
        await Minutes(1);   // 5 minutes left
        Assert.Equal([("kid", "Notify.GamesEndSoon")], Engine.Messages);
        await Minutes(4);
        Assert.Single(Engine.Messages);   // warned once

        await Minutes(1);   // the hour is used up
        Assert.False(GamesAllowed("kid"));
        Assert.Equal(("kid", "Notify.GamesOverClosing"), Engine.Messages[^1]);
        Assert.Empty(Engine.Closed);

        await Minutes(1);   // a minute later the game is closed
        Assert.Single(Engine.Closed);
        Assert.Equal(61, C.State.Games["kid"].PlayedToday);   // the minute before closing was played too
    }

    [Fact]
    public async Task Games_are_not_closed_when_the_preset_says_so()
    {
        var c = await WithUsers("kid");
        c.Presets[0].Games.CloseGamesWhenOver = false;
        await C.SetConfigAsync(c, default);
        await C.AllowGamesAsync("kid", 0.25, countPlayOnly: true, default);
        Engine.Running.Add(FakeEngine.Game("kid"));
        await Minutes(20);
        Assert.False(GamesAllowed("kid"));
        Assert.Equal(("kid", "Notify.GamesOver"), Engine.Messages[^1]);
        Assert.Empty(Engine.Closed);
    }

    [Fact]
    public async Task Schedule_allows_games_in_its_window_and_counts_the_daily_limit()
    {
        var c = await WithUsers("kid");
        // Friday, now 12:00 (UTC is the local time here): allowed 12:30–14:00, at most 30 minutes.
        ConfigEditor.AddGamesWindow(c.Presets[0], [DayOfWeek.Friday], "12:30", "14:00", 30);
        await C.SetConfigAsync(c, default);
        Assert.False(GamesAllowed("kid"));

        await Minutes(30);
        Assert.True(GamesAllowed("kid"));

        Engine.Running.Add(FakeEngine.Game("kid"));
        await Minutes(30);
        Assert.False(GamesAllowed("kid"));   // limit used up before the end of the window
        var g = (await C.GetStatusAsync(default)).Games.Single();
        Assert.Equal(GamesReason.LimitReached, g.Reason);
        Assert.Equal(30, g.PlayedToday);
    }

    [Fact]
    public async Task Each_account_has_its_own_games_time()
    {
        await WithUsers("kid", "teen");
        await C.AllowGamesAsync("teen", 2, countPlayOnly: false, default);
        Assert.True(GamesAllowed("teen"));
        Assert.False(GamesAllowed("kid"));

        await C.AllowGamesAsync(null, 1, countPlayOnly: false, default);   // everyone
        Assert.True(GamesAllowed("kid"));

        await C.LockGamesAsync("teen", default);
        Assert.False(GamesAllowed("teen"));
        Assert.True(GamesAllowed("kid"));

        var e = await Assert.ThrowsAsync<UcException>(() => C.AllowGamesAsync("nobody", 1, true, default));
        Assert.Equal(ErrorCodes.NotRestricted, e.Code);
    }

    [Fact]
    public async Task Lock_inside_the_schedule_lasts_until_the_end_of_the_day()
    {
        var c = await WithUsers("kid");
        ConfigEditor.AddGamesWindow(c.Presets[0], [DayOfWeek.Friday, DayOfWeek.Saturday], "10:00", "22:00", 0);
        await C.SetConfigAsync(c, default);
        Assert.True(GamesAllowed("kid"));

        Engine.Running.Add(FakeEngine.Game("kid"));
        await Minutes(1);
        await C.LockGamesAsync("kid", default);
        Assert.False(GamesAllowed("kid"));
        Assert.Equal(("kid", "Notify.GamesOverClosing"), Engine.Messages[^1]);
        await Minutes(1);
        Assert.Single(Engine.Closed);

        _env.Time.Now = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);   // Saturday
        await C.TickAsync(default);
        Assert.True(GamesAllowed("kid"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    [InlineData(25)]
    [InlineData(double.NaN)]
    public async Task Games_hours_out_of_range_are_refused(double hours)
    {
        await WithUsers("kid");
        var e = await Assert.ThrowsAsync<UcException>(() => C.AllowGamesAsync(null, hours, true, default));
        Assert.Equal(ErrorCodes.HoursOutOfRange, e.Code);
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
        await C.AllowGamesAsync("kid", 1, countPlayOnly: false, default);
        await C.SuspendAsync(default);
        await C.ApplyAsync(default);
        await C.AllowGamesAsync("kid", 1, countPlayOnly: false, default);

        _env.Time.Advance(TimeSpan.FromHours(2));
        var restarted = _env.Restart();
        Engine.Applies.Clear();
        await restarted.InitializeAsync(default);
        Assert.Null(restarted.State.Games["kid"].AllowedUntil);
        Assert.Empty(Engine.Applies.Single().Games);
        Assert.Equal(["kid"], restarted.Config.Users);
    }

    [Fact]
    public async Task Given_play_time_survives_a_restart()
    {
        await WithUsers("kid");
        await C.AllowGamesAsync("kid", 2, countPlayOnly: true, default);
        var restarted = _env.Restart();
        await restarted.InitializeAsync(default);
        Assert.Equal(120, restarted.State.Games["kid"].PlayMinutesLeft);
        Assert.True(GamesAllowed("kid"));
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
        c.Presets[0].Accounts.Add("someone");
        Assert.Equal(["kid"], C.Config.Users);
    }
}
