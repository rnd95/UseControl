namespace UseControl.Core.Paths;

/// <summary>Where Windows and programs live on this PC. An interface so tests can use fixed values.</summary>
public interface ISystemPaths
{
    /// <summary>For example "C:\".</summary>
    string SystemDriveRoot { get; }

    /// <summary>For example "C:\Windows".</summary>
    string WindowsDirectory { get; }

    /// <summary>Program Files folders (64-bit and 32-bit), without duplicates.</summary>
    IReadOnlyList<string> ProgramFilesDirectories { get; }

    string ProgramData { get; }

    string UsersDirectory { get; }
}

public sealed class WindowsSystemPaths : ISystemPaths
{
    public static WindowsSystemPaths Current { get; } = new();

    private WindowsSystemPaths()
    {
        var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        SystemDriveRoot = PathRules.Normalize(drive);
        WindowsDirectory = PathRules.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } w ? w : $@"{drive}\Windows");
        ProgramFilesDirectories =
        [
            .. new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            }
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => PathRules.Normalize(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        ProgramData = PathRules.Normalize(Environment.GetEnvironmentVariable("ProgramData") ?? $@"{drive}\ProgramData");
        UsersDirectory = PathRules.Normalize($@"{drive}\Users");
    }

    public string SystemDriveRoot { get; }
    public string WindowsDirectory { get; }
    public IReadOnlyList<string> ProgramFilesDirectories { get; }
    public string ProgramData { get; }
    public string UsersDirectory { get; }
}

/// <summary>Fixed paths, for tests and for describing another PC.</summary>
public sealed class FixedSystemPaths : ISystemPaths
{
    public string SystemDriveRoot { get; init; } = @"C:\";
    public string WindowsDirectory { get; init; } = @"C:\Windows";
    public IReadOnlyList<string> ProgramFilesDirectories { get; init; } = [@"C:\Program Files", @"C:\Program Files (x86)"];
    public string ProgramData { get; init; } = @"C:\ProgramData";
    public string UsersDirectory { get; init; } = @"C:\Users";
}
