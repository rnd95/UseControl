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

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(IntPtr process, uint flags, char* exeName, ref int size);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSSendMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSSendMessage(IntPtr server, int sessionId, string title, int titleLength, string message, int messageLength,
        uint style, int timeout, out int response, [MarshalAs(UnmanagedType.Bool)] bool wait);

    /// <summary>The program file of a process and its access token (TOKEN_QUERY; close it), or null.</summary>
    internal static unsafe string? ProcessImagePath(int pid, out IntPtr token)
    {
        token = IntPtr.Zero;
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024;
            if (!QueryFullProcessImageName(h, 0, buffer, ref size)) return null;
            if (!OpenProcessToken(h, TokenQuery, out token)) token = IntPtr.Zero;
            return new string(buffer, 0, size);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    internal static void CloseHandleIfValid(IntPtr handle)
    {
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    /// <summary>A message box in a user session (OK button, information icon, on top), without waiting for it.</summary>
    internal static bool SendSessionMessage(int sessionId, string title, string message, int timeoutSeconds)
    {
        const uint style = 0x0 | 0x40 | 0x10000 | 0x40000;   // MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST
        return WTSSendMessage(IntPtr.Zero, sessionId, title, title.Length * 2, message, message.Length * 2, style, timeoutSeconds, out _, false);
    }

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
