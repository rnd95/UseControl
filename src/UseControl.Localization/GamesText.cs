using System.Globalization;
using UseControl.Core.Games;

namespace UseControl.Localization;

/// <summary>Games time in words, in the current language: the state of an account, durations, days, time windows.</summary>
public static class GamesText
{
    /// <summary>Monday first, as people read a week here.</summary>
    public static IReadOnlyList<DayOfWeek> Week { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>"allowed until 20:00, 45 min of play left today", "blocked, next time tomorrow 16:00"…</summary>
    public static string State(bool allowed, GamesReason reason, DateTimeOffset? until, double? minutesLeft, DateTimeOffset? nextStart, DateTimeOffset now)
    {
        if (allowed)
        {
            return minutesLeft is { } m
                ? Loc.T("Games.State.ScheduleLimit", When(until, now), Duration(m))
                : Loc.T("Games.State.Schedule", When(until, now));
        }
        var next = nextStart is null ? null : When(nextStart, now);
        return reason switch
        {
            GamesReason.Locked => next is null ? Loc.T("Games.State.Locked") : Loc.T("Games.State.LockedNext", next),
            GamesReason.LimitReached => next is null ? Loc.T("Games.State.LimitReached") : Loc.T("Games.State.LimitReachedNext", next),
            _ => next is null ? Loc.T("Games.State.NoWindows") : Loc.T("Games.State.OutsideSchedule", next),
        };
    }

    /// <summary>"45 min", "1 h 30 min", "2 h".</summary>
    public static string Duration(double minutes)
    {
        var total = (int)Math.Ceiling(Math.Max(0, minutes) - 0.001);
        if (total < 60) return Loc.T("Games.Minutes", total);
        var h = total / 60;
        var m = total % 60;
        return m == 0 ? Loc.T("Games.Hours", h) : Loc.T("Games.HoursMinutes", h, m);
    }

    /// <summary>"18:30" today, "tomorrow 16:00", "Sat 10:00" later in the week.</summary>
    public static string When(DateTimeOffset? time, DateTimeOffset now)
    {
        if (time is not { } t) return "—";
        var local = t.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return clock;
        if (local.Date == today.AddDays(1)) return Loc.T("Games.Tomorrow", clock);
        return Loc.T("Games.OnDay", Loc.Culture.DateTimeFormat.GetAbbreviatedDayName(local.DayOfWeek), clock);
    }

    /// <summary>"Mon–Fri", "Sat, Sun", "every day".</summary>
    public static string Days(IEnumerable<DayOfWeek> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var set = days.ToHashSet();
        if (set.Count == 7) return Loc.T("Games.EveryDay");
        var names = Loc.Culture.DateTimeFormat;
        var parts = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            if (!set.Contains(Week[i])) continue;
            var j = i;
            while (j + 1 < 7 && set.Contains(Week[j + 1])) j++;
            parts.Add(j - i >= 2
                ? names.GetAbbreviatedDayName(Week[i]) + "–" + names.GetAbbreviatedDayName(Week[j])
                : string.Join(", ", Week.Skip(i).Take(j - i + 1).Select(names.GetAbbreviatedDayName)));
            i = j;
        }
        return string.Join(", ", parts);
    }

    /// <summary>"Mon–Fri 16:00–20:00, up to 1 h".</summary>
    public static string Window(GamesWindow w)
    {
        ArgumentNullException.ThrowIfNull(w);
        var text = $"{Days(w.Days)}  {w.From}–{w.To}";
        return w.LimitMinutes > 0 ? text + ", " + Loc.T("Games.UpTo", Duration(w.LimitMinutes)) : text;
    }
}
