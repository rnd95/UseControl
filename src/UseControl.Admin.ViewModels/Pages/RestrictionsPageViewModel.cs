using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;

namespace UseControl.Admin.ViewModels.Pages;

/// <summary>
/// What restricted accounts may not do in Windows: tools, and places programs cannot start from.
/// Every switch is saved and applied as soon as it is clicked.
/// </summary>
public sealed partial class RestrictionsPageViewModel : PageViewModel
{
    private static readonly HashSet<string> Switches =
    [
        nameof(DisableCommandPrompt), nameof(DisableRegistryEditor), nameof(DisableTaskManager), nameof(DisableControlPanel),
        nameof(DisableRunDialog), nameof(DisableAutorun), nameof(DisablePasswordChange),
        nameof(SystemDriveRoot), nameof(WritableDriveRoots), nameof(OtherDriveLetters), nameof(RecycleBins),
        nameof(SystemWritableFolders), nameof(WritableProgramFolders),
    ];

    private bool _loading;

    public RestrictionsPageViewModel(PanelSession session) : base(session, "Ui.Nav.Restrictions", "") { }

    public override bool UsesPreset => true;

    [ObservableProperty] private bool _disableCommandPrompt;
    [ObservableProperty] private bool _disableRegistryEditor;
    [ObservableProperty] private bool _disableTaskManager;
    [ObservableProperty] private bool _disableControlPanel;
    [ObservableProperty] private bool _disableRunDialog;
    [ObservableProperty] private bool _disableAutorun;
    [ObservableProperty] private bool _disablePasswordChange;

    [ObservableProperty] private bool _systemDriveRoot;
    [ObservableProperty] private bool _writableDriveRoots;
    [ObservableProperty] private bool _otherDriveLetters;
    [ObservableProperty] private bool _recycleBins;
    [ObservableProperty] private bool _systemWritableFolders;
    [ObservableProperty] private bool _writableProgramFolders;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is { } name && Switches.Contains(name)) _ = Save();
    }

    private Task Save()
    {
        var restrictions = new AccountRestrictions
        {
            DisableCommandPrompt = DisableCommandPrompt,
            DisableRegistryEditor = DisableRegistryEditor,
            DisableTaskManager = DisableTaskManager,
            DisableControlPanel = DisableControlPanel,
            DisableRunDialog = DisableRunDialog,
            DisableAutorun = DisableAutorun,
            DisablePasswordChange = DisablePasswordChange,
        };
        var auto = new AutoBlockOptions
        {
            SystemDriveRoot = SystemDriveRoot,
            WritableDriveRoots = WritableDriveRoots,
            OtherDriveLetters = OtherDriveLetters,
            RecycleBins = RecycleBins,
            SystemWritableFolders = SystemWritableFolders,
            WritableProgramFolders = WritableProgramFolders,
        };
        return Session.EditPresetAsync(c =>
        {
            c.Restrictions = restrictions;
            c.Auto = auto;
        });
    }

    protected override void Rebuild()
    {
        var c = Session.Preset;
        if (c is null) return;
        _loading = true;
        try
        {
            DisableCommandPrompt = c.Restrictions.DisableCommandPrompt;
            DisableRegistryEditor = c.Restrictions.DisableRegistryEditor;
            DisableTaskManager = c.Restrictions.DisableTaskManager;
            DisableControlPanel = c.Restrictions.DisableControlPanel;
            DisableRunDialog = c.Restrictions.DisableRunDialog;
            DisableAutorun = c.Restrictions.DisableAutorun;
            DisablePasswordChange = c.Restrictions.DisablePasswordChange;
            SystemDriveRoot = c.Auto.SystemDriveRoot;
            WritableDriveRoots = c.Auto.WritableDriveRoots;
            OtherDriveLetters = c.Auto.OtherDriveLetters;
            RecycleBins = c.Auto.RecycleBins;
            SystemWritableFolders = c.Auto.SystemWritableFolders;
            WritableProgramFolders = c.Auto.WritableProgramFolders;
        }
        finally
        {
            _loading = false;
        }
    }
}
