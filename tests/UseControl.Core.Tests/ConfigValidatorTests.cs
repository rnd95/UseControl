using UseControl.Core.Configuration;
using UseControl.Core.Paths;

namespace UseControl.Core.Tests;

public class ConfigValidatorTests
{
    private static readonly FixedSystemPaths Sp = new();

    [Fact]
    public void Default_config_is_valid() => Assert.Empty(ConfigValidator.Validate(ConfigSerializer.LoadDefault(), WindowsSystemPaths.Current));

    [Fact]
    public void Reports_every_problem_at_once()
    {
        var c = new UcConfig { Users = ["kid", "KID", @"bad\name"] };
        c.BlockedPaths.Add(new PathEntry { Path = @"C:\Windows" });
        c.BlockedPaths.Add(new PathEntry { Path = @"D:\X" });
        c.BlockedPaths.Add(new PathEntry { Path = @"d:\x\" });
        c.AllowedPaths.Add(new AllowedPathEntry { Path = @"E:\" });
        c.BlockedNames.Add(new NameEntry { Name = "readme.txt" });
        c.BlockedFiles.Add(new FileHashEntry { FileName = "x.exe", Sha256 = "1", Md5 = "2", Size = 1 });
        c.FileAccess.Add(new FileAccessRule { Path = @"C:\Windows\Temp", Rights = FileAccessRights.None });
        c.Browsers.Chrome.ExtensionsAllowed.Add("nope");
        c.Browsers.Edge.Policies["X"] = System.Text.Json.JsonDocument.Parse("[1]").RootElement;

        var codes = ConfigValidator.Validate(c, Sp).Select(e => e.Code).ToList();
        Assert.Contains(ErrorCodes.InvalidUserName, codes);
        Assert.Contains(ErrorCodes.AlreadyInList, codes);       // kid/KID and D:\X twice
        Assert.Contains(ErrorCodes.PathProtected, codes);
        Assert.Contains(ErrorCodes.WholeDriveNotAllowed, codes);
        Assert.Contains(ErrorCodes.InvalidProgramName, codes);
        Assert.Contains(ErrorCodes.InvalidFileHash, codes);
        Assert.Contains(ErrorCodes.PathInsideWindows, codes);
        Assert.Contains(ErrorCodes.NoRights, codes);
        Assert.Contains(ErrorCodes.InvalidExtensionId, codes);
        Assert.Contains(ErrorCodes.InvalidPolicyValue, codes);
        Assert.Equal(2, codes.Count(c => c == ErrorCodes.AlreadyInList));
    }

    [Fact]
    public void EnsureValid_throws_the_first_problem()
    {
        var c = new UcConfig { Users = [@"bad\name"] };
        Assert.Equal(ErrorCodes.InvalidUserName, Assert.Throws<UcException>(() => ConfigValidator.EnsureValid(c, Sp)).Code);
    }
}
