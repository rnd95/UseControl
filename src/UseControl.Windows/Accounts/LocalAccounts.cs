using System.DirectoryServices.AccountManagement;
using System.Security.Principal;
using Microsoft.Win32;
using UseControl.Core;

namespace UseControl.Windows.Accounts;

/// <summary>A local account as UseControl needs it.</summary>
public sealed record AccountInfo(string Name, string Sid, bool Enabled, bool PasswordNotRequired, bool IsAdministrator, bool IsBuiltInAdministrator, string? ProfilePath);

/// <summary>Local users and the Administrators group (found by SID, so any Windows language works).</summary>
public static class LocalAccounts
{
    public const string AdministratorsSid = "S-1-5-32-544";
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const string HiddenList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    public static IReadOnlyList<AccountInfo> GetAll()
    {
        var admins = GetAdministratorSids();
        using var ctx = new PrincipalContext(ContextType.Machine);
        using var query = new PrincipalSearcher(new UserPrincipal(ctx));
        var list = new List<AccountInfo>();
        foreach (var p in query.FindAll())
        {
            using (p)
            {
                if (p is UserPrincipal u && u.Sid is not null) list.Add(ToInfo(u, admins));
            }
        }
        return list;
    }

    /// <summary>The account to restrict: must exist, be a standard account and have a profile.</summary>
    public static AccountInfo GetRestrictable(string name)
    {
        var a = Find(name) ?? throw new UcException(ErrorCodes.AccountNotFound, name);
        if (a.IsAdministrator) throw new UcException(ErrorCodes.AccountIsAdministrator, a.Name);
        if (string.IsNullOrEmpty(a.ProfilePath) || !File.Exists(Path.Combine(a.ProfilePath, "NTUSER.DAT")))
            throw new UcException(ErrorCodes.ProfileMissing, a.Name);
        return a;
    }

    public static AccountInfo? Find(string name)
    {
        using var ctx = new PrincipalContext(ContextType.Machine);
        using var u = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, name);
        return u?.Sid is null ? null : ToInfo(u, GetAdministratorSids());
    }

    /// <summary>Standard (non-administrator) enabled accounts, for "add account" lists.</summary>
    public static IReadOnlyList<AccountInfo> GetStandardAccounts() =>
        [.. GetAll().Where(a => a.Enabled && !a.IsAdministrator).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];

    public static IReadOnlySet<string> GetAdministratorSids()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var ctx = new PrincipalContext(ContextType.Machine);
        using var g = GroupPrincipal.FindByIdentity(ctx, IdentityType.Sid, AdministratorsSid);
        if (g is null) return set;
        // GetMembers can fail on members whose account no longer exists; read them one by one.
        using var members = g.GetMembers().GetEnumerator();
        while (true)
        {
            try
            {
                if (!members.MoveNext()) break;
                if (members.Current.Sid is { } sid) set.Add(sid.Value);
                members.Current.Dispose();
            }
            catch (PrincipalOperationException)
            {
                continue;
            }
            catch (NoMatchingPrincipalException)
            {
                continue;
            }
        }
        return set;
    }

    /// <summary>Names of the members of the Administrators group.</summary>
    public static IReadOnlyList<string> GetAdministratorNames()
    {
        var names = new List<string>();
        foreach (var sid in GetAdministratorSids())
        {
            try { names.Add(new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value.Split('\\')[^1]); }
            catch (IdentityNotMappedException) { names.Add(sid); }
        }
        return names;
    }

    public static string? GetProfilePath(string sid)
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"{ProfileList}\{sid}");
        return k?.GetValue("ProfileImagePath") is string p ? Environment.ExpandEnvironmentVariables(p) : null;
    }

    /// <summary>True when the account is hidden from the sign-in screen (Winlogon UserList value 0).</summary>
    public static bool IsHiddenFromSignIn(string name)
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(HiddenList);
        return k?.GetValue(name) is int v && v == 0;
    }

    private static AccountInfo ToInfo(UserPrincipal u, IReadOnlySet<string> admins)
    {
        var sid = u.Sid.Value;
        return new AccountInfo(
            u.SamAccountName,
            sid,
            u.Enabled ?? false,
            u.PasswordNotRequired,
            admins.Contains(sid),
            sid.EndsWith("-500", StringComparison.Ordinal),
            GetProfilePath(sid));
    }
}
