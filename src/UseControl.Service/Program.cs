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
builder.Services.AddHostedService<ServiceWorker>();

using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);