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
    public const string UnlockGames = "unlockGames";
    public const string LockGames = "lockGames";
    public const string Preview = "preview";
    public const string Audit = "audit";
    public const string FindGames = "findGames";
    public const string CheckUpdate = "checkUpdate";
    public const string InstallUpdate = "installUpdate";
}

public sealed record Request(string Command, JsonElement? Payload = null);

/// <summary>The answer to a request. On failure <see cref="ErrorCode"/> and <see cref="ErrorArgs"/> let the client show the message in its language.</summary>
public sealed record Response(bool Ok, JsonElement? Result = null, string? ErrorCode = null, string? Error = null, IReadOnlyList<string>? ErrorArgs = null);

public sealed record PingResult(string Version, int ProcessId);

public sealed record AccountStatus(string User, RestrictionState? State, string? Error);

/// <summary>Games of one restricted account now.</summary>
/// <param name="User">The account.</param>
/// <param name="Preset">Its profile.</param>
/// <param name="Allowed">Whether games may run now.</param>
/// <param name="Reason">Why.</param>
/// <param name="Until">When allowed: the end of the time window.</param>
/// <param name="MinutesLeft">When the window has a daily limit: minutes of play left today.</param>
/// <param name="NextStart">When blocked: when the next time window starts.</param>
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
    string DataDirectory,
    UpdateStatus? Update = null);

/// <summary>Updates of UseControl itself.</summary>
/// <param name="Current">The running version.</param>
/// <param name="Latest">The newest published version found, or null before the first check.</param>
/// <param name="Available">A newer version than the running one exists.</param>
/// <param name="LastCheck">When releases were last looked at.</param>
/// <param name="ErrorCode">Why the last check or installation failed (an error code), or null.</param>
/// <param name="ErrorArgs">The values of that error.</param>
/// <param name="Installing">The installer has been started.</param>
/// <param name="PageUrl">The release page of <paramref name="Latest"/>.</param>
/// <param name="CanInstall">This copy can update itself (it was installed by the installer).</param>
public sealed record UpdateStatus(
    string Current,
    string? Latest,
    bool Available,
    DateTimeOffset? LastCheck,
    string? ErrorCode,
    IReadOnlyList<string> ErrorArgs,
    bool Installing,
    string? PageUrl,
    bool CanInstall);

/// <param name="User">One account, or null for every restricted account.</param>
public sealed record GamesRequest(string? User);

public sealed record SetConfigResult(ApplyResult? Applied, IReadOnlyList<AccountResult> Removed);

public sealed record PreviewResult(IReadOnlyList<AccountPreview> Accounts, IReadOnlyList<AutoPath> AutoPaths);

/// <summary>Serialisation shared by both ends: the same options as config.json.</summary>
public static class IpcJson
{
    public static JsonSerializerOptions Options => ConfigSerializer.Options;

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement? e) => e is { } v && v.ValueKind != JsonValueKind.Null ? v.Deserialize<T>(Options) : default;
}
