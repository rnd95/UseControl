using System.Globalization;

namespace UseControl.Core.Games;

/// <summary>
/// Decides whether an account may play now, and counts play time. Pure logic: the service passes the
/// time and the time zone, so every rule is tested without waiting.
/// <para>Order: an administrator's "allow until" wins; then the schedule (unless locked for the day);
/// then minutes of play an administrator gave. Play is counted against the schedule's daily limit
/// first and against given minutes only outside the schedule.</para>
/// </summary>
public static class GamesClock
{
    public static GamesDecision Evaluate(GamesSettings settings, GamesState state, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(zone);

        if (state.AllowedUntil is { } until && until > now) return new(true, GamesReason.Allowed, until, null, null);

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var played = state.Day == DayKey(local) ? state.PlayedToday : 0;
        var locked = state.LockedUntil is { } lu && lu > now;
        var schedule = settings.Mode == GamesMode.Schedule;

        var limitReached = false;
        if (schedule && !locked)
        {
            var open = OpenWindows(settings, local).ToList();
            if (open.Count > 0)
            {
                // Several windows open at once: the most generous one counts (0 = no limit).
                var limit = open.Any(w => w.LimitMinutes == 0) ? 0 : open.Max(w => w.LimitMinutes);
                var end = open.Max(w => GamesWindow.ParseTime(w.To)!.Value);
                if (limit == 0 || played < limit)
                    return new(true, GamesReason.Schedule, At(local, end, zone), limit == 0 ? null : limit - played, null);
                limitReached = true;
            }
        }

        if (state.PlayMinutesLeft > 0) return new(true, GamesReason.PlayTime, null, state.PlayMinutesLeft, null);

        if (!schedule) return new(false, GamesReason.Blocked, null, null, null);
        var reason = locked ? GamesReason.Locked : limitReached ? GamesReason.LimitReached : GamesReason.OutsideSchedule;
        return new(false, reason, null, null, NextStart(settings, local, zone, skipToday: locked || limitReached));
    }

    /// <summary>
    /// Adds minutes during which a game ran. Call it before the decision changes (the minutes belong to
    /// the decision that allowed the game).
    /// </summary>
    public static void RecordPlay(GamesSettings settings, GamesState state, DateTimeOffset now, TimeZoneInfo zone, double minutes)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (minutes <= 0) return;
        var d = Evaluate(settings, state, now, zone);
        StartDay(state, now, zone);
        state.PlayedToday += minutes;
        if (d.Reason == GamesReason.PlayTime) state.PlayMinutesLeft = Math.Max(0, state.PlayMinutesLeft - minutes);
    }

    /// <summary>Starts a new day's count and forgets what has ended. Returns true when something changed.</summary>
    public static bool StartDay(GamesState state, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(state);
        var changed = false;
        var day = DayKey(TimeZoneInfo.ConvertTime(now, zone));
        if (state.Day != day)
        {
            state.Day = day;
            state.PlayedToday = 0;
            changed = true;
        }
        if (state.AllowedUntil is { } u && u <= now)
        {
            state.AllowedUntil = null;
            changed = true;
        }
        if (state.LockedUntil is { } l && l <= now)
        {
            state.LockedUntil = null;
            changed = true;
        }
        return changed;
    }

    /// <summary>An administrator allows games: for some time from now, or for some minutes of play.</summary>
    public static void Allow(GamesState state, DateTimeOffset now, double hours, bool countPlayOnly)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.LockedUntil = null;
        if (countPlayOnly)
        {
            state.PlayMinutesLeft = hours * 60;
            state.AllowedUntil = null;
        }
        else
        {
            state.AllowedUntil = now.AddHours(hours);
            state.PlayMinutesLeft = 0;
        }
    }

    /// <summary>An administrator blocks games now: given time is taken back and the schedule is off until the end of the day.</summary>
    public static void Lock(GamesState state, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.AllowedUntil = null;
        state.PlayMinutesLeft = 0;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        state.LockedUntil = At(local, 24 * 60, zone);
    }

    private static IEnumerable<GamesWindow> OpenWindows(GamesSettings s, DateTimeOffset local)
    {
        var minute = (int)local.TimeOfDay.TotalMinutes;
        foreach (var w in s.Schedule)
        {
            if (!w.Days.Contains(local.DayOfWeek)) continue;
            if (GamesWindow.ParseTime(w.From) is not { } from || GamesWindow.ParseTime(w.To) is not { } to) continue;
            if (minute >= from && minute < to) yield return w;
        }
    }

    private static DateTimeOffset? NextStart(GamesSettings s, DateTimeOffset local, TimeZoneInfo zone, bool skipToday)
    {
        var minute = (int)local.TimeOfDay.TotalMinutes;
        for (var dayOffset = skipToday ? 1 : 0; dayOffset <= 7; dayOffset++)
        {
            var day = local.Date.AddDays(dayOffset);
            var starts = s.Schedule
                .Where(w => w.Days.Contains(day.DayOfWeek))
                .Select(w => GamesWindow.ParseTime(w.From))
                .OfType<int>()
                .Where(f => dayOffset > 0 || f > minute)
                .Order()
                .ToList();
            if (starts.Count > 0) return At(day, starts[0], zone);
        }
        return null;
    }

    /// <summary>A local day and minute as a point in time.</summary>
    private static DateTimeOffset At(DateTimeOffset localDay, int minute, TimeZoneInfo zone) => At(localDay.Date, minute, zone);

    private static DateTimeOffset At(DateTime day, int minute, TimeZoneInfo zone)
    {
        var t = DateTime.SpecifyKind(day.Date.AddMinutes(minute), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(t)) t = t.AddHours(1);   // a clock change skipped this time
        return new DateTimeOffset(t, zone.GetUtcOffset(t));
    }

    internal static string DayKey(DateTimeOffset local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
