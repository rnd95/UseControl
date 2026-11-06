using UseControl.Core.Games;

namespace UseControl.Core.Tests;

public class GamesClockTests
{
    // A fixed zone without clock changes: local = UTC + 2.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test");

    /// <summary>Friday 6 November 2026 at the given local time.</summary>
    private static DateTimeOffset Fri(int hour, int minute = 0) => new(2026, 11, 6, hour, minute, 0, TimeSpan.FromHours(2));

    private static GamesSettings Schedule(params GamesWindow[] windows) => new() { Mode = GamesMode.Schedule, Schedule = [.. windows] };

    private static GamesWindow Weekdays(string from, string to, int limit = 0) => new()
    {
        Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
        From = from,
        To = to,
        LimitMinutes = limit,
    };

    private static GamesWindow Weekend(string from, string to) => new() { Days = [DayOfWeek.Saturday, DayOfWeek.Sunday], From = from, To = to };

    [Fact]
    public void Manual_mode_blocks_until_an_administrator_allows()
    {
        var s = new GamesSettings();
        var st = new GamesState();
        Assert.Equal(GamesReason.Blocked, GamesClock.Evaluate(s, st, Fri(17), Zone).Reason);

        GamesClock.Allow(st, Fri(17), 1.5, countPlayOnly: false);
        var d = GamesClock.Evaluate(s, st, Fri(17), Zone);
        Assert.True(d.Allowed);
        Assert.Equal(GamesReason.Allowed, d.Reason);
        Assert.Equal(Fri(18, 30), d.Until);
        Assert.False(GamesClock.Evaluate(s, st, Fri(18, 30), Zone).Allowed);
    }

    [Fact]
    public void Play_time_counts_only_while_a_game_runs()
    {
        var s = new GamesSettings();
        var st = new GamesState();
        GamesClock.Allow(st, Fri(10), 1, countPlayOnly: true);

        // Hours later, nothing played: still a full hour.
        var d = GamesClock.Evaluate(s, st, Fri(15), Zone);
        Assert.Equal(GamesReason.PlayTime, d.Reason);
        Assert.Equal(60, d.MinutesLeft);

        for (var i = 0; i < 45; i++) GamesClock.RecordPlay(s, st, Fri(15).AddMinutes(i), Zone, 1);
        Assert.Equal(15, GamesClock.Evaluate(s, st, Fri(16), Zone).MinutesLeft);
        Assert.Equal(45, st.PlayedToday);

        for (var i = 0; i < 15; i++) GamesClock.RecordPlay(s, st, Fri(17).AddMinutes(i), Zone, 1);
        Assert.False(GamesClock.Evaluate(s, st, Fri(17, 20), Zone).Allowed);
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
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.FromHours(2)), after.NextStart);   // Saturday 10:00
    }

    [Fact]
    public void Daily_limit_counts_play_and_starts_again_the_next_day()
    {
        var s = Schedule(Weekdays("16:00", "20:00", limit: 60), Weekend("10:00", "21:00"));
        var st = new GamesState();
        for (var i = 0; i < 50; i++) GamesClock.RecordPlay(s, st, Fri(16).AddMinutes(i), Zone, 1);

        var d = GamesClock.Evaluate(s, st, Fri(17), Zone);
        Assert.Equal(10, d.MinutesLeft);
        Assert.Equal(TimeSpan.FromMinutes(10), d.Remaining(Fri(17)));

        for (var i = 0; i < 10; i++) GamesClock.RecordPlay(s, st, Fri(17).AddMinutes(i), Zone, 1);
        var used = GamesClock.Evaluate(s, st, Fri(17, 30), Zone);
        Assert.Equal(GamesReason.LimitReached, used.Reason);
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.FromHours(2)), used.NextStart);

        // Saturday: a new day, no limit in that window.
        var sat = Fri(11).AddDays(1);
        GamesClock.StartDay(st, sat, Zone);
        Assert.Equal(0, st.PlayedToday);
        Assert.Equal(GamesReason.Schedule, GamesClock.Evaluate(s, st, sat, Zone).Reason);
    }

    [Fact]
    public void Play_inside_the_schedule_does_not_use_up_given_minutes()
    {
        var s = Schedule(Weekdays("16:00", "20:00", limit: 30));
        var st = new GamesState();
        GamesClock.Allow(st, Fri(12), 1, countPlayOnly: true);
        for (var i = 0; i < 30; i++) GamesClock.RecordPlay(s, st, Fri(16).AddMinutes(i), Zone, 1);
        Assert.Equal(60, st.PlayMinutesLeft);

        // The limit is used up: the given hour takes over.
        var d = GamesClock.Evaluate(s, st, Fri(16, 30), Zone);
        Assert.Equal(GamesReason.PlayTime, d.Reason);
        GamesClock.RecordPlay(s, st, Fri(16, 30), Zone, 5);
        Assert.Equal(55, st.PlayMinutesLeft);
    }

    [Fact]
    public void Lock_takes_back_given_time_and_closes_the_schedule_until_tomorrow()
    {
        var s = Schedule(Weekdays("16:00", "20:00"));
        var st = new GamesState();
        GamesClock.Allow(st, Fri(16), 2, countPlayOnly: true);
        GamesClock.Lock(st, Fri(17), Zone);

        var d = GamesClock.Evaluate(s, st, Fri(17), Zone);
        Assert.Equal(GamesReason.Locked, d.Reason);
        Assert.Equal(0, st.PlayMinutesLeft);
        Assert.Equal(Fri(16).AddDays(3), d.NextStart);   // Monday 16:00

        // Allowing again lifts the lock.
        GamesClock.Allow(st, Fri(17), 0.5, countPlayOnly: false);
        Assert.True(GamesClock.Evaluate(s, st, Fri(17), Zone).Allowed);
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
