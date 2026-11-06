using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;

namespace UseControl.Admin.ViewModels.Pages;

/// <summary>What restricted accounts may not do: Windows tools, installing, places blocked automatically.</summary>
public sealed partial class RestrictionsPageViewModel : PageViewModel
{
    public RestrictionsPageViewModel(PanelSession session) : base(session, "Ui.Nav.Restrictions", "") { }

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

    /// <summary>Exceptions: program folders that run although they are inside a blocked place.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ProgramRow> AllowedFolders { get; } = [];

    [RelayCommand]
    private Task AllowFolder()
    {
        var path = Session.Dialogs.PickFolder(Localization.Loc.T("Ui.Programs.PickAllowFolder"));
        return path is null ? Task.CompletedTask : Session.EditPresetAsync(c => ConfigEditor.AddAllowedPath(c, path));
    }

    [RelayCommand]
    private Task RemoveAllowedFolder(ProgramRow? row) => row is null ? Task.CompletedTask : Session.EditPresetAsync(c => ConfigEditor.RemoveAllowedPath(c, row.Key));

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
        return Session.EditPresetAsync(c =>
        {
            c.Restrictions = restrictions;
            c.Auto = auto;
        });
    }

    protected override void Rebuild()
    {
        var c = Session.Preset;
        if (c is null)
        {
            AllowedFolders.Clear();
            return;
        }
        Replace(AllowedFolders, c.AllowedPaths.Select(e => new ProgramRow(e.Path, e.Path, "", e.Note)));
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
}
