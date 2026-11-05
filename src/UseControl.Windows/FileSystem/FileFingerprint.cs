using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using UseControl.Core.Configuration;

namespace UseControl.Windows.FileSystem;

/// <summary>The fingerprints Windows hash rules need for a program file.</summary>
public static class FileFingerprint
{
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "Software Restriction Policy hash rules are defined with MD5; it identifies a file, it does not protect a secret.")]
    public static FileHashEntry Compute(string path, RuleCategory category, string? note = null)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var sha = SHA256.HashData(s);
        s.Position = 0;
        var md5 = MD5.HashData(s);
        return new FileHashEntry
        {
            FileName = Path.GetFileName(path),
            Sha256 = Convert.ToHexString(sha),
            Md5 = Convert.ToHexString(md5),
            Size = s.Length,
            Category = category,
            Note = note,
        };
    }
}
