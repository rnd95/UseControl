using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace UseControl.Windows.Registry;

internal static class RegistryHelpers
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>Opens a key for writing, creating it if missing. Never deletes anything that is there.</summary>
    public static RegistryKey Ensure(RegistryKey parent, string path) =>
        parent.CreateSubKey(path, writable: true) ?? throw new InvalidOperationException($"Cannot create {parent.Name}\\{path}");

    public static void DeleteTree(RegistryKey parent, string path) => parent.DeleteSubKeyTree(path, throwOnMissingSubKey: false);

    public static void DeleteValue(RegistryKey parent, string path, string name)
    {
        using var k = parent.OpenSubKey(path, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public static void SetDword(RegistryKey parent, string path, string name, int value)
    {
        using var k = Ensure(parent, path);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Makes a key read-only for the user: owned by Administrators, full control for Administrators
    /// and SYSTEM, read for the user, nothing inherited.
    /// </summary>
    public static void Lock(RegistryKey parent, string path, string userSid)
    {
        using var k = OpenForSecurity(parent, path);
        if (k is null) return;
        var sec = new RegistrySecurity();
        sec.SetOwner(Administrators);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (who, rights) in new[] { (Administrators, RegistryRights.FullControl), (LocalSystem, RegistryRights.FullControl), (new SecurityIdentifier(userSid), RegistryRights.ReadKey) })
        {
            sec.AddAccessRule(new RegistryAccessRule(who, rights, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        k.SetAccessControl(sec);
    }

    /// <summary>Back to inherited permissions only. The owner stays Administrators (handing ownership to another account needs a privilege).</summary>
    public static void Unlock(RegistryKey parent, string path)
    {
        using var k = OpenForSecurity(parent, path);
        if (k is null) return;
        var sec = new RegistrySecurity();
        sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        k.SetAccessControl(sec);
    }

    /// <summary>True when the key is owned by Administrators and neither the user nor everyone can change it.</summary>
    public static bool IsLocked(RegistryKey parent, string path, string userSid)
    {
        using var k = parent.OpenSubKey(path, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadPermissions);
        if (k is null) return false;
        var sec = k.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (sec.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != Administrators) return false;
        const RegistryRights write = RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership;
        string[] weak = [userSid, "S-1-5-11", "S-1-1-0", "S-1-5-32-545", "S-1-5-4"];
        foreach (RegistryAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType == AccessControlType.Allow && weak.Contains(r.IdentityReference.Value) && (r.RegistryRights & write) != 0) return false;
        }
        return true;
    }

    private static RegistryKey? OpenForSecurity(RegistryKey parent, string path) =>
        parent.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership | RegistryRights.ReadKey);
}
