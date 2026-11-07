using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UseControl.Core.Paths;
using UseControl.Service;
using UseControl.Windows;

// The UseControl service. Installed as "UseControl", runs as LocalSystem. Started from a console
// (as administrator) it runs in the foreground, which is handy while developing.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "UseControl");

var dataDirectory = RestrictionEngine.DefaultDataDirectory;
RestrictionEngine.SecureDataDirectory(dataDirectory);

builder.Services.AddSingleton<IEngine>(_ => new WindowsEngine(dataDirectory));
builder.Services.AddSingleton(sp => new UcController(
    dataDirectory,
    sp.GetRequiredService<IEngine>(),
    WindowsSystemPaths.Current,
    TimeProvider.System,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("UseControl")));
builder.Services.AddSingleton<UseControl.Core.Updates.IUpdateSource>(_ => new UseControl.Windows.Updates.GitHubUpdateSource());
builder.Services.AddSingleton(sp => new Updater(
    dataDirectory,
    sp.GetRequiredService<UseControl.Core.Updates.IUpdateSource>(),
    TimeProvider.System,
    () => sp.GetRequiredService<UcController>().Config.Updates,
    UseControl.Core.Updates.ReleaseVersion.Format(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0)),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("UseControl.Updates"),
    TimeSpan.FromSeconds(5)));
builder.Services.AddHostedService<ServiceWorker>();

using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);