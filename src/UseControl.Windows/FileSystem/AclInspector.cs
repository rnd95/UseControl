using System.Security.AccessControl;
using System.Security.Principal;

namespace UseControl.Windows.FileSystem;

/// <summary>Who can change files and folders.</summary>
public static class AclInspector
{
    /// <summary>Everyone, Users, Authenticated Users, INTERACTIVE.</summary>
    public static readonly IReadOnlyList<string> OrdinaryUserSids = ["S-1-1-0", "S-1-5-32-545", "S-1-5-11", "S-1-5-4"];

    private const FileSystemRights WriteMask =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Names of the given accounts/groups (default: ordinary users) that may create or change files in <paramref name="path"/>.</summary>
    public static IReadOnlyList<string> WritableBy(string path, IEnumerable<string>? sids = null)
    {
        var set = new HashSet<string>(sids ?? OrdinaryUserSids, StringComparer.OrdinalIgnoreCase);
        FileSystemSecurity sec;
        try
        {
            sec = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            return [];
        }
        var who = new List<string>();
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow || (r.FileSystemRights & WriteMask) == 0) continue;
            if (!set.Contains(r.IdentityReference.Value)) continue;
            var name = Name(r.IdentityReference.Value);
            if (!who.Contains(name)) who.Add(name);
        }
        return who;
    }

    /// <summary>Allow entries on <paramref name="path"/> for the given accounts/groups, as "name rights".</summary>
    public static IReadOnlyList<string> AccessFor(string path, IEnumerable<string> sids)
    {
        var set = new HashSet<string>(sids, StringComparer.OrdinalIgnoreCase);
        var sec = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : (FileSystemSecurity)new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        return
        [
            .. sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow && set.Contains(r.IdentityReference.Value))
                .Select(r => $"{Name(r.IdentityReference.Value)} {r.FileSystemRights}"),
        ];
    }

    public static string Name(string sid)
    {
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException) { return sid; }
    }
}
