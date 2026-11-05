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
        if (PanelPreferences.Load().Language is { } lang) TranslationSource.Instance.Culture = Loc.Pick(lang);

        // --demo: sample data, no service needed (for screenshots and working on the look).
        // --page N: open the N-th page (0 = Status). --lang uk|en: language for this run.
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        if (Arg(e.Args, "--lang") is { } argLang) TranslationSource.Instance.Culture = Loc.Pick(argLang);

        IServiceClient client = demo ? new ViewModels.Demo.DemoServiceClient() : new UcClient();
        var session = new PanelSession(client, new DialogService(), WindowsSystemPaths.Current);
        var main = new MainViewModel(
            session,
            demo ? () => ["kids", "guest", "visitor"] : () => [.. LocalAccounts.GetStandardAccounts().Select(a => a.Name)],
            (path, category) => FileFingerprint.Compute(path, category),
            PanelPreferences.SaveLanguage);
        if (int.TryParse(Arg(e.Args, "--page"), out var page) && page >= 0 && page < main.Pages.Count) main.SelectedPage = main.Pages[page];

        var window = new MainWindow { DataContext = main };
        MainWindow = window;
        window.Show();
        _ = main.StartAsync();
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
