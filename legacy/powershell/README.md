# UseControl

Restrictions for standard Windows accounts on a shared or family PC. The restricted accounts:

- cannot run games or VPN/proxy clients;
- cannot install programs without an administrator;
- cannot add browser extensions you have not approved;
- cannot switch off the restrictions themselves.

Everything else installed on the PC keeps working. Games can be allowed for a few hours, and a security check reports common weak spots.

Built with Windows PowerShell 5.1 and Windows Forms, both part of Windows 10/11. No modules to install, no service, no cloud.

## How it works

Everything is written into the **restricted account's own registry hive**. Administrators are never affected.

**Software Restriction Policy (per user).**
- Programs run as usual, except from places the user can write to, plus the game and VPN folders you list.
- *Run as administrator* + an admin password still runs anything. That is how you approve an installer.
- Places blocked automatically, each switchable in `auto`:

| Place | Why |
|---|---|
| the account's profile and Downloads | the user can write there |
| the root of the system drive | ordinary users can create `C:\anything` |
| other drives that ordinary users can write to | the user can write there |
| program folders that ordinary users can write to | the user can write there |
| USB and network drive letters | removable or not controlled by the PC |
| Recycle Bins | the user can write there |
| writable folders inside Windows | the user can write there |

  Windows and Program Files stay allowed. Folders you add to `allowedPaths` run even inside a blocked place: Windows uses the most specific matching rule.

**Program names** (`DisallowRun`): command prompt, PowerShell, registry editor and script hosts, game launchers, VPN clients.

**Browser policies:**

| Browser | Policies |
|---|---|
| Chrome, Edge | extensions only from an allowlist; no DNS-over-HTTPS (so a DNS filter keeps working); SafeSearch; no incognito/InPrivate, guest mode, new profiles or developer tools; dangerous downloads blocked |
| Firefox | extensions only from an allowlist; no DNS-over-HTTPS; no private browsing |

**Store:** standard users cannot install Store/MSIX apps. This is the only machine-wide setting.

**Locking:** the policy keys are made read-only for the user.

## Install

1. Create the standard account(s), sign in to each once, then sign out.
2. In an administrator PowerShell, from the project folder:
   ```
   powershell -ExecutionPolicy Bypass -File .\UseControl.ps1 -Action Install
   ```
   This copies the tool to `C:\Program Files\UseControl` (only administrators can change it there) and puts a **UseControl** shortcut on the desktop. On first use `config.json` is created from `config.sample.json`.
3. Open the shortcut:
   - **Accounts > Add account**;
   - **Programs > Detect games**;
   - **Status > Apply restrictions**.

Work with the installed copy from then on. If you later edit `config.json` in the project folder, running Install again copies it over the installed one and keeps the previous one as `config.<date>.bak.json`.

## Console and remote use

`UseControl.ps1` without arguments shows a menu. Every item also works as `-Action`:

```
UseControl.ps1 -Action Status
UseControl.ps1 -Action Preview                     # every rule that would be written, and why
UseControl.ps1 -Action Apply
UseControl.ps1 -Action Undo                        # remove all restrictions
UseControl.ps1 -Action AddUser -User kids
UseControl.ps1 -Action RemoveUser -User kids
UseControl.ps1 -Action AllowGames -Hours 2
UseControl.ps1 -Action LockGames
UseControl.ps1 -Action Block -Path "D:\Games" -Category games
UseControl.ps1 -Action Block -Name game.exe -Category games
UseControl.ps1 -Action Unblock -Path "D:\Games"
UseControl.ps1 -Action AllowPath -Path "C:\Tools"
UseControl.ps1 -Action DetectGames -Add
UseControl.ps1 -Action AllowExtension -Browser chrome -ExtensionId <32 letters>
UseControl.ps1 -Action Audit
```

That makes it usable from remote tools without a desktop: MeshCentral terminal or *Run commands*, an RMM, or a scheduled task.

## Games for a limited time

`AllowGames` turns the folders of category `games` into allowed folders and stops blocking game program names until a set time. That time is stored in `C:\ProgramData\UseControl\state.json`, readable only by administrators and SYSTEM.

The scheduled task **UseControl games lock** (SYSTEM) blocks them again at that time and at every start of the PC. It only runs the copy in Program Files, so a restricted user cannot replace the script it runs. The log is in `C:\ProgramData\UseControl\log.txt`.

A signed-in account may need to sign out and back in to notice a change. Online games blocked by a DNS filter (AdGuard Home, Pi-hole, router) are not affected; use the filter's own schedule.

## config.json

| Key | Meaning |
|---|---|
| `users` | accounts to restrict |
| `auto` | automatic blocking switches (all on by default) |
| `blockedPaths` | folders, with a category: `games`, `vpn`, `tools`, `other` or `writable`. Machine-wide variables such as `%ProgramFiles(x86)%` are expanded; per-user ones like `%LOCALAPPDATA%` are not |
| `allowedPaths` | program folders that run although they are inside a blocked place |
| `blockedNames` | program file names |
| `browsers` | extension allowlists and policies per browser. A policy set to `null` is not written |
| `blockStoreInstalls` | Store/MSIX installs for administrators only |
| `audit` | what the security check expects: `admins`, `privateFolders`, `hiddenAccounts`, `knownRemoteServices` |

`C:\`, the Windows folder and Program Files cannot be blocked as a whole.

## Security check

Read-only. Reports:
- services that ordinary users can modify (a classic way to become SYSTEM);
- writable program folders and allowed folders;
- private folders that others can open;
- the Administrators group, the built-in Administrator and accounts allowed an empty password;
- hidden accounts;
- UAC and AlwaysInstallElevated;
- RDP exposure;
- remote-access services you have not listed;
- Defender and the firewall;
- whether each restricted account's rules are in place.

## Limits

- Apps already installed from the Store (packaged apps) are not covered by Software Restriction Policies.
- A user who knows an administrator password can do anything: that is the point of the password.
- Network-level control (DNS filtering, blocking VPN protocols) belongs on the router or DNS server.

## Tests

```
Invoke-Pester .\tests
```
Pester 3.4 ships with Windows 10. The tests cover config handling, validation and the rule set, and do not change the system.

The `meshcentral` folder holds an example of a VPN-only MeshCentral server (Docker) for remote access to such PCs.
