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

    /// <summary>Restricts "kid" with a schedule: every day 12:30–14:00 (now is Friday 12:00), optionally with a limit.</summary>
    private async Task<UcConfig> KidWithSchedule(int limit = 0, string from = "12:30", string to = "14:00")
    {
        var c = await WithUsers("kid");
        ConfigEditor.AddGamesWindow(c.Presets[0], Enum.GetValues<DayOfWeek>(), from, to, limit);
        await C.SetConfigAsync(c, default);
        return c;
    }

    [Fact]
    public async Task Games_run_only_in_the_schedule_with_a_warning_before_the_end_and_closing_after_it()
    {
        await KidWithSchedule();
        Assert.False(GamesAllowed("kid"));
        await Minutes(30);   // 12:30
        Assert.True(GamesAllowed("kid"));

        Engine.Running.Add(FakeEngine.Game("kid"));
        await Minutes(84);   // 13:54
        Assert.Empty(Engine.Messages);
        await Minutes(1);    // 13:55: five minutes left
        Assert.Equal([("kid", "Notify.GamesEndSoon")], Engine.Messages);
        await Minutes(4);
        Assert.Single(Engine.Messages);   // warned once

        await Minutes(1);    // 14:00: over
        Assert.False(GamesAllowed("kid"));
        Assert.Equal(("kid", "Notify.GamesOverClosing"), Engine.Messages[^1]);
        Assert.Empty(Engine.Closed);
        await Minutes(1);    // a minute later the game is closed
        Assert.Single(Engine.Closed);
        Assert.Equal(91, C.State.Games["kid"].PlayedToday);   // 90 in the window and the minute before closing
    }

    [Fact]
    public async Task Games_are_not_closed_when_the_profile_says_so()
    {
        var c = await KidWithSchedule(from: "12:00", to: "12:10");
        c.Presets[0].Games.CloseGamesWhenOver = false;
        await C.SetConfigAsync(c, default);
        Engine.Running.Add(FakeEngine.Game("kid"));
        await Minutes(12);
        Assert.False(GamesAllowed("kid"));
        Assert.Equal(("kid", "Notify.GamesOver"), Engine.Messages[^1]);
        Assert.Empty(Engine.Closed);
    }

    [Fact]
    public async Task Each_account_has_its_own_schedule_and_lock()
    {
        var c = await WithUsers("kid", "teen");
        ConfigEditor.AddGamesWindow(c.GetPreset("teen"), Enum.GetValues<DayOfWeek>(), "11:00", "23:00", 0);
        await C.SetConfigAsync(c, default);
        Assert.True(GamesAllowed("teen"));
        Assert.False(GamesAllowed("kid"));

        await C.LockGamesAsync("teen", default);
        Assert.False(GamesAllowed("teen"));
        Assert.Equal(GamesReason.Locked, (await C.GetStatusAsync(default)).Games.Single(g => g.User == "teen").Reason);
        await C.UnlockGamesAsync(null, default);
        Assert.True(GamesAllowed("teen"));

        var e = await Assert.ThrowsAsync<UcException>(() => C.LockGamesAsync("nobody", default));
        Assert.Equal(ErrorCodes.NotRestricted, e.Code);
    }

    [Fact]
    public async Task Played_time_and_a_lock_survive_a_restart()
    {
        await KidWithSchedule(limit: 60);
        Engine.Running.Add(FakeEngine.Game("kid"));
        await Minutes(40);   // 30 minutes of them in the window
        await C.LockGamesAsync("kid", default);
        var restarted = _env.Restart();
        Engine.Applies.Clear();
        await restarted.InitializeAsync(default);
        Assert.True(restarted.State.Games["kid"].PlayedToday >= 40);
        Assert.NotNull(restarted.State.Games["kid"].LockedUntil);
        Assert.Empty(Engine.Applies.Single().Games);
        Assert.Equal(["kid"], restarted.Config.Users);
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
    public async Task Lock_lasts_until_the_end_of_the_day()
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
