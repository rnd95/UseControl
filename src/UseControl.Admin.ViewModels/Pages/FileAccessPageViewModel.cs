using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

public sealed record AccessRow(string Path, FileAccessMode ModeValue, string Mode, string Rights, string Users, bool IsDeny);

public sealed partial class FileAccessPageViewModel : PageViewModel
{
    public FileAccessPageViewModel(PanelSession session) : base(session, "Ui.Nav.FileAccess", "") { }

    public ObservableCollection<AccessRow> Rules { get; } = [];

    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private bool _deny = true;

    [ObservableProperty]
    private bool _read = true;

    [ObservableProperty]
    private bool _write = true;

    [ObservableProperty]
    private bool _execute = true;

    [ObservableProperty]
    private bool _delete = true;

    /// <summary>Comma-separated accounts; empty = every restricted account.</summary>
    [ObservableProperty]
    private string _users = "";

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

    [RelayCommand]
    private async Task Add()
    {
        var rights = (Read ? FileAccessRights.Read : 0) | (Write ? FileAccessRights.Write : 0) | (Execute ? FileAccessRights.Execute : 0) | (Delete ? FileAccessRights.Delete : 0);
        var mode = Deny ? FileAccessMode.Deny : FileAccessMode.Allow;
        var path = Path;
        var users = Users.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (await Session.EditAsync(c => ConfigEditor.SetFileAccess(c, path, mode, rights, users, Session.Paths)).ConfigureAwait(true)) Path = "";
    }

    [RelayCommand]
    private Task Remove(AccessRow? row) => row is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.RemoveFileAccess(c, row.Path, row.ModeValue));

    public static string RightsText(FileAccessRights r)
    {
        if (r == FileAccessRights.Full) return Loc.T("Ui.Access.Full");
        var parts = new List<string>();
        if (r.HasFlag(FileAccessRights.Read)) parts.Add(Loc.T("Ui.Access.Read"));
        if (r.HasFlag(FileAccessRights.Write)) parts.Add(Loc.T("Ui.Access.Write"));
        if (r.HasFlag(FileAccessRights.Execute)) parts.Add(Loc.T("Ui.Access.Execute"));
        if (r.HasFlag(FileAccessRights.Delete)) parts.Add(Loc.T("Ui.Access.Delete"));
        return string.Join(", ", parts);
    }

    protected override void Rebuild()
    {
        var c = Session.Config;
        if (c is null) return;
        Replace(Rules, c.FileAccess.Select(r => new AccessRow(
            r.Path,
            r.Mode,
            Loc.T(r.Mode == FileAccessMode.Deny ? "Ui.Access.Deny" : "Ui.Access.Allow"),
            RightsText(r.Rights),
            r.Users.Count == 0 ? Loc.T("Ui.Access.AllAccounts") : string.Join(", ", r.Users),
            r.Mode == FileAccessMode.Deny)));
    }
}
