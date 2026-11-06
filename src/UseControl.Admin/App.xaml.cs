using System.Windows;
using UseControl.Admin.Infrastructure;
using UseControl.Admin.ViewModels;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Paths;
using UseControl.Ipc;
using UseControl.Localization;
using UseControl.Windows.Accounts;
using UseControl.Windows.FileSystem;

namespace UseControl.Admin;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var prefs = PanelPreferences.Load();
        if (prefs.Language is { } lang) TranslationSource.Instance.Culture = Loc.Pick(lang);

        // --theme light|dark|system: theme for this run (screenshots).
        var theme = Enum.TryParse<PanelTheme>(Arg(e.Args, "--theme"), ignoreCase: true, out var t) ? t : prefs.Theme;
        ThemeManager.Apply(theme);

        // --demo: sample data, no service needed (for screenshots and working on the look).
        // --page N: open the N-th page (0 = Overview). --lang en|uk|de|pl: language for this run.
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        if (Arg(e.Args, "--lang") is { } argLang) TranslationSource.Instance.Culture = Loc.Pick(argLang);

        // The service only answers elevated administrators: check first, instead of a window that cannot connect.
        if (!demo && !Elevation.IsElevated())
        {
            if (Elevation.IsAdministratorWithoutElevation())
            {
                Elevation.RestartElevated(e.Args);   // the usual Windows prompt; cancelled = nothing more
            }
            else if (MessageBox.Show(Loc.T("Ui.AdminOnly"), Loc.T("Ui.App.Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                Elevation.RestartElevated(e.Args);   // Windows asks for an administrator's password
            }
            Shutdown();
            return;
        }

        IServiceClient client = demo ? new ViewModels.Demo.DemoServiceClient() : new UcClient();
        var session = new PanelSession(client, new DialogService(), WindowsSystemPaths.Current);
        var main = new MainViewModel(
            session,
            demo ? () => ["kids", "guest", "visitor"] : () => [.. LocalAccounts.GetStandardAccounts().Select(a => a.Name)],
            (path, category) => FileFingerprint.Compute(path, category),
            PanelPreferences.SaveLanguage,
            new ThemeChoices(theme, chosen =>
            {
                ThemeManager.Apply(chosen);
                PanelPreferences.SaveTheme(chosen);
            }));
        if (int.TryParse(Arg(e.Args, "--page"), out var page) && page >= 0 && page < main.Pages.Count) main.SelectedPage = main.Pages[page];

        var window = new MainWindow { DataContext = main };
        window.SourceInitialized += (_, _) => ThemeManager.SetTitleBar(window);
        // Colours picked once by converters (state colours) are picked again after a theme change.
        ThemeManager.Changed += (_, _) =>
        {
            window.DataContext = null;
            window.DataContext = main;
        };
        MainWindow = window;
        window.Show();
        _ = StartAsync(main, demo);
    }

    private static async Task StartAsync(MainViewModel main, bool demo)
    {
        await main.StartAsync().ConfigureAwait(true);
        // In demo mode the security check page shows results straight away.
        if (demo && main.SelectedPage is ViewModels.Pages.AuditPageViewModel audit) await audit.RunCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
