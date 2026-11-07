using UseControl.Core.Configuration;
using UseControl.Core.Updates;

namespace UseControl.Core.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V0.10.0", "0.10.0")]
    public void Final_release_tags_are_versions(string tag, string expected) =>
        Assert.Equal(expected, ReleaseVersion.Format(ReleaseVersion.Parse(tag)!));

    [Theory]
    [InlineData("v1.2.3-beta")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not(string? tag) => Assert.Null(ReleaseVersion.Parse(tag));

    [Fact]
    public void Newer_is_compared_by_numbers_not_text()
    {
        Assert.True(ReleaseVersion.IsNewer("0.10.0", "0.9.9"));
        Assert.False(ReleaseVersion.IsNewer("0.1.0", "0.1.0"));
        Assert.False(ReleaseVersion.IsNewer("0.0.9", "0.1.0"));
        Assert.False(ReleaseVersion.IsNewer("garbage", "0.1.0"));
    }

    [Fact]
    public void Checksum_files_are_read_in_both_formats()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var sums = $"{a}  UseControl-1.0.0-x64.msi\r\n{b} *UseControl-1.0.0-x64.zip\n";
        Assert.Equal(a.ToUpperInvariant(), ReleaseVersion.HashFor(sums, "UseControl-1.0.0-x64.msi"));
        Assert.Equal(b, ReleaseVersion.HashFor(sums, "usecontrol-1.0.0-x64.zip"));
        Assert.Null(ReleaseVersion.HashFor(sums, "other.msi"));
        Assert.Null(ReleaseVersion.HashFor("not a hash  UseControl-1.0.0-x64.msi", "UseControl-1.0.0-x64.msi"));
        Assert.Equal("UseControl-1.0.0-x64.msi", ReleaseVersion.MsiName("1.0.0"));
    }

    [Fact]
    public void Update_settings_default_to_a_daily_check_without_automatic_installation()
    {
        var c = ConfigSerializer.Parse("""{ "schemaVersion": 2, "updates": null }""");
        Assert.True(c.Updates.CheckDaily);
        Assert.False(c.Updates.InstallAutomatically);
    }
}
