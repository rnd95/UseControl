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
    private string _summary = "";

    [RelayCommand]
    private Task Run() => Session.RunAsync(async () =>
    {
        _checks = await Session.Client.AuditAsync().ConfigureAwait(true);
        Rebuild();
    });

    protected override void Rebuild()
    {
        Replace(Checks, _checks
            .OrderByDescending(c => c.Status)
            .Select(c => new AuditRow(
                c.Status,
                Loc.T("Audit." + c.Status),
                (Loc.Has("Audit." + c.Id) ? Loc.T("Audit." + c.Id) : c.Id) + (c.Subject is null ? "" : ": " + c.Subject),
                c.Detail)));
        Summary = _checks.Count == 0 ? Loc.T("Ui.Audit.NotRun")
            : Loc.T("Cli.AuditSummary", _checks.Count, _checks.Count(c => c.Status == AuditStatus.Fail), _checks.Count(c => c.Status == AuditStatus.Warning));
    }
}
