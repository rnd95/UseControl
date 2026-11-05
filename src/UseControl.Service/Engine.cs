using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Rules;
using UseControl.Windows;
using UseControl.Windows.Audit;
using UseControl.Windows.Restrictions;

namespace UseControl.Service;

/// <summary>What the service needs from Windows. An interface so the controller can be tested without changing the PC.</summary>
public interface IEngine
{
    ApplyResult Apply(UcConfig config, bool gamesAllowed);

    AccountResult RemoveAccount(string user);

    ApplyResult RemoveAll(UcConfig config);

    PreviewData Preview(UcConfig config, bool gamesAllowed);

    IReadOnlyDictionary<string, RestrictionState?> ReadStates(UcConfig config);

    IReadOnlyList<AuditCheck> Audit(UcConfig config);

    IReadOnlyList<string> FindNewGames(UcConfig config);

    bool StoreInstallsAdminOnly();
}

public sealed record PreviewData(IReadOnlyList<AccountPreview> Accounts, IReadOnlyList<AutoPath> AutoPaths);

/// <summary>The real engine: <see cref="RestrictionEngine"/> on this PC.</summary>
public sealed class WindowsEngine(string dataDirectory) : IEngine
{
    private readonly RestrictionEngine _engine = new(WindowsSystemPaths.Current, dataDirectory);

    public ApplyResult Apply(UcConfig config, bool gamesAllowed) => _engine.Apply(config, gamesAllowed);

    public AccountResult RemoveAccount(string user) => RestrictionEngine.RemoveAccount(user);

    public ApplyResult RemoveAll(UcConfig config) => _engine.RemoveAll(config);

    public PreviewData Preview(UcConfig config, bool gamesAllowed)
    {
        var accounts = _engine.Preview(config, gamesAllowed, out var auto);
        return new PreviewData(accounts, auto);
    }

    public IReadOnlyDictionary<string, RestrictionState?> ReadStates(UcConfig config) => RestrictionEngine.ReadStates(config);

    public IReadOnlyList<AuditCheck> Audit(UcConfig config) => _engine.Audit(config);

    public IReadOnlyList<string> FindNewGames(UcConfig config) => _engine.FindNewGames(config);

    public bool StoreInstallsAdminOnly() => MachinePolicies.StoreInstallsAdminOnly();
}
