using System.Text.RegularExpressions;

namespace UseControl.Core.Validation;

public enum BrowserKind
{
    Chrome,
    Edge,
    Firefox,
}

/// <summary>Checks of values typed by the administrator.</summary>
public static partial class Validators
{
    [GeneratedRegex(@"^[^\\/:*?""<>|]+\.(exe|com|msc|bat|cmd|scr|cpl)$", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramName();

    // Local account names: up to 20 characters, none of " / \ [ ] : ; | = , + * ? < > @
    [GeneratedRegex(@"^[^\\/""\[\]:|<>+=;,?*@]{1,20}$")]
    private static partial Regex UserName();

    [GeneratedRegex("^[a-p]{32}$")]
    private static partial Regex ChromiumExtensionId();

    [GeneratedRegex(@"^(\{[0-9a-fA-F-]{36}\}|[^\s@]+@[^\s@]+)$")]
    private static partial Regex FirefoxExtensionId();

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex Hex();

    public static bool IsProgramName(string name) => ProgramName().IsMatch(name);

    public static bool IsUserName(string name) => UserName().IsMatch(name) && name.Trim().Length == name.Length && !name.EndsWith('.');

    /// <summary>Chrome and Edge: 32 letters a-p. Firefox: an e-mail-like ID or a {GUID}.</summary>
    public static bool IsExtensionId(BrowserKind browser, string id) =>
        browser == BrowserKind.Firefox ? FirefoxExtensionId().IsMatch(id) : ChromiumExtensionId().IsMatch(id);

    public static bool IsHex(string value, int length) => value.Length == length && Hex().IsMatch(value);
}
