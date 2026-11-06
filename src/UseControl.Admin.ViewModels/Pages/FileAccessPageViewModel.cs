using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

public sealed record AccessRow(string Path, FileAccessMode ModeValue, string Mode, string Rights, string Users, bool IsDeny);

/// <summary>A ready-made kind of access, instead of combining modes and rights by hand.</summary>
public sealed record AccessPreset(string Key, FileAccessMode Mode, FileAccessRights Rights)
{
    public string Text => Loc.T("Ui.Access.Preset." + Key);
}

public sealed partial class AccountChoice(string name, bool isSelected) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected = isSelected;
}

public sealed partial class FileAccessPageViewModel : PageViewModel
{
    public FileAccessPageViewModel(PanelSession session) : base(session, "Ui.Nav.FileAccess", "")
    {
        _preset = Presets[0];
    }

    public static IReadOnlyList<AccessPreset> Presets { get; } =
    [
        new("NoAccess", FileAccessMode.Deny, FileAccessRights.Full),
        new("ReadOnly", FileAccessMode.Deny, FileAccessRights.Write | FileAccessRights.Delete),
        new("NoRun", FileAccessMode.Deny, FileAccessRights.Execute),
        new("FullAllow", FileAccessMode.Allow, FileAccessRights.Full),
    ];

    public ObservableCollection<AccessRow> Rules { get; } = [];

    /// <summary>The restricted accounts; all ticked means "every restricted account" (also future ones).</summary>
    public ObservableCollection<AccountChoice> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _path = "";

    [ObservableProperty]
    private AccessPreset _preset;

    private bool HasPath() => !string.IsNullOrWhiteSpace(Path);

    [RelayCommand]
    private void PickFolder()
    {
        if (Session.Dialogs.PickFolder(Loc.T("Ui.Access.PickFolder")) is { } p) Path = p;
    }

    [RelayCommand]
    private void PickFile()
    {
        if (Session.Dialogs.PickFile(Loc.T("Ui.Access.PickFile"), "*.*|*.*") is { } p) Path = p;
    }

    [RelayCommand(CanExecute = nameof(HasPath))]
    private async Task Add()
    {
        var path = Path;
        var preset = Preset;
        var chosen = Accounts.Where(a => a.IsSelected).Select(a => a.Name).ToList();
        // Every account ticked = the rule follows the list of restricted accounts.
        var users = chosen.Count == Accounts.Count ? [] : chosen;
        if (await Session.EditAsync(c => ConfigEditor.SetFileAccess(c, path, preset.Mode, preset.Rights, users, Session.Paths)).ConfigureAwait(true)) Path = "";
    }

    [RelayCommand]
    private Task Remove(AccessRow? row) => row is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.RemoveFileAccess(c, row.Path, row.ModeValue));

    public static string RightsText(FileAccessMode mode, FileAccessRights r)
    {
        var preset = Presets.FirstOrDefault(p => p.Mode == mode && p.Rights == r);
        if (preset is not null) return preset.Text;
        if (r == FileAccessRights.Full) return Loc.T("Ui.Access.Full");
        var parts = new List<string>();
        if (r.HasFlag(FileAccessRights.Read)) parts.Add(Loc.T("Ui.Access.Read"));
        if (r.HasFlag(FileAccessRights.Write)) parts.Add(Loc.T("Ui.Access.Write"));
        if (r.HasFlag(FileAccessRights.Execute)) parts.Add(Loc.T("Ui.Access.Execute"));
        if (r.HasFlag(FileAccessRights.Delete)) parts.Add(Loc.T("Ui.Access.Delete"));
        return Loc.T(mode == FileAccessMode.Deny ? "Ui.Access.Deny" : "Ui.Access.Allow") + ": " + string.Join(", ", parts);
    }

    protected override void Rebuild()
    {
        var c = Session.Config;
        if (c is null) return;
        OnPropertyChanged(nameof(Presets));
        Replace(Accounts, c.Users.Select(u => new AccountChoice(u, true)));
        Replace(Rules, c.FileAccess.Select(r => new AccessRow(
            r.Path,
            r.Mode,
            Loc.T(r.Mode == FileAccessMode.Deny ? "Ui.Access.Deny" : "Ui.Access.Allow"),
            RightsText(r.Mode, r.Rights),
            r.Users.Count == 0 ? Loc.T("Ui.Access.AllAccounts") : string.Join(", ", r.Users),
            r.Mode == FileAccessMode.Deny)));
    }
}
