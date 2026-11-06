using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

/// <summary>One account in the list: restricted or not, with the one action that makes sense for it.</summary>
public sealed record AccountRow(string Name, bool IsRestricted, bool Exists, string StatusText);

/// <summary>
/// Every standard account of the PC in one list, each with its own button: no selection needed.
/// Accounts listed in the configuration but missing on the PC are shown too, so they can be removed.
/// </summary>
public sealed partial class AccountsPageViewModel : PageViewModel
{
    private readonly Func<IReadOnlyList<string>> _standardAccounts;

    public AccountsPageViewModel(PanelSession session, Func<IReadOnlyList<string>> standardAccounts)
        : base(session, "Ui.Nav.Accounts", "")
    {
        _standardAccounts = standardAccounts;
    }

    public ObservableCollection<AccountRow> Accounts { get; } = [];

    [RelayCommand]
    private Task Restrict(AccountRow? row) =>
        row is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.AddUser(c, row.Name));

    [RelayCommand]
    private async Task Unrestrict(AccountRow? row)
    {
        if (row is null || !Session.Dialogs.Confirm(Loc.T("Ui.Accounts.ConfirmRemove", row.Name))) return;
        await Session.EditAsync(c => ConfigEditor.RemoveUser(c, row.Name)).ConfigureAwait(true);
    }

    protected override void Rebuild()
    {
        var restricted = Session.Config?.Users ?? [];
        IReadOnlyList<string> onPc;
        try
        {
            onPc = _standardAccounts();
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            onPc = [];
        }

        var rows = new List<AccountRow>();
        foreach (var name in onPc.Concat(restricted).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var isRestricted = restricted.Contains(name, StringComparer.OrdinalIgnoreCase);
            var exists = onPc.Contains(name, StringComparer.OrdinalIgnoreCase);
            var status = !exists ? Loc.T("Ui.Accounts.Missing") : Loc.T(isRestricted ? "Ui.Accounts.IsRestricted" : "Ui.Accounts.NotRestricted");
            rows.Add(new AccountRow(name, isRestricted, exists, status));
        }
        // Restricted accounts first: they are the ones the administrator looks after.
        Replace(Accounts, rows.OrderByDescending(r => r.IsRestricted));
    }
}
