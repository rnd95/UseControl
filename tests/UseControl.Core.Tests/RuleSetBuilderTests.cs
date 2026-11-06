using UseControl.Core.Configuration;
using UseControl.Core.Rules;

namespace UseControl.Core.Tests;

public class RuleSetBuilderTests
{
    private static readonly AutoPath[] Auto =
    [
        new(@"C:\", "system drive root"),
        new(@"D:\", "writable drive"),
        new(@"C:\Program Files (x86)\Steam", "writable program folder"),
    ];

    private static readonly string[] SystemAllowed = [@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)"];

    private static Preset Config()
    {
        var c = new Preset { Name = "kid", Accounts = ["kid"] };
        c.BlockedPaths.Add(new PathEntry { Path = @"D:\games", Category = RuleCategory.Games });
        c.BlockedPaths.Add(new PathEntry { Path = @"C:\Program Files (x86)\Steam", Category = RuleCategory.Games });
        c.BlockedPaths.Add(new PathEntry { Path = @"C:\Program Files\WireGuard", Category = RuleCategory.Vpn });
        c.AllowedPaths.Add(new AllowedPathEntry { Path = @"C:\Tools" });
        c.BlockedNames.Add(new NameEntry { Name = "steam.exe", Category = RuleCategory.Games });
        c.BlockedNames.Add(new NameEntry { Name = "cmd.exe", Category = RuleCategory.Tools });
        c.BlockedFiles.Add(new FileHashEntry { FileName = "game.exe", Category = RuleCategory.Games, Sha256 = "A", Md5 = "B", Size = 1 });
        return c;
    }

    private static RuleSet Build(bool games) => RuleSetBuilder.Build(Config(), @"C:\Users\kid", Auto, SystemAllowed, games);

    private static string[] Paths(IEnumerable<PathRule> r) => [.. r.Select(x => x.Path)];

    [Fact]
    public void Blocks_the_profile_first_then_automatic_places_and_the_config_list()
    {
        var r = Build(games: false);
        Assert.Equal(new PathRule(@"C:\Users\kid", RuleSource.Profile), r.Blocked[0]);
        Assert.Contains(@"D:\", Paths(r.Blocked));
        Assert.Contains(@"D:\games", Paths(r.Blocked));
        Assert.Contains(@"C:\Program Files\WireGuard", Paths(r.Blocked));
    }

    [Fact]
    public void Lists_each_path_once()
    {
        var r = Build(games: false);
        Assert.Single(r.Blocked, x => x.Path == @"C:\Program Files (x86)\Steam");
    }

    [Fact]
    public void Keeps_Windows_Program_Files_and_allowed_folders_allowed()
    {
        var a = Paths(Build(games: false).Allowed);
        Assert.Contains(@"C:\Windows", a);
        Assert.Contains(@"C:\Program Files (x86)", a);
        Assert.Contains(@"C:\Tools", a);
        Assert.DoesNotContain(@"D:\games", a);
    }

    [Fact]
    public void Games_time_allows_game_folders_even_inside_blocked_or_writable_places()
    {
        var r = Build(games: true);
        var a = Paths(r.Allowed);
        var b = Paths(r.Blocked);
        Assert.Contains(@"D:\games", a);
        Assert.Contains(@"C:\Program Files (x86)\Steam", a);
        Assert.DoesNotContain(@"C:\Program Files (x86)\Steam", b);
        Assert.Contains(@"D:\", b);
        Assert.Contains(@"C:\Program Files\WireGuard", b);
    }

    [Fact]
    public void Games_time_stops_blocking_game_names_and_files_only()
    {
        Assert.Contains("steam.exe", Build(games: false).BlockedNames);
        Assert.Single(Build(games: false).BlockedFiles);
        var g = Build(games: true);
        Assert.DoesNotContain("steam.exe", g.BlockedNames);
        Assert.Contains("cmd.exe", g.BlockedNames);
        Assert.Empty(g.BlockedFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Never_allows_and_blocks_the_same_path(bool games)
    {
        var r = Build(games);
        var blocked = new HashSet<string>(Paths(r.Blocked), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(r.Allowed, a => blocked.Contains(a.Path));
    }

    [Fact]
    public void Works_without_a_profile_and_automatic_places()
    {
        var r = RuleSetBuilder.Build(Config(), null, [], [], gamesAllowed: false);
        Assert.Equal(3, r.Blocked.Count);
        Assert.Single(r.Allowed);
    }
}
