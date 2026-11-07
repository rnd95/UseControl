using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UseControl.Windows.Updates;

/// <summary>Authenticode signatures of programs and installers.</summary>
public static partial class Signature
{
    private static readonly Guid VerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>The publisher (certificate subject) of a file with a valid signature; null when it is not signed or the signature is not valid.</summary>
    public static string? Publisher(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!IsTrusted(file)) return null;
        try
        {
#pragma warning disable SYSLIB0057 // the only API that reads the signer of a signed file
            using var cert = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static unsafe bool IsTrusted(string file)
    {
        fixed (char* path = file)
        {
            var fileInfo = new FileInfo { Size = (uint)sizeof(FileInfo), Path = (IntPtr)path };
            var data = new TrustData
            {
                Size = (uint)sizeof(TrustData),
                UiChoice = 2,              // WTD_UI_NONE
                RevocationChecks = 0,      // WTD_REVOKE_NONE
                UnionChoice = 1,           // WTD_CHOICE_FILE
                File = (IntPtr)(&fileInfo),
                StateAction = 0,
                ProvFlags = 0x1000,        // WTD_CACHE_ONLY_URL_RETRIEVAL: no network
            };
            var action = VerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Size;
        public IntPtr Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
