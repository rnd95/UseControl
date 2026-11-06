using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Rules;
using UseControl.Windows;
using UseControl.Windows.Audit;
using UseControl.Windows.Games;
using UseControl.Windows.Restrictions;

namespace UseControl.Service;

/// <summary>What the service needs from Windows. An interface so the controller can be tested without changing the PC.</summary>
public interface IEngine
{
    ApplyResult Apply(UcConfig config, Func<string, bool> gamesAllowed);

    AccountResult RemoveAccount(string user);

    ApplyResult RemoveAll(UcConfig config);

    PreviewData Preview(UcConfig config, Func<string, bool> gamesAllowed);

    IReadOnlyDictionary<string, RestrictionState?> ReadStates(UcConfig config);

    IReadOnlyList<AuditCheck> Audit(UcConfig config);

    IReadOnlyList<string> FindGames();

    bool StoreInstallsAdminOnly();

    /// <summary>The games restricted accounts run now.</summary>
    IReadOnlyList<RunningGame> RunningGames(UcConfig config);

    int CloseGames(IReadOnlyList<RunningGame> games);

    /// <summary>A message on the screen of the account running <paramref name="game"/>.</summary>
    void Notify(RunningGame game, string key, params object?[] args);
}

public sealed record PreviewData(IReadOnlyList<AccountPreview> Accounts, IReadOnlyList<AutoPath> AutoPaths);

/// <summary>The real engine: <see cref="RestrictionEngine"/> on this PC.</summary>
public sealed class WindowsEngine(string dataDirectory) : IEngine
{
    private readonly RestrictionEngine _engine = new(WindowsSystemPaths.Current, dataDirectory);

    private readonly GameProcesses _games = new();

    public ApplyResult Apply(UcConfig config, Func<string, bool> gamesAllowed) => _engine.Apply(config, gamesAllowed);

    public AccountResult RemoveAccount(string user) => RestrictionEngine.RemoveAccount(user);

    public ApplyResult RemoveAll(UcConfig config) => _engine.RemoveAll(config);

    public PreviewData Preview(UcConfig config, Func<string, bool> gamesAllowed)
    {
        var accounts = _engine.Preview(config, gamesAllowed, out var auto);
        return new PreviewData(accounts, auto);
    }

    public IReadOnlyDictionary<string, RestrictionState?> ReadStates(UcConfig config) => RestrictionEngine.ReadStates(config);

    public IReadOnlyList<AuditCheck> Audit(UcConfig config) => _engine.Audit(config);

    public IReadOnlyList<string> FindGames() => _engine.FindGames();

    public bool StoreInstallsAdminOnly() => MachinePolicies.StoreInstallsAdminOnly();

    public IReadOnlyList<RunningGame> RunningGames(UcConfig config) => _games.Find(RestrictionEngine.RestrictedAccounts(config));

    public int CloseGames(IReadOnlyList<RunningGame> games) => GameProcesses.Close(games);

    public void Notify(RunningGame game, string key, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(game);
        SessionMessages.Show(game.SessionId, game.Sid, key, 60, args);
    }
}
