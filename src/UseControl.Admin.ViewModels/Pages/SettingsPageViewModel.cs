using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

public sealed record LanguageChoice(CultureInfo Culture, string Text);

/// <summary>Settings of UseControl itself: language, backup, version. What restricted accounts may do is on other pages.</summary>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly Action<CultureInfo> _saveLanguage;

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

    [ObservableProperty]
    private string _panelVersion = typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    [ObservableProperty]
    private string _serviceVersion = "—";

    [ObservableProperty]
    private string _dataDirectory = "—";

    partial void OnLanguageChanged(LanguageChoice value)
    {
        TranslationSource.Instance.Culture = value.Culture;
        _saveLanguage(value.Culture);
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

    protected override void Rebuild()
    {
        ServiceVersion = Session.Status?.Version ?? "—";
        DataDirectory = Session.Status?.DataDirectory ?? "—";
    }
}
