using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

public sealed partial class AccountsPageViewModel : PageViewModel
{
    private readonly Func<IReadOnlyList<string>> _standardAccounts;

    public AccountsPageViewModel(PanelSession session, Func<IReadOnlyList<string>> standardAccounts)
        : base(session, "Ui.Nav.Accounts", "")
    {
        _standardAccounts = standardAccounts;
    }

    public ObservableCollection<string> Restricted { get; } = [];

    /// <summary>Standard accounts on this PC that are not restricted yet.</summary>
    public ObservableCollection<string> Candidates { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string? _selectedCandidate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private string? _selectedRestricted;

    private bool CanAdd() => !string.IsNullOrEmpty(SelectedCandidate);

    private bool CanRemove() => !string.IsNullOrEmpty(SelectedRestricted);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task Add()
    {
        var name = SelectedCandidate!;
        return Session.EditAsync(c => ConfigEditor.AddUser(c, name));
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task Remove()
    {
        var name = SelectedRestricted!;
        if (!Session.Dialogs.Confirm(Loc.T("Ui.Accounts.ConfirmRemove", name))) return;
        await Session.EditAsync(c => ConfigEditor.RemoveUser(c, name)).ConfigureAwait(true);
    }

    protected override void Rebuild()
    {
        var users = Session.Config?.Users ?? [];
        Replace(Restricted, users);
        IReadOnlyList<string> all;
        try
        {
            all = _standardAccounts();
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            all = [];
        }
        Replace(Candidates, all.Where(a => !users.Contains(a, StringComparer.OrdinalIgnoreCase)));
    }
}
