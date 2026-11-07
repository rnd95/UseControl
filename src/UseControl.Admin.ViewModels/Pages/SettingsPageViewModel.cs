using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

public sealed record LanguageChoice(CultureInfo Culture, string Text);

public sealed record ThemeChoice(PanelTheme Theme, string Text);

/// <summary>Settings of UseControl itself: language, backup, version. What restricted accounts may do is on other pages.</summary>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly Action<CultureInfo> _saveLanguage;
    private readonly ThemeChoices _themes;

    public SettingsPageViewModel(PanelSession session, Action<CultureInfo> saveLanguage, ThemeChoices themes, UpdateViewModel? update = null)
        : base(session, "Ui.Nav.Settings", "")
    {
        _saveLanguage = saveLanguage;
        Update = update ?? new UpdateViewModel(session);
        _themes = themes;
        BuildThemes(themes.Current);
        Languages = [.. Loc.Available.Select(c => new LanguageChoice(c, char.ToUpper(c.NativeName[0], c) + c.NativeName[1..]))];
        _language = Languages.FirstOrDefault(l => l.Culture.Name == Loc.Culture.Name) ?? Languages[0];
    }

    public override NavSection Section => NavSection.Computer;

    public UpdateViewModel Update { get; }

    public IReadOnlyList<LanguageChoice> Languages { get; }

    [ObservableProperty]
    private LanguageChoice _language;

    [ObservableProperty]
    private string _panelVersion = typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>"Copyright © 2026 Rate Software", from the assembly.</summary>
    public string Copyright { get; } =
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyCopyrightAttribute>(typeof(SettingsPageViewModel).Assembly)?.Copyright ?? "";

    [ObservableProperty]
    private string _serviceVersion = "—";

    [ObservableProperty]
    private string _dataDirectory = "—";

    partial void OnLanguageChanged(LanguageChoice value)
    {
        TranslationSource.Instance.Culture = value.Culture;
        _saveLanguage(value.Culture);
    }

    public IReadOnlyList<ThemeChoice> Themes { get; private set; } = [];

    [ObservableProperty]
    private ThemeChoice? _theme;

    private bool _buildingThemes;

    partial void OnThemeChanged(ThemeChoice? value)
    {
        if (!_buildingThemes && value is not null) _themes.Apply(value.Theme);
    }

    private void BuildThemes(PanelTheme current)
    {
        _buildingThemes = true;
        try
        {
            Themes = [.. Enum.GetValues<PanelTheme>().Select(t => new ThemeChoice(t, Loc.T("Ui.Settings.Theme." + t)))];
            OnPropertyChanged(nameof(Themes));
            Theme = Themes.First(t => t.Theme == current);
        }
        finally
        {
            _buildingThemes = false;
        }
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
        BuildThemes(Theme?.Theme ?? _themes.Current);
        ServiceVersion = Session.Status?.Version ?? "—";
        DataDirectory = Session.Status?.DataDirectory ?? "—";
    }
}
