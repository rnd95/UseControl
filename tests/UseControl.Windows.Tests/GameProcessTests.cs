using UseControl.Core.Configuration;
using UseControl.Windows.Games;

namespace UseControl.Windows.Tests;

public class GameProcessTests
{
    private static Preset Preset()
    {
        var p = new Preset { Name = "kid" };
        p.BlockedPaths.Add(new PathEntry { Path = @"D:\Games", Category = RuleCategory.Games });
        p.BlockedPaths.Add(new PathEntry { Path = @"C:\Program Files\WireGuard", Category = RuleCategory.Vpn });
        p.BlockedNames.Add(new NameEntry { Name = "steam.exe", Category = RuleCategory.Games });
        p.BlockedNames.Add(new NameEntry { Name = "cmd.exe", Category = RuleCategory.Tools });
        return p;
    }

    private static string? NoHash(string path, long size) => null;

    [Fact]
    public void Games_are_recognised_by_folder_and_name_but_other_categories_are_not()
    {
        var m = new GameMatcher(Preset());
        Assert.True(m.Matches(@"D:\Games\Racer\racer.exe", NoHash));
        Assert.True(m.Matches(@"E:\Somewhere\Steam.exe", NoHash));
        Assert.False(m.Matches(@"C:\Program Files\WireGuard\wireguard.exe", NoHash));
        Assert.False(m.Matches(@"C:\Windows\System32\cmd.exe", NoHash));
        Assert.False(m.Matches(@"D:\GamesOther\x.exe", NoHash));
    }

    [Fact]
    public void Games_blocked_by_fingerprint_are_recognised_whatever_their_name()
    {
        var file = Path.Combine(Path.GetTempPath(), $"uc-game-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(file, [1, 2, 3, 4, 5]);
        try
        {
            var p = new Preset { Name = "kid" };
            p.BlockedFiles.Add(new FileHashEntry { FileName = "original.exe", Sha256 = new string('A', 64), Md5 = new string('B', 32), Size = 5, Category = RuleCategory.Games });
            var m = new GameMatcher(p);
            Assert.True(m.Matches(file, (_, _) => new string('a', 64)));
            Assert.False(m.Matches(file, (_, _) => new string('C', 64)));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_preset_without_games_matches_nothing()
    {
        Assert.True(new GameMatcher(new Preset { Name = "x" }).IsEmpty);
        Assert.Empty(new GameProcesses().Find(new Dictionary<string, (string, Preset)> { ["kid"] = ("S-1-5-21-1-2-3-1001", new Preset { Name = "x" }) }));
    }
}
