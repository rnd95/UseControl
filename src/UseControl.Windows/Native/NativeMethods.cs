using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UseControl.Windows.Native;

internal static partial class NativeMethods
{
    internal static readonly IntPtr HkeyUsers = unchecked((IntPtr)(int)0x80000003);

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegLoadKeyW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegLoadKey(IntPtr hKey, string lpSubKey, string lpFile);

    [LibraryImport("advapi32.dll", EntryPoint = "RegUnLoadKeyW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegUnLoadKey(IntPtr hKey, string lpSubKey);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    /// <summary>Enables a privilege the process token already holds (administrators hold backup and restore).</summary>
    internal static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception();
            var tp = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var err = Marshal.GetLastPInvokeError();
            if (err == ErrorNotAllAssigned) throw new Win32Exception(err);
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
