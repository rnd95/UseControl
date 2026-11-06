using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace UseControl.Admin.Infrastructure;

/// <summary>
/// The panel starts without asking for elevation and decides itself: an administrator working without
/// elevation gets the usual Windows prompt, anybody else learns that only administrators may use it.
/// </summary>
public static partial class Elevation
{
    private const int TokenElevationType = 18;
    private const int TokenElevationTypeLimited = 3;
    private const int ErrorCancelled = 1223;

    /// <summary>Running with administrator rights now.</summary>
    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>An administrator account working without elevation (UAC gave it a limited token).</summary>
    public static bool IsAdministratorWithoutElevation()
    {
        using var id = WindowsIdentity.GetCurrent();
        return GetTokenInformation(id.Token, TokenElevationType, out var type, sizeof(int), out _) && type == TokenElevationTypeLimited;
    }

    /// <summary>Starts this program again with "Run as administrator". False when the prompt was cancelled.</summary>
    public static bool RestartElevated(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)),
        };
        try
        {
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}
