using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

/// <summary>A preset an account can get. <see cref="Name"/> null: a new preset of its own, named after the account.</summary>
public sealed record PresetChoice(string? Name, string Text);

/// <summary>One account in the list: restricted or not, its preset, and the one action that makes sense for it.</summary>
public sealed partial class AccountRow : ObservableObject
{
    private readonly Action<AccountRow, PresetChoice>? _moved;

    public AccountRow(string name, bool isRestricted, bool exists, string statusText, IReadOnlyList<PresetChoice> presets, PresetChoice preset, Action<AccountRow, PresetChoice>? moved)
    {
        Name = name;
        IsRestricted = isRestricted;
        Exists = exists;
        StatusText = statusText;
        Presets = presets;
        _preset = preset;
        _moved = moved;
    }

    public string Name { get; }

    public bool IsRestricted { get; }

    public bool Exists { get; }

    public string StatusText { get; }

    public IReadOnlyList<PresetChoice> Presets { get; }

    /// <summary>For a restricted account its preset (changing it moves the account); otherwise the preset it will get.</summary>
    [ObservableProperty]
    private PresetChoice _preset;

    partial void OnPresetChanged(PresetChoice value)
    {
        if (IsRestricted) _moved?.Invoke(this, value);
    }
}

/// <summary>
/// Every standard account of the PC in one list, each with its own button and preset: no selection needed.
/// Accounts listed in the configuration but missing on the PC are shown too, so they can be removed.
/// The Store setting is here because it is for the whole PC, not for a preset.
/// </summary>
public sealed partial class AccountsPageViewModel : PageViewModel
{
    private readonly Func<IReadOnlyList<string>> _standardAccounts;
    private bool _loading;

    public AccountsPageViewModel(PanelSession session, Func<IReadOnlyList<string>> standardAccounts)
        : base(session, "Ui.Nav.Accounts", "")
    {
        _standardAccounts = standardAccounts;
    }

    public ObservableCollection<AccountRow> Accounts { get; } = [];

    [ObservableProperty]
    private bool _blockStoreInstalls;

    partial void OnBlockStoreInstallsChanged(bool value)
    {
        if (!_loading) _ = Session.EditAsync(c => c.BlockStoreInstalls = value);
    }

    [RelayCommand]
    private async Task Restrict(AccountRow? row)
    {
        if (row is null) return;
        var preset = row.Preset.Name;
        string? created = null;
        if (await Session.EditAsync(c => created = ConfigEditor.AddUser(c, row.Name, preset).Name).ConfigureAwait(true) && created is not null)
            Session.PresetName = created;
    }

    [RelayCommand]
    private async Task Unrestrict(AccountRow? row)
    {
        if (row is null || !Session.Dialogs.Confirm(Loc.T("Ui.Accounts.ConfirmRemove", row.Name))) return;
        await Session.EditAsync(c => ConfigEditor.RemoveUser(c, row.Name)).ConfigureAwait(true);
    }

    private void Move(AccountRow row, PresetChoice to)
    {
        if (_loading || to.Name is null) return;
        _ = Session.EditAsync(c => ConfigEditor.MoveUser(c, row.Name, to.Name));
    }

    protected override void Rebuild()
    {
        var c = Session.Config;
        var restricted = c?.Users ?? [];
        IReadOnlyList<string> onPc;
        try
        {
            onPc = _standardAccounts();
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            onPc = [];
        }

        _loading = true;
        try
        {
            BlockStoreInstalls = c?.BlockStoreInstalls ?? true;
            var existing = (c?.Presets ?? []).Select(p => new PresetChoice(p.Name, p.Name)).ToList();
            var rows = new List<AccountRow>();
            foreach (var name in onPc.Concat(restricted).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                var preset = c?.PresetOf(name);
                var exists = onPc.Contains(name, StringComparer.OrdinalIgnoreCase);
                var status = !exists ? Loc.T("Ui.Accounts.Missing") : Loc.T(preset is not null ? "Ui.Accounts.IsRestricted" : "Ui.Accounts.NotRestricted");
                if (preset is not null)
                {
                    rows.Add(new AccountRow(name, true, exists, status, existing, existing.First(p => p.Name == preset.Name), Move));
                }
                else
                {
                    // A new account gets its own preset unless the administrator picks a shared one.
                    var own = c?.FindPreset(name) is null ? new PresetChoice(null, Loc.T("Ui.Accounts.OwnPreset", name)) : null;
                    var choices = own is null ? existing : [own, .. existing];
                    rows.Add(new AccountRow(name, false, exists, status, choices, choices.FirstOrDefault(p => p.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true) ?? choices[0], null));
                }
            }
            // Restricted accounts first: they are the ones the administrator looks after.
            Replace(Accounts, rows.OrderByDescending(r => r.IsRestricted));
        }
        finally
        {
            _loading = false;
        }
    }
}
