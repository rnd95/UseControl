using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UseControl.Ipc;

internal static partial class PipeNative
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    public static int ServerProcessId(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var pid) ? (int)pid : throw new System.ComponentModel.Win32Exception();

    public static int ClientProcessId(SafePipeHandle pipe) =>
        GetNamedPipeClientProcessId(pipe, out var pid) ? (int)pid : throw new System.ComponentModel.Win32Exception();

    /// <summary>
    /// Services run in session 0, which ordinary users cannot start processes in. A pipe served from
    /// another session is a program that took the name before the service started.
    /// </summary>
    public static bool IsServedFromSessionZero(SafePipeHandle pipe)
    {
        using var p = Process.GetProcessById(ServerProcessId(pipe));
        return p.SessionId == 0;
    }
}
