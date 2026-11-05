using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;

namespace UseControl.Admin.ViewModels.Pages;

/// <summary>What restricted accounts may not do: Windows tools, installing, places blocked automatically.</summary>
public sealed partial class RestrictionsPageViewModel : PageViewModel
{
    public RestrictionsPageViewModel(PanelSession session) : base(session, "Ui.Nav.Restrictions", "") { }

    [ObservableProperty] private bool _disableCommandPrompt;
    [ObservableProperty] private bool _disableRegistryEditor;
    [ObservableProperty] private bool _disableTaskManager;
    [ObservableProperty] private bool _disableControlPanel;
    [ObservableProperty] private bool _disableRunDialog;
    [ObservableProperty] private bool _disableAutorun;
    [ObservableProperty] private bool _disablePasswordChange;

    [ObservableProperty] private bool _blockStoreInstalls;
    [ObservableProperty] private bool _systemDriveRoot;
    [ObservableProperty] private bool _writableDriveRoots;
    [ObservableProperty] private bool _otherDriveLetters;
    [ObservableProperty] private bool _recycleBins;
    [ObservableProperty] private bool _systemWritableFolders;
    [ObservableProperty] private bool _writableProgramFolders;

    [RelayCommand]
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
        var store = BlockStoreInstalls;
        return Session.EditAsync(c =>
        {
            c.Restrictions = restrictions;
            c.Auto = auto;
            c.BlockStoreInstalls = store;
        });
    }

    protected override void Rebuild()
    {
        var c = Session.Config;
        if (c is null) return;
        DisableCommandPrompt = c.Restrictions.DisableCommandPrompt;
        DisableRegistryEditor = c.Restrictions.DisableRegistryEditor;
        DisableTaskManager = c.Restrictions.DisableTaskManager;
        DisableControlPanel = c.Restrictions.DisableControlPanel;
        DisableRunDialog = c.Restrictions.DisableRunDialog;
        DisableAutorun = c.Restrictions.DisableAutorun;
        DisablePasswordChange = c.Restrictions.DisablePasswordChange;
        BlockStoreInstalls = c.BlockStoreInstalls;
        SystemDriveRoot = c.Auto.SystemDriveRoot;
        WritableDriveRoots = c.Auto.WritableDriveRoots;
        OtherDriveLetters = c.Auto.OtherDriveLetters;
        RecycleBins = c.Auto.RecycleBins;
        SystemWritableFolders = c.Auto.SystemWritableFolders;
        WritableProgramFolders = c.Auto.WritableProgramFolders;
    }
}
