using System.Text.Json;
using UseControl.Core.Configuration;
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

/// <summary>Everything the panel shows on its first page.</summary>
public sealed record ServiceStatus(
    string Version,
    bool Enabled,
    DateTimeOffset? GamesAllowedUntil,
    DateTimeOffset? LastApplied,
    IReadOnlyList<AccountResult> LastResults,
    IReadOnlyList<AccountStatus> Accounts,
    bool StoreInstallsAdminOnly,
    string DataDirectory);

public sealed record AllowGamesRequest(double Hours);

public sealed record SetConfigResult(ApplyResult? Applied, IReadOnlyList<AccountResult> Removed);

public sealed record PreviewResult(IReadOnlyList<AccountPreview> Accounts, IReadOnlyList<AutoPath> AutoPaths);

/// <summary>Serialisation shared by both ends: the same options as config.json.</summary>
public static class IpcJson
{
    public static JsonSerializerOptions Options => ConfigSerializer.Options;

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement? e) => e is { } v && v.ValueKind != JsonValueKind.Null ? v.Deserialize<T>(Options) : default;
}
