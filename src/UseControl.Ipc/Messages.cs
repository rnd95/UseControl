using System.Text.Json;
using UseControl.Core.Configuration;
using UseControl.Core.Games;
using UseControl.Core.Rules;
using UseControl.Windows;
using UseControl.Windows.Restrictions;

namespace UseControl.Ipc;

/// <summary>Commands the service understands.</summary>
public static class Commands
{
    public const string Ping = "ping";
    public const string GetStatus = "getStatus";
    public const string GetConfig = "getConfig";
    public const string SetConfig = "setConfig";
    public const string Apply = "apply";
    public const string Suspend = "suspend";
    public const string AllowGames = "allowGames";
    public const string LockGames = "lockGames";
    public const string Preview = "preview";
    public const string Audit = "audit";
    public const string FindGames = "findGames";
}

public sealed record Request(string Command, JsonElement? Payload = null);

/// <summary>The answer to a request. On failure <see cref="ErrorCode"/> and <see cref="ErrorArgs"/> let the client show the message in its language.</summary>
public sealed record Response(bool Ok, JsonElement? Result = null, string? ErrorCode = null, string? Error = null, IReadOnlyList<string>? ErrorArgs = null);

public sealed record PingResult(string Version, int ProcessId);

public sealed record AccountStatus(string User, RestrictionState? State, string? Error);

/// <summary>Games of one restricted account now.</summary>
/// <param name="User">The account.</param>
/// <param name="Preset">Its preset.</param>
/// <param name="Allowed">Whether games may run now.</param>
/// <param name="Reason">Why.</param>
/// <param name="Until">When allowed: when it ends at the latest.</param>
/// <param name="MinutesLeft">When counted by play: minutes of play left.</param>
/// <param name="NextStart">When blocked by the schedule: when the next time window starts.</param>
/// <param name="PlayedToday">Minutes a game ran today.</param>
/// <param name="Playing">A game is running now.</param>
public sealed record AccountGames(string User, string Preset, bool Allowed, GamesReason Reason, DateTimeOffset? Until, double? MinutesLeft, DateTimeOffset? NextStart, double PlayedToday, bool Playing);

/// <summary>Everything the panel shows on its first page.</summary>
public sealed record ServiceStatus(
    string Version,
    bool Enabled,
    IReadOnlyList<AccountGames> Games,
    DateTimeOffset? LastApplied,
    IReadOnlyList<AccountResult> LastResults,
    IReadOnlyList<AccountStatus> Accounts,
    bool StoreInstallsAdminOnly,
    string DataDirectory);

/// <param name="User">One account, or null for every restricted account.</param>
/// <param name="Hours">How long.</param>
/// <param name="CountPlayOnly">True: the time counts only while a game runs (the timer starts when a game starts). False: from now on.</param>
public sealed record AllowGamesRequest(string? User, double Hours, bool CountPlayOnly);

/// <param name="User">One account, or null for every restricted account.</param>
public sealed record LockGamesRequest(string? User);

public sealed record SetConfigResult(ApplyResult? Applied, IReadOnlyList<AccountResult> Removed);

public sealed record PreviewResult(IReadOnlyList<AccountPreview> Accounts, IReadOnlyList<AutoPath> AutoPaths);

/// <summary>Serialisation shared by both ends: the same options as config.json.</summary>
public static class IpcJson
{
    public static JsonSerializerOptions Options => ConfigSerializer.Options;

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement? e) => e is { } v && v.ValueKind != JsonValueKind.Null ? v.Deserialize<T>(Options) : default;
}
