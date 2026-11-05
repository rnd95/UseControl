using UseControl.Core.Paths;

namespace UseControl.Core.Tests;

public class PathRulesTests
{
    private static readonly FixedSystemPaths Sp = new();

    [Theory]
    [InlineData("d:", @"D:\")]
    [InlineData(@"e:\", @"E:\")]
    [InlineData(@" ""F:\Games\X\"" ", @"F:\Games\X")]
    [InlineData("c:/Tools/x", @"C:\Tools\x")]
    public void Normalize_cleans_paths(string input, string expected) => Assert.Equal(expected, PathRules.Normalize(input));

    [Fact]
    public void Normalize_expands_environment_variables() =>
        Assert.Equal(PathRules.Normalize(Environment.GetEnvironmentVariable("ProgramFiles")!) + @"\X", PathRules.Normalize(@"%ProgramFiles%\X"));

    [Theory]
    [InlineData(@"Games\X")]
    [InlineData(@"\\server\share")]
    [InlineData("")]
    public void Normalize_rejects_relative_and_network_paths(string input) =>
        Assert.Equal(ErrorCodes.PathNotAbsolute, Assert.Throws<UcException>(() => PathRules.Normalize(input)).Code);

    [Theory]
    [InlineData(@"F:\Games\*")]
    [InlineData(@"F:\a|b")]
    [InlineData(@"F:\a:b")]
    public void Normalize_rejects_invalid_characters(string input) =>
        Assert.Equal(ErrorCodes.PathInvalidCharacters, Assert.Throws<UcException>(() => PathRules.Normalize(input)).Code);

    [Theory]
    [InlineData(@"D:\games\X", @"D:\", true)]
    [InlineData(@"F:\Programs\Steam", @"F:\Programs\Steam", true)]
    [InlineData(@"f:\programs\steam\x", @"F:\Programs\Steam", true)]
    [InlineData(@"F:\Programs\SteamTools", @"F:\Programs\Steam", false)]
    public void IsUnder_matches_the_folder_and_below_only(string path, string parent, bool expected) =>
        Assert.Equal(expected, PathRules.IsUnder(path, parent));

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"c:\program files\")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\Windows\System32")]
    public void Windows_and_Program_Files_cannot_be_blocked(string path) =>
        Assert.Throws<UcException>(() => PathRules.EnsureCanBlock(path, Sp));

    [Theory]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Program Files (x86)\Steam")]
    [InlineData(@"D:\")]
    public void Folders_inside_them_and_other_drives_can_be_blocked(string path) =>
        PathRules.EnsureCanBlock(path, Sp);

    [Theory]
    [InlineData(@"D:\", ErrorCodes.WholeDriveNotAllowed)]
    [InlineData(@"C:\Windows\Temp", ErrorCodes.PathInsideWindows)]
    [InlineData(@"C:\Users", ErrorCodes.PathProtected)]
    [InlineData(@"C:\ProgramData", ErrorCodes.PathProtected)]
    [InlineData(@"C:\Program Files", ErrorCodes.PathProtected)]
    public void File_access_refuses_risky_places(string path, string code) =>
        Assert.Equal(code, Assert.Throws<UcException>(() => PathRules.EnsureCanChangeAccess(path, Sp)).Code);

    [Theory]
    [InlineData(@"D:\Stanislav")]
    [InlineData(@"C:\Program Files\SomeGame")]
    [InlineData(@"C:\Users\Admin\Documents")]
    public void File_access_allows_ordinary_folders(string path) => PathRules.EnsureCanChangeAccess(path, Sp);
}
