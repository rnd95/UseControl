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
tests/                  xUnit tests
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

## Languages

English and Ukrainian. Every text lives in resource files, so a new language is a new file. No code changes are needed.

## License

[MIT](LICENSE)
