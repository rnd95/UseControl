using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Rules;
using UseControl.Windows.FileSystem;

namespace UseControl.Windows.Scanning;

/// <summary>Finds the places on this PC where a restricted account could put a program and run it.</summary>
public sealed class AutoPathScanner
{
    /// <summary>Folders inside Windows that ordinary users can write to by default.</summary>
    internal static readonly string[] SystemWritableFolders =
    [
        "Temp", "Tasks", "tracing", @"Registration\CRMLog", @"System32\Tasks", @"SysWOW64\Tasks",
        @"System32\spool\drivers\color", @"System32\spool\PRINTERS", @"System32\com\dmp", @"SysWOW64\com\dmp",
        @"System32\FxsTmp", @"SysWOW64\FxsTmp", @"System32\Microsoft\Crypto\RSA\MachineKeys", @"debug\WIA",
        @"PLA\Reports", @"PLA\Rules", @"PLA\Templates", @"servicing\Sessions",
    ];

    private readonly ISystemPaths _sp;
    private readonly Func<IReadOnlyList<string>> _fixedDrives;
    private readonly Func<string, IReadOnlyList<string>> _writableBy;

    public AutoPathScanner(ISystemPaths sp)
        : this(sp, GetFixedDrives, p => AclInspector.WritableBy(p))
    {
    }

    internal AutoPathScanner(ISystemPaths sp, Func<IReadOnlyList<string>> fixedDrives, Func<string, IReadOnlyList<string>> writableBy)
    {
        _sp = sp;
        _fixedDrives = fixedDrives;
        _writableBy = writableBy;
    }

    public static IReadOnlyList<string> GetFixedDrives() =>
        [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => PathRules.Normalize(d.Name))];

    /// <summary>Windows and Program Files: allowed even when the system drive root is blocked.</summary>
    public IReadOnlyList<string> SystemAllowedPaths() => [_sp.WindowsDirectory, .. _sp.ProgramFilesDirectories];

    public IReadOnlyList<AutoPath> Scan(AutoBlockOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var list = new List<AutoPath>();
        var fixedDrives = _fixedDrives();

        // Ordinary users can create folders in the root of the system drive by default (C:\anything).
        if (options.SystemDriveRoot) list.Add(new AutoPath(_sp.SystemDriveRoot, "system drive root"));

        if (options.SystemWritableFolders)
        {
            foreach (var f in SystemWritableFolders) list.Add(new AutoPath($@"{_sp.WindowsDirectory}\{f}", "writable folder inside Windows"));
            list.Add(new AutoPath($@"{_sp.UsersDirectory}\Public", "shared by all users"));
            list.Add(new AutoPath(_sp.ProgramData, "users can create folders there"));
        }

        var blockedRoots = new List<string>();
        if (options.WritableDriveRoots)
        {
            foreach (var d in fixedDrives.Where(d => !d.Equals(_sp.SystemDriveRoot, StringComparison.OrdinalIgnoreCase)))
            {
                if (_writableBy(d).Count == 0) continue;
                list.Add(new AutoPath(d, "drive writable by ordinary users"));
                blockedRoots.Add(d);
            }
        }

        if (options.OtherDriveLetters)
        {
            foreach (var c in Enumerable.Range('A', 26).Select(i => (char)i))
            {
                var root = $@"{c}:\";
                if (!fixedDrives.Contains(root, StringComparer.OrdinalIgnoreCase)) list.Add(new AutoPath(root, "USB, network or future drive"));
            }
        }

        if (options.RecycleBins)
        {
            foreach (var d in fixedDrives) list.Add(new AutoPath($"{d}$Recycle.Bin", "Recycle Bin"));
        }

        if (options.WritableProgramFolders)
        {
            var candidates = new List<string>();
            foreach (var pf in _sp.ProgramFilesDirectories) candidates.AddRange(SubDirectories(pf));
            foreach (var d in fixedDrives.Where(d => !d.Equals(_sp.SystemDriveRoot, StringComparison.OrdinalIgnoreCase) && !blockedRoots.Contains(d)))
            {
                var level1 = SubDirectories(d).Where(p => !IsSystemFolderName(Path.GetFileName(p))).ToList();
                candidates.AddRange(level1);
                foreach (var l1 in level1) candidates.AddRange(SubDirectories(l1));
            }
            foreach (var c in candidates)
            {
                var who = _writableBy(c);
                if (who.Count > 0) list.Add(new AutoPath(c, "program folder writable by " + string.Join(", ", who)));
            }
        }
        return list;
    }

    private static bool IsSystemFolderName(string name) =>
        name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    private static List<string> SubDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Select(p => p.TrimEnd('\\')).ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}
