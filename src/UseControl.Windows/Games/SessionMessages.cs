using System.Globalization;
using UseControl.Localization;
using UseControl.Windows.Native;

namespace UseControl.Windows.Games;

/// <summary>
/// Shows a short message box on a signed-in account's screen, from the service (no program needs to
/// run in the account). The text is in the account's own language when UseControl has it.
/// </summary>
public static class SessionMessages
{
    /// <summary>Shows the text without waiting; the box closes by itself after <paramref name="timeoutSeconds"/>.</summary>
    public static bool Show(int sessionId, string sid, string key, int timeoutSeconds, params object?[] args)
    {
        var culture = LanguageOf(sid);
        return NativeMethods.SendSessionMessage(sessionId, Loc.In(culture, "Notify.Title"), Loc.In(culture, key, args), timeoutSeconds);
    }

    /// <summary>The language of a signed-in account (its regional format), or English.</summary>
    public static CultureInfo LanguageOf(string sid)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.Users.OpenSubKey(sid + @"\Control Panel\International");
            return Loc.Pick(k?.GetValue("LocaleName") as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Loc.Available[0];
        }
    }
}
