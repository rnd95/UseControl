using Microsoft.Win32;
using UseControl.Windows.Registry;

namespace UseControl.Windows.Restrictions;

/// <summary>The only machine-wide setting: standard users cannot install Store/MSIX apps.</summary>
public static class MachinePolicies
{
    private const string AppxKey = @"SOFTWARE\Policies\Microsoft\Windows\Appx";
    private const string BlockNonAdminUserInstall = nameof(BlockNonAdminUserInstall);

    public static void SetStoreInstallsAdminOnly(bool enabled)
    {
        if (enabled) RegistryHelpers.SetDword(Microsoft.Win32.Registry.LocalMachine, AppxKey, BlockNonAdminUserInstall, 1);
        else RegistryHelpers.DeleteValue(Microsoft.Win32.Registry.LocalMachine, AppxKey, BlockNonAdminUserInstall);
    }

    public static bool StoreInstallsAdminOnly()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(AppxKey);
        return k?.GetValue(BlockNonAdminUserInstall) is int v && v == 1;
    }
}
