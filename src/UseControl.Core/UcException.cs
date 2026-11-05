using System.Globalization;

namespace UseControl.Core;

/// <summary>
/// An error the user can act on. <see cref="Code"/> identifies the message, so the interface can show
/// it in the user's language; <see cref="Exception.Message"/> is the English text.
/// </summary>
public sealed class UcException : Exception
{
    public UcException(string code, params object[] args)
        : base(ErrorMessages.Format(code, args))
    {
        Code = code;
        Args = args;
    }

    // No UcException(string message) constructor on purpose: with it, new UcException(code) without
    // arguments would silently become a plain message and lose its code.
    public UcException() : this(ErrorCodes.Unknown, []) { }

    public UcException(string message, Exception innerException) : base(message, innerException) { Code = ErrorCodes.Unknown; Args = []; }

    public string Code { get; } = ErrorCodes.Unknown;

    public IReadOnlyList<object> Args { get; } = [];
}

/// <summary>Message codes of <see cref="UcException"/>. The localized texts use the same names.</summary>
public static class ErrorCodes
{
    public const string Unknown = nameof(Unknown);
    public const string ConfigNotFound = nameof(ConfigNotFound);
    public const string ConfigInvalid = nameof(ConfigInvalid);
    public const string ConfigTooNew = nameof(ConfigTooNew);
    public const string PathNotAbsolute = nameof(PathNotAbsolute);
    public const string PathInvalidCharacters = nameof(PathInvalidCharacters);
    public const string PathProtected = nameof(PathProtected);
    public const string PathContainsProtected = nameof(PathContainsProtected);
    public const string PathInsideWindows = nameof(PathInsideWindows);
    public const string WholeDriveNotAllowed = nameof(WholeDriveNotAllowed);
    public const string AlreadyInList = nameof(AlreadyInList);
    public const string NotInList = nameof(NotInList);
    public const string InvalidProgramName = nameof(InvalidProgramName);
    public const string InvalidUserName = nameof(InvalidUserName);
    public const string InvalidExtensionId = nameof(InvalidExtensionId);
    public const string InvalidFileHash = nameof(InvalidFileHash);
    public const string NoRights = nameof(NoRights);
    public const string InvalidPolicyValue = nameof(InvalidPolicyValue);
    public const string AccountNotFound = nameof(AccountNotFound);
    public const string AccountIsAdministrator = nameof(AccountIsAdministrator);
    public const string ProfileMissing = nameof(ProfileMissing);
    public const string HiveLoadFailed = nameof(HiveLoadFailed);
    public const string AdministratorRequired = nameof(AdministratorRequired);
    public const string ServiceNotRunning = nameof(ServiceNotRunning);
    public const string ServiceNotTrusted = nameof(ServiceNotTrusted);
    public const string ServiceFailed = nameof(ServiceFailed);
    public const string UnknownCommand = nameof(UnknownCommand);
    public const string InvalidRequest = nameof(InvalidRequest);
    public const string HoursOutOfRange = nameof(HoursOutOfRange);
}

/// <summary>English texts of the error codes (the fallback when no translation exists).</summary>
public static class ErrorMessages
{
    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        [ErrorCodes.Unknown] = "Unknown error.",
        [ErrorCodes.ConfigNotFound] = "Configuration not found: {0}",
        [ErrorCodes.ConfigInvalid] = "Configuration {0} is not valid: {1}",
        [ErrorCodes.ConfigTooNew] = "Configuration {0} was written by a newer UseControl (schema {1}); this version understands up to {2}.",
        [ErrorCodes.PathNotAbsolute] = "Not an absolute local path: '{0}'.",
        [ErrorCodes.PathInvalidCharacters] = "The path contains characters that are not allowed: '{0}'.",
        [ErrorCodes.PathProtected] = "{0} is needed by Windows or by every program and cannot be used here.",
        [ErrorCodes.PathContainsProtected] = "{0} contains {1}, which Windows needs.",
        [ErrorCodes.PathInsideWindows] = "{0} is inside the Windows folder; changing access there can break Windows.",
        [ErrorCodes.WholeDriveNotAllowed] = "A whole drive ({0}) cannot be used here.",
        [ErrorCodes.AlreadyInList] = "{0} is already in the list.",
        [ErrorCodes.NotInList] = "{0} is not in the list.",
        [ErrorCodes.InvalidProgramName] = "'{0}' is not a program file name (for example game.exe).",
        [ErrorCodes.InvalidUserName] = "'{0}' is not a valid local account name.",
        [ErrorCodes.InvalidExtensionId] = "'{0}' is not a valid {1} extension ID.",
        [ErrorCodes.InvalidFileHash] = "The file fingerprint of '{0}' is not valid.",
        [ErrorCodes.NoRights] = "Choose at least one kind of access.",
        [ErrorCodes.InvalidPolicyValue] = "Policy {0}: only numbers, true/false, text or null are supported.",
        [ErrorCodes.AccountNotFound] = "There is no local account '{0}'.",
        [ErrorCodes.AccountIsAdministrator] = "{0} is an administrator; only standard accounts can be restricted.",
        [ErrorCodes.ProfileMissing] = "The profile of {0} does not exist yet: sign in as {0} once, then sign out.",
        [ErrorCodes.HiveLoadFailed] = "Cannot open the settings of {0} (Windows error {1}). If the account is signed in, try again after it signs out.",
        [ErrorCodes.AdministratorRequired] = "This needs administrator rights.",
        [ErrorCodes.ServiceNotRunning] = "The UseControl service is not running or not installed.",
        [ErrorCodes.ServiceNotTrusted] = "The program answering as the UseControl service is not the service. Nothing was sent to it.",
        [ErrorCodes.ServiceFailed] = "The UseControl service reported an error: {0}",
        [ErrorCodes.UnknownCommand] = "Unknown command '{0}'.",
        [ErrorCodes.InvalidRequest] = "The request is not valid: {0}",
        [ErrorCodes.HoursOutOfRange] = "Choose between {0} and {1} hours.",
    };

    public static string Format(string code, params object[] args)
    {
        var template = Texts.TryGetValue(code, out var t) ? t : code;
        return args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    /// <summary>All codes, so tests can check that every code has a text in every language.</summary>
    public static IReadOnlyCollection<string> AllCodes => Texts.Keys;
}
