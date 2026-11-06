using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Windows.Native;

namespace UseControl.Windows.Games;

/// <summary>A game process an account runs now.</summary>
public sealed record RunningGame(string User, string Sid, int ProcessId, string Path, int SessionId);

/// <summary>What counts as a game for one preset: its game folders, names and files.</summary>
public sealed class GameMatcher
{
    private readonly List<string> _folders;
    private readonly HashSet<string> _names;
    private readonly Dictionary<long, List<string>> _filesBySize;

    public GameMatcher(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        _folders = [.. preset.BlockedPaths.Where(e => e.Category == RuleCategory.Games)
            .Select(e => PathRules.TryNormalize(e.Path, out var p) ? p : null).OfType<string>()];
        _names = new HashSet<string>(preset.BlockedNames.Where(n => n.Category == RuleCategory.Games).Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
        _filesBySize = preset.BlockedFiles.Where(f => f.Category == RuleCategory.Games)
            .GroupBy(f => f.Size).ToDictionary(g => g.Key, g => g.Select(f => f.Sha256).ToList());
    }

    public bool IsEmpty => _folders.Count == 0 && _names.Count == 0 && _filesBySize.Count == 0;

    /// <summary>True when the program at this path is a game of the preset.</summary>
    public bool Matches(string path, Func<string, long, string?> sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (_names.Contains(Path.GetFileName(path))) return true;
        if (PathRules.IsUnderAny(path, _folders)) return true;
        if (_filesBySize.Count == 0) return false;
        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        return _filesBySize.TryGetValue(size, out var hashes) && sha256(path, size) is { } h && hashes.Contains(h, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Finds and closes the games restricted accounts run. Used by the service to count play time and to
/// close games when games time is over. Needs SYSTEM or administrator rights to see other sessions.
/// </summary>
public sealed class GameProcesses
{
    // Fingerprints of programs already checked, by path, size and change time.
    private readonly Dictionary<(string Path, long Size, DateTime Changed), string?> _hashes = [];

    /// <param name="accounts">Account name to (SID, preset).</param>
    public IReadOnlyList<RunningGame> Find(IReadOnlyDictionary<string, (string Sid, Preset Preset)> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var matchers = accounts.ToDictionary(a => a.Value.Sid, a => (User: a.Key, Matcher: new GameMatcher(a.Value.Preset)), StringComparer.OrdinalIgnoreCase);
        if (matchers.Values.All(m => m.Matcher.IsEmpty)) return [];

        var found = new List<RunningGame>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                int session;
                try { session = p.SessionId; }
                catch (InvalidOperationException) { continue; }
                if (session == 0) continue;   // services, not people

                var info = ProcessInfo.Read(p.Id);
                if (info is null || !matchers.TryGetValue(info.Value.Sid, out var m)) continue;
                if (m.Matcher.Matches(info.Value.Path, Sha256)) found.Add(new RunningGame(m.User, info.Value.Sid, p.Id, info.Value.Path, session));
            }
        }
        return found;
    }

    /// <summary>Closes the given game processes; returns how many were closed.</summary>
    public static int Close(IEnumerable<RunningGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        var closed = 0;
        foreach (var g in games)
        {
            try
            {
                using var p = Process.GetProcessById(g.ProcessId);
                p.Kill();
                closed++;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or protected: nothing more to do.
            }
        }
        return closed;
    }

    private string? Sha256(string path, long size)
    {
        try
        {
            var key = (path, size, File.GetLastWriteTimeUtc(path));
            if (_hashes.TryGetValue(key, out var known)) return known;
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(s));
            if (_hashes.Count > 2000) _hashes.Clear();
            _hashes[key] = hash;
            return hash;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The program file and the owner of a process.</summary>
internal static class ProcessInfo
{
    public static (string Path, string Sid)? Read(int pid)
    {
        var path = NativeMethods.ProcessImagePath(pid, out var token);
        if (path is null) return null;
        try
        {
            if (token == IntPtr.Zero) return null;
            using var id = new WindowsIdentity(token);
            return id.User is { } sid ? (path, sid.Value) : null;
        }
        catch (Exception e) when (e is ArgumentException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandleIfValid(token);
        }
    }
}
