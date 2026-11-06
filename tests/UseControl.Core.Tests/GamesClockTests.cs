using UseControl.Core.Games;

namespace UseControl.Core.Tests;

public class GamesClockTests
{
    // A fixed zone without clock changes: local = UTC + 2.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test");

    /// <summary>Friday 6 November 2026 at the given local time.</summary>
    private static DateTimeOffset Fri(int hour, int minute = 0) => new(2026, 11, 6, hour, minute, 0, TimeSpan.FromHours(2));

    private static readonly DateTimeOffset SaturdayTen = new(2026, 11, 7, 10, 0, 0, TimeSpan.FromHours(2));

    private static GamesSettings Schedule(params GamesWindow[] windows) => new() { Schedule = [.. windows] };

    private static GamesWindow Weekdays(string from, string to, int limit = 0) => new()
    {
        Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
        From = from,
        To = to,
        LimitMinutes = limit,
    };

    private static GamesWindow Weekend(string from, string to) => new() { Days = [DayOfWeek.Saturday, DayOfWeek.Sunday], From = from, To = to };

    private static void Play(GamesState st, DateTimeOffset from, int minutes)
    {
        for (var i = 0; i < minutes; i++) GamesClock.RecordPlay(st, from.AddMinutes(i), Zone, 1);
    }

    [Fact]
    public void An_empty_schedule_blocks_games_always()
    {
        var d = GamesClock.Evaluate(new GamesSettings(), new GamesState(), Fri(17), Zone);
        Assert.False(d.Allowed);
        Assert.Equal(GamesReason.OutsideSchedule, d.Reason);
        Assert.Null(d.NextStart);
    }

    [Fact]
    public void Schedule_allows_inside_a_window_only_and_tells_the_next_start()
    {
        var s = Schedule(Weekdays("16:00", "20:00"), Weekend("10:00", "21:00"));
        var st = new GamesState();

        var before = GamesClock.Evaluate(s, st, Fri(15, 59), Zone);
        Assert.Equal(GamesReason.OutsideSchedule, before.Reason);
        Assert.Equal(Fri(16), before.NextStart);

        var inside = GamesClock.Evaluate(s, st, Fri(16), Zone);
        Assert.Equal(GamesReason.Schedule, inside.Reason);
        Assert.Equal(Fri(20), inside.Until);
        Assert.Null(inside.MinutesLeft);

        var after = GamesClock.Evaluate(s, st, Fri(20), Zone);
        Assert.False(after.Allowed);
        Assert.Equal(SaturdayTen, after.NextStart);
    }

    [Fact]
    public void Daily_limit_counts_play_and_starts_again_the_next_day()
    {
        var s = Schedule(Weekdays("16:00", "20:00", limit: 60), Weekend("10:00", "21:00"));
        var st = new GamesState();
        Play(st, Fri(16), 50);

        var d = GamesClock.Evaluate(s, st, Fri(17), Zone);
        Assert.Equal(10, d.MinutesLeft);
        Assert.Equal(TimeSpan.FromMinutes(10), d.Remaining(Fri(17)));

        Play(st, Fri(17), 10);
        var used = GamesClock.Evaluate(s, st, Fri(17, 30), Zone);
        Assert.Equal(GamesReason.LimitReached, used.Reason);
        Assert.Equal(SaturdayTen, used.NextStart);

        // Saturday: a new day, no limit in that window.
        GamesClock.StartDay(st, SaturdayTen, Zone);
        Assert.Equal(0, st.PlayedToday);
        Assert.Equal(GamesReason.Schedule, GamesClock.Evaluate(s, st, SaturdayTen, Zone).Reason);
    }

    [Fact]
    public void Lock_closes_the_schedule_until_tomorrow_and_unlock_takes_it_back()
    {
        var s = Schedule(Weekdays("16:00", "20:00"));
        var st = new GamesState();
        GamesClock.Lock(st, Fri(17), Zone);

        var d = GamesClock.Evaluate(s, st, Fri(17), Zone);
        Assert.Equal(GamesReason.Locked, d.Reason);
        Assert.Equal(Fri(16).AddDays(3), d.NextStart);   // Monday 16:00
        Assert.Equal(Fri(0).AddDays(1), st.LockedUntil);   // midnight

        GamesClock.Unlock(st);
        Assert.True(GamesClock.Evaluate(s, st, Fri(17), Zone).Allowed);

        GamesClock.Lock(st, Fri(17), Zone);
        Assert.True(GamesClock.StartDay(st, Fri(0).AddDays(1), Zone));   // the next day the lock is gone
        Assert.Null(st.LockedUntil);
    }

    [Fact]
    public void Several_open_windows_use_the_most_generous_limit_and_the_latest_end()
    {
        var s = Schedule(Weekdays("16:00", "18:00", limit: 30), Weekdays("17:00", "21:00", limit: 0));
        var d = GamesClock.Evaluate(s, new GamesState(), Fri(17, 30), Zone);
        Assert.Null(d.MinutesLeft);
        Assert.Equal(Fri(21), d.Until);
    }

    [Fact]
    public void Remaining_is_the_shorter_of_the_window_and_the_limit()
    {
        var s = Schedule(Weekdays("16:00", "20:00", limit: 120));
        var d = GamesClock.Evaluate(s, new GamesState(), Fri(19, 30), Zone);
        Assert.Equal(TimeSpan.FromMinutes(30), d.Remaining(Fri(19, 30)));
    }

    [Theory]
    [InlineData("16:00", 960)]
    [InlineData("0:05", 5)]
    [InlineData("24:00", 1440)]
    [InlineData("24:01", null)]
    [InlineData("16", null)]
    [InlineData("16:5", null)]
    [InlineData("ab:cd", null)]
    public void Times_are_parsed_strictly(string text, int? minutes) => Assert.Equal(minutes, GamesWindow.ParseTime(text));
}
