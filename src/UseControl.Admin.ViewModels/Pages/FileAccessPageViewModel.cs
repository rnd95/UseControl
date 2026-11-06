using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core;
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

/// <summary>
/// Access of restricted accounts to private files and folders (Windows permissions). Whether programs
/// may start from a folder is decided on the "Apps and games" page, not here.
/// </summary>
public sealed partial class FileAccessPageViewModel : PageViewModel
{
    public FileAccessPageViewModel(PanelSession session) : base(session, "Ui.Nav.FileAccess", "")
    {
        _access = Choices[0];
    }

    public override NavSection Section => NavSection.Computer;

    /// <summary>Every kind of access a rule can have (for the texts of existing rules).</summary>
    private static readonly AccessPreset[] All =
    [
        new("NoAccess", FileAccessMode.Deny, FileAccessRights.Full),
        new("ReadOnly", FileAccessMode.Deny, FileAccessRights.Write | FileAccessRights.Delete),
        new("NoRun", FileAccessMode.Deny, FileAccessRights.Execute),
        new("FullAllow", FileAccessMode.Allow, FileAccessRights.Full),
    ];

    /// <summary>The kinds of access offered for new rules. Blocking programs is done by the profile, so "no run" is not offered.</summary>
    public static IReadOnlyList<AccessPreset> Choices { get; } = [.. All.Where(p => p.Key != "NoRun")];

    public ObservableCollection<AccessRow> Rules { get; } = [];

    /// <summary>The restricted accounts; all ticked means "every restricted account" (also future ones).</summary>
    public ObservableCollection<AccountChoice> Accounts { get; } = [];

    [ObservableProperty]
    private AccessPreset _access;

    [RelayCommand]
    private Task AddFolders() => Add(Session.Dialogs.PickFolders(Loc.T("Ui.Access.PickFolders")));

    [RelayCommand]
    private Task AddFiles() => Add(Session.Dialogs.PickFiles(Loc.T("Ui.Access.PickFiles"), "*.*|*.*"));

    /// <summary>One rule per chosen file or folder, with the chosen access, for the ticked accounts.</summary>
    private Task Add(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return Task.CompletedTask;
        var access = Access;
        var chosen = Accounts.Where(a => a.IsSelected).Select(a => a.Name).ToList();
        if (chosen.Count == 0)
        {
            Session.ShowMessage(MessageKind.Info, Loc.T("Ui.Access.ChooseAccounts"));
            return Task.CompletedTask;
        }
        // Every account ticked = the rule follows the list of restricted accounts.
        var users = chosen.Count == Accounts.Count ? [] : chosen;
        return Session.EditAsync(c =>
        {
            foreach (var p in paths) ConfigEditor.SetFileAccess(c, p, access.Mode, access.Rights, users, Session.Paths);
        });
    }

    [RelayCommand]
    private Task Remove(AccessRow? row) => row is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.RemoveFileAccess(c, row.Path, row.ModeValue));

    public static string RightsText(FileAccessMode mode, FileAccessRights r)
    {
        var preset = All.FirstOrDefault(p => p.Mode == mode && p.Rights == r);
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
        OnPropertyChanged(nameof(Choices));
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
