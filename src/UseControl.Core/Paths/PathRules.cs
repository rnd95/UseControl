using System.Buffers;
using System.Text.RegularExpressions;

namespace UseControl.Core.Paths;

/// <summary>Normalising local paths and deciding which ones may be used in rules.</summary>
public static partial class PathRules
{
    [GeneratedRegex(@"^[A-Za-z]:(\\|$)")]
    private static partial Regex AbsoluteLocal();

    private static readonly SearchValues<char> InvalidChars = SearchValues.Create("*?\"<>|");

    /// <summary>
    /// "d:" -> "D:\", "C:\Foo\" -> "C:\Foo"; quotes and spaces trimmed; environment variables of the
    /// running process expanded (%ProgramFiles(x86)%\Steam). Only absolute local paths are accepted.
    /// </summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim()).Replace('/', '\\');
        if (!AbsoluteLocal().IsMatch(p)) throw new UcException(ErrorCodes.PathNotAbsolute, path);
        if (p.AsSpan().IndexOfAny(InvalidChars) >= 0 || p.AsSpan(2).Contains(':')) throw new UcException(ErrorCodes.PathInvalidCharacters, path);
        p = char.ToUpperInvariant(p[0]) + p[1..];
        if (p.Length <= 3) return p[..2] + "\\";
        return p.TrimEnd('\\');
    }

    public static bool TryNormalize(string path, out string normalized)
    {
        try { normalized = Normalize(path); return true; }
        catch (UcException) { normalized = ""; return false; }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="parent"/> or inside it (both normalised).</summary>
    public static bool IsUnder(string path, string parent)
    {
        if (path.Equals(parent, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = parent.EndsWith('\\') ? parent : parent + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnderAny(string path, IEnumerable<string> parents) => parents.Any(p => IsUnder(path, p));

    public static bool IsDriveRoot(string normalizedPath) => normalizedPath.Length == 3;

    /// <summary>Paths that must never be blocked or locked as a whole.</summary>
    public static IReadOnlyList<string> ProtectedPaths(ISystemPaths sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        var list = new List<string>
        {
            sp.SystemDriveRoot,
            sp.WindowsDirectory,
            sp.WindowsDirectory + @"\System32",
            sp.WindowsDirectory + @"\SysWOW64",
            sp.WindowsDirectory + @"\explorer.exe",
        };
        foreach (var pf in sp.ProgramFilesDirectories)
        {
            list.Add(pf);
            list.Add(pf + @"\Common Files");
        }
        return list;
    }

    /// <summary>Throws when blocking programs from <paramref name="path"/> would break Windows or every program.</summary>
    public static string EnsureCanBlock(string path, ISystemPaths sp)
    {
        var p = Normalize(path);
        foreach (var prot in ProtectedPaths(sp))
        {
            if (p.Equals(prot, StringComparison.OrdinalIgnoreCase)) throw new UcException(ErrorCodes.PathProtected, p);
            if (IsUnder(prot, p)) throw new UcException(ErrorCodes.PathContainsProtected, p, prot);
        }
        return p;
    }

    /// <summary>
    /// Throws when changing file-system access to <paramref name="path"/> is too risky: drive roots,
    /// anything inside Windows, and folders that contain Windows, Program Files, ProgramData or the profiles.
    /// </summary>
    public static string EnsureCanChangeAccess(string path, ISystemPaths sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        var p = Normalize(path);
        if (IsDriveRoot(p)) throw new UcException(ErrorCodes.WholeDriveNotAllowed, p);
        if (IsUnder(p, sp.WindowsDirectory)) throw new UcException(ErrorCodes.PathInsideWindows, p);
        foreach (var prot in ProtectedPaths(sp).Concat([sp.ProgramData, sp.UsersDirectory]))
        {
            if (p.Equals(prot, StringComparison.OrdinalIgnoreCase)) throw new UcException(ErrorCodes.PathProtected, p);
            if (IsUnder(prot, p)) throw new UcException(ErrorCodes.PathContainsProtected, p, prot);
        }
        return p;
    }
}
