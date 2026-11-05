using System.Runtime.InteropServices;
using System.Security.Principal;

namespace UseControl.Windows.Accounts;

/// <summary>
/// Local users and groups through netapi32. Used instead of System.DirectoryServices.AccountManagement,
/// which takes 10–20 seconds per call on some PCs.
/// </summary>
internal static partial class NetApi
{
    private const int NerrSuccess = 0;
    private const int ErrorMoreData = 234;
    private const uint MaxPreferredLength = 0xFFFFFFFF;
    private const uint FilterNormalAccount = 0x0002;
    internal const uint UfAccountDisable = 0x0002;
    internal const uint UfPasswdNotReqd = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo1
    {
        public IntPtr Name;
        public IntPtr Password;
        public uint PasswordAge;
        public uint Priv;
        public IntPtr HomeDir;
        public IntPtr Comment;
        public uint Flags;
        public IntPtr ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMembersInfo0
    {
        public IntPtr Sid;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetUserEnum(string? serverName, uint level, uint filter, out IntPtr buffer, uint prefMaxLen, out uint entriesRead, out uint totalEntries, ref uint resumeHandle);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetUserGetInfo(string? serverName, string userName, uint level, out IntPtr buffer);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupGetMembers(string? serverName, string localGroupName, uint level, out IntPtr buffer, uint prefMaxLen, out uint entriesRead, out uint totalEntries, ref nint resumeHandle);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);

    /// <summary>Name and flags of every normal local account.</summary>
    public static IReadOnlyList<(string Name, uint Flags)> Users()
    {
        var list = new List<(string, uint)>();
        uint resume = 0;
        int rc;
        do
        {
            rc = NetUserEnum(null, 1, FilterNormalAccount, out var buf, MaxPreferredLength, out var read, out _, ref resume);
            if (rc != NerrSuccess && rc != ErrorMoreData) throw new System.ComponentModel.Win32Exception(rc);
            try
            {
                var size = Marshal.SizeOf<UserInfo1>();
                for (var i = 0; i < read; i++)
                {
                    var u = Marshal.PtrToStructure<UserInfo1>(buf + i * size);
                    list.Add((Marshal.PtrToStringUni(u.Name) ?? "", u.Flags));
                }
            }
            finally
            {
                if (buf != IntPtr.Zero) _ = NetApiBufferFree(buf);
            }
        }
        while (rc == ErrorMoreData);
        return list;
    }

    /// <summary>Flags of one local account, or null when it does not exist.</summary>
    public static uint? UserFlags(string name)
    {
        var rc = NetUserGetInfo(null, name, 1, out var buf);
        try
        {
            if (rc != NerrSuccess) return null;
            return Marshal.PtrToStructure<UserInfo1>(buf).Flags;
        }
        finally
        {
            if (buf != IntPtr.Zero) _ = NetApiBufferFree(buf);
        }
    }

    /// <summary>SIDs of the members of a local group, found by the group's SID (the name differs by language).</summary>
    public static IReadOnlyList<string> GroupMemberSids(string groupSid)
    {
        var groupName = new SecurityIdentifier(groupSid).Translate(typeof(NTAccount)).Value.Split('\\')[^1];
        var list = new List<string>();
        nint resume = 0;
        int rc;
        do
        {
            rc = NetLocalGroupGetMembers(null, groupName, 0, out var buf, MaxPreferredLength, out var read, out _, ref resume);
            if (rc != NerrSuccess && rc != ErrorMoreData) throw new System.ComponentModel.Win32Exception(rc);
            try
            {
                var size = Marshal.SizeOf<LocalGroupMembersInfo0>();
                for (var i = 0; i < read; i++)
                {
                    var m = Marshal.PtrToStructure<LocalGroupMembersInfo0>(buf + i * size);
                    list.Add(new SecurityIdentifier(m.Sid).Value);
                }
            }
            finally
            {
                if (buf != IntPtr.Zero) _ = NetApiBufferFree(buf);
            }
        }
        while (rc == ErrorMoreData);
        return list;
    }
}
