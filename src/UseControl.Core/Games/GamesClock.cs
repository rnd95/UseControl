using System.Globalization;

namespace UseControl.Core.Games;

/// <summary>
/// Decides whether an account may play now, and counts play time. Games are allowed only in the time
/// windows of the schedule, up to the daily limit; an administrator can block them for the rest of the
/// day. Pure logic: the service passes the time and the time zone, so every rule is tested without waiting.
/// </summary>
public static class GamesClock
{
    public static GamesDecision Evaluate(GamesSettings settings, GamesState state, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var played = state.Day == DayKey(local) ? state.PlayedToday : 0;
        if (state.LockedUntil is { } lu && lu > now)
            return new(false, GamesReason.Locked, null, null, NextStart(settings, local, zone, skipToday: true));

        var open = OpenWindows(settings, local).ToList();
        if (open.Count == 0) return new(false, GamesReason.OutsideSchedule, null, null, NextStart(settings, local, zone, skipToday: false));

        // Several windows open at once: the most generous one counts (0 = no limit).
        var limit = open.Any(w => w.LimitMinutes == 0) ? 0 : open.Max(w => w.LimitMinutes);
        var end = open.Max(w => GamesWindow.ParseTime(w.To)!.Value);
        if (limit > 0 && played >= limit) return new(false, GamesReason.LimitReached, null, null, NextStart(settings, local, zone, skipToday: true));
        return new(true, GamesReason.Schedule, At(local, end, zone), limit == 0 ? null : limit - played, null);
    }

    /// <summary>Adds minutes during which a game ran.</summary>
    public static void RecordPlay(GamesState state, DateTimeOffset now, TimeZoneInfo zone, double minutes)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (minutes <= 0) return;
        StartDay(state, now, zone);
        state.PlayedToday += minutes;
    }

    /// <summary>Starts a new day's count and forgets a lock that has ended. Returns true when something changed.</summary>
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
        if (state.LockedUntil is { } l && l <= now)
        {
            state.LockedUntil = null;
            changed = true;
        }
        return changed;
    }

    /// <summary>An administrator blocks games now, until the end of the day.</summary>
    public static void Lock(GamesState state, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(state);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        state.LockedUntil = At(local, 24 * 60, zone);
    }

    /// <summary>An administrator takes the block back: the schedule applies again.</summary>
    public static void Unlock(GamesState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.LockedUntil = null;
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
