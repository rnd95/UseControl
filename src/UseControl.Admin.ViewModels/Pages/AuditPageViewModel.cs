using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Localization;
using UseControl.Windows.Audit;

namespace UseControl.Admin.ViewModels.Pages;

public sealed record AuditRow(AuditStatus Status, string StatusText, string Name, string Detail);

public sealed partial class AuditPageViewModel : PageViewModel
{
    private IReadOnlyList<AuditCheck> _checks = [];

    public AuditPageViewModel(PanelSession session) : base(session, "Ui.Nav.Audit", "") => _summary = Loc.T("Ui.Audit.NotRun");

    public ObservableCollection<AuditRow> Checks { get; } = [];

    [ObservableProperty]
    private string _summary;

    // What the check expects on this PC; one entry per line.
    [ObservableProperty] private string _admins = "";
    [ObservableProperty] private string _privateFolders = "";
    [ObservableProperty] private string _hiddenAccounts = "";
    [ObservableProperty] private string _knownRemoteServices = "";

    [RelayCommand]
    private Task Run() => Session.RunAsync(async () =>
    {
        _checks = await Session.Client.AuditAsync().ConfigureAwait(true);
        BuildRows();
    });

    [RelayCommand]
    private Task SaveExpectations()
    {
        var admins = Lines(Admins);
        var priv = Lines(PrivateFolders);
        var hidden = Lines(HiddenAccounts);
        var remote = Lines(KnownRemoteServices);
        return Session.EditAsync(c =>
        {
            c.Audit.Admins = admins;
            c.Audit.PrivateFolders = priv;
            c.Audit.HiddenAccounts = hidden;
            c.Audit.KnownRemoteServices = remote;
        });
    }

    internal static List<string> Lines(string text) =>
        [.. text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    private void BuildRows()
    {
        Replace(Checks, _checks
            .OrderByDescending(c => c.Status)
            .Select(c => new AuditRow(
                c.Status,
                Loc.T("Audit." + c.Status),
                (Loc.Has("Audit." + c.Id) ? Loc.T("Audit." + c.Id) : c.Id) + (c.Subject is null ? "" : ": " + c.Subject),
                c.LocalizedDetail())));
        Summary = _checks.Count == 0 ? Loc.T("Ui.Audit.NotRun")
            : Loc.T("Cli.AuditSummary", _checks.Count, _checks.Count(c => c.Status == AuditStatus.Fail), _checks.Count(c => c.Status == AuditStatus.Warning));
    }

    protected override void Rebuild()
    {
        BuildRows();
        var c = Session.Config;
        if (c is null) return;
        Admins = string.Join(Environment.NewLine, c.Audit.Admins);
        PrivateFolders = string.Join(Environment.NewLine, c.Audit.PrivateFolders);
        HiddenAccounts = string.Join(Environment.NewLine, c.Audit.HiddenAccounts);
        KnownRemoteServices = string.Join(Environment.NewLine, c.Audit.KnownRemoteServices);
    }
}
