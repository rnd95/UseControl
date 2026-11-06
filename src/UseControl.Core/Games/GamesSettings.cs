using System.Globalization;

namespace UseControl.Core.Games;

/// <summary>When the games of a profile may be played: only in the time windows of the schedule.</summary>
public sealed class GamesSettings
{
    /// <summary>Time windows when games are allowed. Empty: games are always blocked.</summary>
    public List<GamesWindow> Schedule { get; set; } = [];

    /// <summary>When games time is over, games still running are closed a minute after a warning.</summary>
    public bool CloseGamesWhenOver { get; set; } = true;
}

/// <summary>
/// Days and hours when games are allowed, for example Monday to Friday 16:00–20:00, at most 60 minutes.
/// The limit counts the time a game is actually running, over the whole day.
/// </summary>
public sealed class GamesWindow
{
    public List<DayOfWeek> Days { get; set; } = [];

    /// <summary>Start, "HH:mm".</summary>
    public string From { get; set; } = "16:00";

    /// <summary>End, "HH:mm" (24:00 = midnight at the end of the day); later than <see cref="From"/>.</summary>
    public string To { get; set; } = "20:00";

    /// <summary>At most this many minutes of play a day; 0 = the whole window.</summary>
    public int LimitMinutes { get; set; }

    /// <summary>Minutes since midnight of "HH:mm" (00:00–24:00), or null when the text is not a time.</summary>
    public static int? ParseTime(string? text)
    {
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || parts[1].Length != 2 || h > 24 || m > 59 || (h == 24 && m != 0))
        {
            return null;
        }
        return h * 60 + m;
    }

    public static string FormatTime(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"{minutes / 60:00}:{minutes % 60:00}");

    /// <summary>Throws <see cref="UcException"/> when the window cannot be used.</summary>
    public void EnsureValid()
    {
        var from = ParseTime(From);
        var to = ParseTime(To);
        if (from is null) throw new UcException(ErrorCodes.InvalidTime, From ?? "");
        if (to is null) throw new UcException(ErrorCodes.InvalidTime, To ?? "");
        if (to <= from) throw new UcException(ErrorCodes.WindowEndsBeforeStart, From!, To!);
        if (Days.Count == 0 || Days.Any(d => !Enum.IsDefined(d))) throw new UcException(ErrorCodes.NoDays);
        if (LimitMinutes < 0 || LimitMinutes > 24 * 60) throw new UcException(ErrorCodes.InvalidArgument, LimitMinutes, "limit");
    }
}

/// <summary>What the service remembers about one account's games (part of state.json).</summary>
public sealed class GamesState
{
    /// <summary>Blocked by an administrator until this time, also inside the schedule.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>The local day <see cref="PlayedToday"/> belongs to, "yyyy-MM-dd".</summary>
    public string? Day { get; set; }

    /// <summary>Minutes a game ran on <see cref="Day"/>.</summary>
    public double PlayedToday { get; set; }
}

/// <summary>Why games are allowed or blocked for an account now.</summary>
public enum GamesReason
{
    /// <summary>Blocked by an administrator until the end of the day.</summary>
    Locked,

    /// <summary>Blocked: outside the time windows of the schedule (or the schedule is empty).</summary>
    OutsideSchedule,

    /// <summary>Blocked: the daily limit of the schedule is used up.</summary>
    LimitReached,

    /// <summary>Allowed by the schedule.</summary>
    Schedule,
}

/// <param name="Allowed">Whether games may run now.</param>
/// <param name="Reason">Why.</param>
/// <param name="Until">When allowed: the end of the time window.</param>
/// <param name="MinutesLeft">When allowed and the window has a daily limit: the minutes of play left today.</param>
/// <param name="NextStart">When blocked: when the next time window starts (within a week).</param>
public sealed record GamesDecision(bool Allowed, GamesReason Reason, DateTimeOffset? Until, double? MinutesLeft, DateTimeOffset? NextStart)
{
    /// <summary>How long games stay allowed if a game runs all the time.</summary>
    public TimeSpan? Remaining(DateTimeOffset now)
    {
        if (!Allowed) return TimeSpan.Zero;
        TimeSpan? r = Until is { } u ? u - now : null;
        if (MinutesLeft is { } m && (r is null || TimeSpan.FromMinutes(m) < r)) r = TimeSpan.FromMinutes(m);
        return r is { } x && x < TimeSpan.Zero ? TimeSpan.Zero : r;
    }
}
