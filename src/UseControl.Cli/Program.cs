using System.Text;
using UseControl.Cli;
using UseControl.Core.Paths;
using UseControl.Ipc;

// uc.exe: talks to the UseControl service. Run it as administrator (or as SYSTEM from a remote tool).
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
if (Environment.GetEnvironmentVariable("UC_LANG") is { Length: > 0 } lang) UseControl.Localization.Loc.Culture = UseControl.Localization.Loc.Pick(lang);

var app = new CliApp(new UcClient(), Console.Out, Console.Error, Console.In, WindowsSystemPaths.Current);
return await app.RunAsync(args).ConfigureAwait(false);
