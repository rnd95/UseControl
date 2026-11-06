using System.Globalization;
using System.Resources;
using UseControl.Core;

namespace UseControl.Localization;

/// <summary>
/// Texts in the current language. Keys are the names in Resources\Strings.resx; a missing translation
/// falls back to English, a missing key shows the key itself (so a gap is visible, never a crash).
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Resources = new("UseControl.Localization.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>Languages that ship with UseControl. Add a code here when adding Strings.&lt;code&gt;.resx.</summary>
    public static IReadOnlyList<CultureInfo> Available { get; } = [CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("uk")];

    /// <summary>The language used; by default the Windows display language when it is available, otherwise English.</summary>
    public static CultureInfo Culture { get; set; } = Pick(CultureInfo.CurrentUICulture.Name);

    /// <summary>The closest available language for a code such as "uk", "uk-UA" or "en-GB".</summary>
    public static CultureInfo Pick(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Available[0];
        try
        {
            var c = CultureInfo.GetCultureInfo(name.Trim());
            return Available.FirstOrDefault(a => a.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase) || a.Name.Equals(c.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                ?? Available[0];
        }
        catch (CultureNotFoundException)
        {
            return Available[0];
        }
    }

    public static bool Has(string key) => Resources.GetString(key, CultureInfo.InvariantCulture) is not null;

    /// <summary>The English text, whatever the current language (for logs and machine-readable output).</summary>
    public static string English(string key, params object?[] args)
    {
        var text = Resources.GetString(key, CultureInfo.InvariantCulture) ?? key;
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }

    public static string T(string key, params object?[] args)
    {
        var text = Resources.GetString(key, Culture) ?? key;
        return args.Length == 0 ? text : string.Format(Culture, text, args);
    }

    /// <summary>The text in a given language (for messages shown to someone else than the administrator).</summary>
    public static string In(CultureInfo culture, string key, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var text = Resources.GetString(key, culture) ?? key;
        return args.Length == 0 ? text : string.Format(culture, text, args);
    }

    /// <summary>The message of an error in the current language.</summary>
    public static string Error(UcException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Error(e.Code, e.Args, e.Message);
    }

    public static string Error(string code, IReadOnlyList<object>? args, string fallback)
    {
        var key = "Error." + code;
        if (!Has(key)) return fallback;
        try
        {
            return T(key, [.. args ?? []]);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    /// <summary>All keys of a language file (for the completeness tests).</summary>
    public static IReadOnlyCollection<string> Keys(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        // Not disposed: ResourceManager caches this set and keeps using it for every lookup.
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        if (set is null) return [];
        return [.. set.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key)];
    }
}
