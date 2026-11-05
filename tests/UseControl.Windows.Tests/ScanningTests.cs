using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Windows.Audit;
using UseControl.Windows.Scanning;

namespace UseControl.Windows.Tests;

public class ScanningTests
{
    private static readonly FixedSystemPaths Sp = new();

    private static AutoPathScanner Scanner(params string[] writable) =>
        new(Sp, () => [@"C:\", @"D:\", @"F:\"], p => writable.Contains(p, StringComparer.OrdinalIgnoreCase) ? ["Everyone"] : []);

    [Fact]
    public void Finds_the_system_root_writable_drives_other_letters_and_recycle_bins()
    {
        var found = Scanner(@"D:\").Scan(new AutoBlockOptions { WritableProgramFolders = false }).Select(a => a.Path).ToList();
        Assert.Contains(@"C:\", found);
        Assert.Contains(@"D:\", found);
        Assert.DoesNotContain(@"F:\", found);
        Assert.Contains(@"E:\", found);
        Assert.Contains(@"Z:\", found);
        Assert.Contains(@"F:\$Recycle.Bin", found);
        Assert.Contains(@"C:\Windows\Temp", found);
        Assert.Contains(@"C:\ProgramData", found);
        Assert.Contains(@"C:\Users\Public", found);
    }

    [Fact]
    public void Switched_off_options_find_nothing()
    {
        var none = new AutoBlockOptions
        {
            SystemDriveRoot = false, WritableDriveRoots = false, OtherDriveLetters = false,
            RecycleBins = false, SystemWritableFolders = false, WritableProgramFolders = false,
        };
        Assert.Empty(Scanner(@"D:\").Scan(none));
    }

    [Fact]
    public void Real_scan_runs_on_this_PC()
    {
        var found = new AutoPathScanner(WindowsSystemPaths.Current).Scan(new AutoBlockOptions());
        Assert.Contains(found, a => a.Path == WindowsSystemPaths.Current.SystemDriveRoot);
        Assert.All(found, a => PathRules.Normalize(a.Path));
    }

    [Fact]
    public void Reads_steam_library_folders()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path"  "C:\\Program Files (x86)\\Steam"  "label" "" }
                "1" { "path"  "D:\\SteamLibrary" }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameFinder.ParseSteamLibraries(vdf));
        Assert.Empty(GameFinder.ParseSteamLibraries(""));
    }

    [Fact]
    public void Reads_epic_install_location()
    {
        Assert.Equal(@"D:\games\Epic Games\Fortnite", GameFinder.ParseEpicInstallLocation("""{ "InstallLocation": "D:\\games\\Epic Games\\Fortnite" }"""));
        Assert.Null(GameFinder.ParseEpicInstallLocation("not json"));
        Assert.Null(GameFinder.ParseEpicInstallLocation("{}"));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\svc.exe"" -run", @"C:\Program Files\App\svc.exe")]
    [InlineData(@"C:\Program Files\App\svc.exe -k x", @"C:\Program Files\App\svc.exe")]
    [InlineData(@"\??\C:\Drivers\x.exe", @"C:\Drivers\x.exe")]
    [InlineData(@"", null)]
    public void Reads_service_program_paths(string imagePath, string? expected) =>
        Assert.Equal(expected, SecurityAudit.ServiceExecutable(imagePath));

    [Fact]
    public void Security_check_runs_without_administrator_rights()
    {
        var c = new UcConfig();
        var checks = SecurityAudit.Run(c, [], new Dictionary<string, Restrictions.RestrictionState?>());
        Assert.Contains(checks, x => x.Id == "services");
        Assert.Contains(checks, x => x.Id == "uac");
        Assert.Contains(checks, x => x.Id == "restrictions" && x.Status == AuditStatus.Warning);
    }
}
