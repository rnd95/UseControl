using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

public sealed record LanguageChoice(CultureInfo Culture, string Text);

public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly Action<CultureInfo> _saveLanguage;
    private bool _loading;

    public SettingsPageViewModel(PanelSession session, Action<CultureInfo> saveLanguage)
        : base(session, "Ui.Nav.Settings", "")
    {
        _saveLanguage = saveLanguage;
        Languages = [.. Loc.Available.Select(c => new LanguageChoice(c, char.ToUpper(c.NativeName[0], c) + c.NativeName[1..]))];
        _language = Languages.FirstOrDefault(l => l.Culture.Name == Loc.Culture.Name) ?? Languages[0];
    }

    public IReadOnlyList<LanguageChoice> Languages { get; }

    [ObservableProperty]
    private LanguageChoice _language;

    partial void OnLanguageChanged(LanguageChoice value)
    {
        TranslationSource.Instance.Culture = value.Culture;
        _saveLanguage(value.Culture);
    }

    [ObservableProperty] private bool _systemDriveRoot;
    [ObservableProperty] private bool _writableDriveRoots;
    [ObservableProperty] private bool _otherDriveLetters;
    [ObservableProperty] private bool _recycleBins;
    [ObservableProperty] private bool _systemWritableFolders;
    [ObservableProperty] private bool _writableProgramFolders;
    [ObservableProperty] private bool _blockStoreInstalls;

    /// <summary>One entry per line.</summary>
    [ObservableProperty] private string _admins = "";
    [ObservableProperty] private string _privateFolders = "";
    [ObservableProperty] private string _hiddenAccounts = "";
    [ObservableProperty] private string _knownRemoteServices = "";

    [RelayCommand]
    private Task Save()
    {
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
        var admins = Lines(Admins);
        var priv = Lines(PrivateFolders);
        var hidden = Lines(HiddenAccounts);
        var remote = Lines(KnownRemoteServices);
        return Session.EditAsync(c =>
        {
            c.Auto = auto;
            c.BlockStoreInstalls = store;
            c.Audit.Admins = admins;
            c.Audit.PrivateFolders = priv;
            c.Audit.HiddenAccounts = hidden;
            c.Audit.KnownRemoteServices = remote;
        });
    }

    [RelayCommand]
    private Task Export()
    {
        var file = Session.Dialogs.PickSaveFile(Loc.T("Ui.Settings.Export"), "JSON|*.json", "UseControl.config.json");
        if (file is null) return Task.CompletedTask;
        return Session.RunAsync(async () =>
        {
            ConfigSerializer.Save(await Session.Client.GetConfigAsync().ConfigureAwait(true), file);
            Session.ShowMessage(MessageKind.Success, Loc.T("Cli.Exported", file));
        });
    }

    [RelayCommand]
    private Task Import()
    {
        var file = Session.Dialogs.PickFile(Loc.T("Ui.Settings.Import"), "JSON|*.json");
        if (file is null || !Session.Dialogs.Confirm(Loc.T("Ui.Settings.ConfirmImport"))) return Task.CompletedTask;
        return Session.RunAsync(async () =>
        {
            var imported = ConfigSerializer.Load(file);
            var r = await Session.Client.SetConfigAsync(imported).ConfigureAwait(true);
            await Session.RefreshAsync().ConfigureAwait(true);
            if (r.Applied is not null) Session.ShowApplyResult(r.Applied);
            else Session.ShowMessage(MessageKind.Info, Loc.T("Cli.Imported"));
        });
    }

    private static List<string> Lines(string text) =>
        [.. text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    protected override void Rebuild()
    {
        var c = Session.Config;
        if (c is null || _loading) return;
        _loading = true;
        try
        {
            SystemDriveRoot = c.Auto.SystemDriveRoot;
            WritableDriveRoots = c.Auto.WritableDriveRoots;
            OtherDriveLetters = c.Auto.OtherDriveLetters;
            RecycleBins = c.Auto.RecycleBins;
            SystemWritableFolders = c.Auto.SystemWritableFolders;
            WritableProgramFolders = c.Auto.WritableProgramFolders;
            BlockStoreInstalls = c.BlockStoreInstalls;
            Admins = string.Join(Environment.NewLine, c.Audit.Admins);
            PrivateFolders = string.Join(Environment.NewLine, c.Audit.PrivateFolders);
            HiddenAccounts = string.Join(Environment.NewLine, c.Audit.HiddenAccounts);
            KnownRemoteServices = string.Join(Environment.NewLine, c.Audit.KnownRemoteServices);
        }
        finally
        {
            _loading = false;
        }
    }
}
