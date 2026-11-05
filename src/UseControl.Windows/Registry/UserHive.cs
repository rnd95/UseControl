using Microsoft.Win32;
using UseControl.Core;
using UseControl.Windows.Native;

namespace UseControl.Windows.Registry;

/// <summary>
/// The registry of one account (its HKCU). When the account is signed in, Windows already has it
/// open under HKEY_USERS\&lt;SID&gt;; otherwise NTUSER.DAT is loaded there and unloaded on Dispose.
/// Every key opened below <see cref="Root"/> must be disposed before this object, or the unload fails.
/// </summary>
public sealed class UserHive : IDisposable
{
    private readonly string _sid;
    private readonly bool _loadedHere;

    private UserHive(string sid, RegistryKey root, bool loadedHere)
    {
        _sid = sid;
        Root = root;
        _loadedHere = loadedHere;
    }

    public RegistryKey Root { get; }

    /// <summary>True when the account was signed in (its hive was already loaded by Windows).</summary>
    public bool AccountSignedIn => !_loadedHere;

    public static UserHive Open(string accountName, string sid, string profilePath)
    {
        var existing = Microsoft.Win32.Registry.Users.OpenSubKey(sid, writable: true);
        if (existing is not null) return new UserHive(sid, existing, loadedHere: false);

        var file = Path.Combine(profilePath, "NTUSER.DAT");
        if (!File.Exists(file)) throw new UcException(ErrorCodes.ProfileMissing, accountName);
        try
        {
            NativeMethods.EnablePrivilege("SeRestorePrivilege");
            NativeMethods.EnablePrivilege("SeBackupPrivilege");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new UcException(ErrorCodes.AdministratorRequired);
        }
        var rc = NativeMethods.RegLoadKey(NativeMethods.HkeyUsers, sid, file);
        if (rc != 0) throw new UcException(ErrorCodes.HiveLoadFailed, accountName, rc);
        var root = Microsoft.Win32.Registry.Users.OpenSubKey(sid, writable: true)
            ?? throw new UcException(ErrorCodes.HiveLoadFailed, accountName, -1);
        return new UserHive(sid, root, loadedHere: true);
    }

    public void Dispose()
    {
        Root.Dispose();
        if (!_loadedHere) return;
        // Handles opened by .NET may linger until finalised; retry a few times.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (NativeMethods.RegUnLoadKey(NativeMethods.HkeyUsers, _sid) == 0) return;
            Thread.Sleep(200);
        }
        // It unloads at the next restart at the latest; the data is already written.
    }
}
