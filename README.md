# UseControl — parental control for Windows

UseControl restricts standard Windows accounts on a shared or family PC. The restricted accounts:

- cannot run games (except in the time you allow) or VPN/proxy clients;
- cannot install programs without an administrator;
- get only the browser extensions and settings you allow;
- cannot open folders you keep private.

Every restricted account gets a **profile**: its own set of rules, or one it shares with other accounts.

Games run only in the time of the profile's weekly schedule, with a daily limit counted while a game
actually runs (for example Mon–Fri 16:00–20:00, at most 1 hour of play). Five minutes before the end a
warning appears on the account's screen; a minute after the end running games are closed. An
administrator can block games for the rest of the day.

The panel groups everything a profile allows the way family safety tools do: **Apps and games** holds one
list of programs, program names and folders, each either a game (runs only in game time) or always blocked,
together with the games schedule and the folders that always run. Several programs or folders can be added
at once. **File access** is only about private files and folders; whether programs may start from a folder
is decided in Apps and games.

UseControl is local: no cloud and no telemetry. It works with ordinary local accounts, and no Microsoft account is needed.

> **Status: early development.** The C# version is being written. Until it is ready, the working tool is the PowerShell version in [`legacy/powershell`](legacy/powershell/README.md).

## Roadmap

| Stage | Contents |
|---|---|
| 1. Foundation | Windows service, admin panel (WPF), `uc.exe` command line. Everything the PowerShel version does, plus file and folder acces control and blocking programs by file fingerprint |
| 2. Screen time | Daily limits for the whole PC and per app, a tray agent, requests for extra time (games schedules and limits are done) |
| 3. Browsers | A catalogue of Chrome, Edge and Firefox policies, site block and allow lists |
| 4. Release | Reports, MSI installer, code signing, winget, updates |

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

## Installing

Download `UseControl-<version>-x64.msi` from the [releases](https://github.com/rnd95/UseControl/releases) and run it.
It installs the programs to `C:\Program Files\UseControl`, registers and starts the `UseControl` service,
adds `uc` to PATH and creates Start menu and desktop shortcuts. Removing UseControl in Windows Settings
lifts the restrictions first; the settings in `C:\ProgramData\UseControl` stay.

A copy installed earlier with `uc install` is taken over by the installer (its service is replaced).

## Updates

The service looks for a new release once a day (GitHub Releases of this repository). The panel shows a new
version on the Overview page and in Settings › Updates; **Update** downloads the installer, checks it and runs
it, and the service restarts with the new version. Restrictions stay in place all the time.

An installer is used only when its SHA-256 matches `SHA256SUMS.txt` of the same release and, once
UseControl is code-signed, when it is signed by the same publisher as the running copy. Only a copy
installed with the `.msi` updates itself.

From the command line: `uc update` (check), `uc update install`, `uc update auto on|off` (install by itself),
`uc update daily on|off` (the daily check).

## Releasing

1. Push a tag: `git tag v0.2.0` and `git push origin v0.2.0`.
2. GitHub Actions (`.github/workflows/release.yml`) runs the tests, publishes the programs and creates a
   **draft** release with `UseControl-0.2.0-x64.msi`, `UseControl-0.2.0-x64.zip` and `SHA256SUMS.txt`.
3. Check the draft, write what is new, and publish it. Installed copies find it within a day.

The same files can be built locally: `.\build\publish.ps1 -Version 0.2.0` and then `.\build\package.ps1 -Version 0.2.0`
(the installer needs the WiX SDK, which `dotnet build` downloads by itself). The repository must be public for
installed copies to see its releases.

## Installing from source

```
.\build\publish.ps1                                # all programs in artifacts\UseControl (self-contained, 64-bit)
.\artifacts\UseControl\uc.exe install              # in an administrator console
```

`uc install` does the following:

- copies the programs to `C:\Program Files\UseControl`;
- registers the `UseControl` service (starts with Windows, runs as SYSTEM, restarts after a failure) and starts it;
- adds `uc` to PATH;
- creates a Start menu and desktop shortcut to the panel.

Running it again updates an existing installation. Such a copy does not update itself; install the `.msi` once to get updates.

An installation of the PowerShell version is replaced: its configuration becomes the service's configuration, and its scheduled task and scripts are removed.

`uc uninstall` lifts all restrictions and removes UseControl. The configuration in `C:\ProgramData\UseControl` stays unless `--purge` is given. `--keep-restrictions` leaves the restrictions in place.

## Changing the look of the panel

The panel is split so that its look can change without touching its behaviour:

- `src/UseControl.Admin/Themes/Light.xaml` and `Dark.xaml` hold the colours of the two themes (same keys).
- `src/UseControl.Admin/Themes/Theme.xaml` holds every size and control style; colours are `DynamicResource`, so the theme switches live.
- `src/UseControl.Admin/Views/*.xaml` lay out one page each. They only bind to the page's view model.
- `src/UseControl.Admin/App.xaml` maps each page to its view. Point a page to a new view to redesign it.

To see every page filled in without installing the service, run the panel with sample data:

```
UseControl.exe --demo [--page 0-7] [--lang en|uk|de|pl] [--theme light|dark|system]
```

Pages are numbered from 0: 0 Overview, 1 Accounts, 2 Apps and games, 3 Windows restrictions, 4 Browsers, 5 File access, 6 Security check, 7 Settings.

Longer explanations are not on the pages: they open from the small "?" next to a heading (`HelpTip` style in `Theme.xaml`, text in `Tag`).

## Languages

English, Ukrainian, German and Polish. Windows' display language is used when available, otherwise English; `uc --lang de` or the `UC_LANG` environment variable overrides it.

To add a language:

1. Copy `src/UseControl.Localization/Resources/Strings.resx` to `Strings.<code>.resx` (for example `Strings.fr.resx`) and translate the values. Keep the `{0}`-style placeholders.
2. Add the code to `Loc.Available` and to `SatelliteResourceLanguages` in the projects.

The tests check that every language has every text, with the same placeholders.

## License

[MIT](LICENSE). Copyright © 2026 Rate Software.
