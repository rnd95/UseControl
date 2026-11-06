using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;

namespace UseControl.Admin.ViewModels.Pages;

/// <summary>Blocked programs other than games (VPNs, system tools, anything else).</summary>
public sealed class ProgramsPageViewModel : PageViewModel
{
    public ProgramsPageViewModel(PanelSession session, Func<string, RuleCategory, FileHashEntry> fingerprint)
        : base(session, "Ui.Nav.Programs", "")
    {
        List = new BlockListViewModel(session, c => c != RuleCategory.Games && c != RuleCategory.Writable, RuleCategory.Other,
            fingerprint, "Ui.Programs.PickBlockFile", "Ui.Programs.PickBlockFolder");
    }

    public BlockListViewModel List { get; }

    public override bool UsesPreset => true;

    protected override void Rebuild() => List.Rebuild(Session.Preset);
}
