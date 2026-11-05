# UseControl — parental control for Windows

UseControl restricts standard Windows accounts on a shared or family PC. The restricted accounts:

- сannot run games or VPN/proxy clients;
- cannot install programs without an administrator;
- get only the browser extensions and settings you allow;
- cannot open folders you keep private.

Games can be allowed for a while. Screen-time limits are coming.

UseControl is local: no cloud and no telemetry. It works with ordinary local accounts, and no Microsoft account is needed.

> **Status: early development.** The C# version is being written. Until it is ready, the working tool is the PowerShell version in [`legacy/powershell`](legacy/powershell/README.md).

## Roadmap

| Stage | Contents |
|---|---|
| 1. Foundation | Windows service, admin panel (WPF), `uc.exe` command line. Everything the PowerShel version does, plus file and folder acces control and blocking programs by file fingerprint |
| 2. Screen time | Daily limits overall and per game or app, allowed hours, a tray agent with warnings, requests for extra time |
| 3. Browsers | A catalogue of Chrome, Edge and Firefox policies, site block and allow lists |
| 4. Release | Reports, MSI installer, code signing, winget, more languages |

## Repository

```
src/UseControl.Core     configuration, validation and rule building (no system access, unit-tested)
src/UseControl.Windows  everything that touches Windows: account registries, restriction policies,
                        browser policies, NTFS access, scans, game detection, the security check
src/UseControl.Ipc      the control channel (named pipe) between the service and its clients
src/UseControl.Service  the Windows service (SYSTEM): owns the configuration, applies it, games timer,
                        puts the restrictions back if something removed them
src/UseControl.Cli      uc.exe, the command line (run "uc help")
src/UseControl.Admin.ViewModels  what the admin panel does (no WPF: pages, commands, texts)
src/UseControl.Admin    the admin panel UseControl.exe (WPF): views and theme only
src/UseControl.Localization  every text shown to people, per language
tests/                  xUnit tests (the Windows tests only use temporary folders and a test key under HKCU)
legacy/powershell/      the PowerShell version (works today)
examples/meshcentral/   a VPN-only MeshCentral server for remote access to such PCs
assets/                 icon
```

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows 10 or 11.

```
dotnet build
dotnet test
```

## Changing the look of the panel

The panel is split so that its look can change without touching its behaviour:

- `src/UseControl.Admin/Themes/Theme.xaml` holds every colour, size and control style.
- `src/UseControl.Admin/Views/*.xaml` lay out one page each. They only bind to the page's view model.
- `src/UseControl.Admin/App.xaml` maps each page to its view. Point a page to a new view to redesign it.

To see every page filled in without installing the service, run the panel with sample data:

```
UseControl.exe --demo [--page 0-8] [--lang en|uk]
```

Pages are numbered from 0: 0 Status, 1 Accounts, 2 Restrictions, 3 Games, 4 Programs, 5 Files and folders, 6 Browsers, 7 Security check, 8 Settings.

## Languages

English and Ukrainian. Windows' display language is used when available, otherwise English; `uc --lang uk` or the `UC_LANG` environment variable overrides it.

To add a language:

1. Copy `src/UseControl.Localization/Resources/Strings.resx` to `Strings.<code>.resx` (for example `Strings.pl.resx`) and translate the values. Keep the `{0}`-style placeholders.
2. Add the code to `Loc.Available` and to `SatelliteResourceLanguages` in the projects.

The tests check that every language has every text, with the same placeholders.

## License

[MIT](LICENSE)
