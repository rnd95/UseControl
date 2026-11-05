using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using UseControl.Core.Configuration;

namespace UseControl.Windows.FileSystem;

/// <summary>One access entry UseControl put on a file or folder.</summary>
public sealed record AppliedAce(string Path, string Sid, FileAccessMode Mode, int Rights);

/// <summary>What happened to one entry during <see cref="FileAccessManager.Apply"/>.</summary>
public sealed record FileAccessChange(string Path, string Account, string Action, string? Error = null);

/// <summary>
/// Puts the file-access rules of the config on files and folders as explicit NTFS entries for the
/// restricted accounts. Every entry it adds is written to a ledger, so it removes exactly what it
/// added and never touches other permissions.
/// </summary>
public sealed class FileAccessManager
{
    private readonly string _ledgerPath;

    public FileAccessManager(string dataDirectory)
    {
        _ledgerPath = System.IO.Path.Combine(dataDirectory, "file-access.json");
    }

    /// <summary>The NTFS rights that stand for the config's rights. Deny keeps attributes readable so Explorer still lists the item.</summary>
    public static FileSystemRights MapRights(FileAccessRights rights, FileAccessMode mode)
    {
        FileSystemRights r = 0;
        if (rights.HasFlag(FileAccessRights.Read))
        {
            r |= FileSystemRights.ReadData | FileSystemRights.ReadExtendedAttributes;
            if (mode == FileAccessMode.Allow) r |= FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize;
        }
        if (rights.HasFlag(FileAccessRights.Write))
            r |= FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        if (rights.HasFlag(FileAccessRights.Execute)) r |= FileSystemRights.ExecuteFile;
        if (rights.HasFlag(FileAccessRights.Delete)) r |= FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
        return r;
    }

    /// <summary>The entries the config asks for, given the restricted accounts (name to SID).</summary>
    public static IReadOnlyList<AppliedAce> Desired(UcConfig config, IReadOnlyDictionary<string, string> accounts)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(accounts);
        var list = new List<AppliedAce>();
        foreach (var rule in config.FileAccess)
        {
            var names = rule.Users.Count == 0 ? accounts.Keys : rule.Users.Where(accounts.ContainsKey);
            foreach (var n in names) list.Add(new AppliedAce(rule.Path, accounts[n], rule.Mode, (int)MapRights(rule.Rights, rule.Mode)));
        }
        return list;
    }

    /// <summary>Makes the file system match the config: removes entries no longer wanted, adds new ones.</summary>
    public IReadOnlyList<FileAccessChange> Apply(UcConfig config, IReadOnlyDictionary<string, string> accounts)
    {
        var desired = Desired(config, accounts);
        var ledger = LoadLedger();
        var changes = new List<FileAccessChange>();
        var kept = new List<AppliedAce>();

        foreach (var old in ledger)
        {
            if (desired.Contains(old)) { kept.Add(old); continue; }
            changes.Add(Try(old, "removed", () => Change(old, add: false)));
        }
        foreach (var d in desired)
        {
            var already = kept.Contains(d);
            var change = Try(d, already ? "checked" : "added", () => Change(d, add: true));
            if (change.Error is null) { if (!already) kept.Add(d); }
            else if (already) kept.Remove(d);
            if (!already || change.Error is not null) changes.Add(change);
        }
        SaveLedger(kept);
        return changes;
    }

    /// <summary>Removes every entry UseControl added.</summary>
    public IReadOnlyList<FileAccessChange> RemoveAll()
    {
        var changes = LoadLedger().Select(a => Try(a, "removed", () => Change(a, add: false))).ToList();
        SaveLedger([]);
        return changes;
    }

    public IReadOnlyList<AppliedAce> LoadLedger()
    {
        if (!File.Exists(_ledgerPath)) return [];
        return JsonSerializer.Deserialize<List<AppliedAce>>(File.ReadAllText(_ledgerPath), Core.Configuration.ConfigSerializer.Options) ?? [];
    }

    private void SaveLedger(IReadOnlyList<AppliedAce> entries)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_ledgerPath)!);
        var tmp = _ledgerPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Core.Configuration.ConfigSerializer.Options));
        File.Move(tmp, _ledgerPath, overwrite: true);
    }

    private static FileAccessChange Try(AppliedAce a, string action, Action change)
    {
        var account = AclInspector.Name(a.Sid);
        try
        {
            change();
            return new FileAccessChange(a.Path, account, action);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or PrivilegeNotHeldException)
        {
            return new FileAccessChange(a.Path, account, action, e.Message);
        }
    }

    // Adding an identical rule twice is harmless (Windows merges it); removal removes exactly this rule.
    private static void Change(AppliedAce a, bool add)
    {
        var isDir = Directory.Exists(a.Path);
        if (!isDir && !File.Exists(a.Path))
        {
            if (add) throw new IOException($"Not found: {a.Path}");
            return;
        }
        var inherit = isDir ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var rule = new FileSystemAccessRule(new SecurityIdentifier(a.Sid), (FileSystemRights)a.Rights, inherit, PropagationFlags.None,
            a.Mode == FileAccessMode.Deny ? AccessControlType.Deny : AccessControlType.Allow);
        if (isDir)
        {
            var di = new DirectoryInfo(a.Path);
            var sec = di.GetAccessControl(AccessControlSections.Access);
            if (add) sec.AddAccessRule(rule); else sec.RemoveAccessRuleSpecific(rule);
            di.SetAccessControl(sec);
        }
        else
        {
            var fi = new FileInfo(a.Path);
            var sec = fi.GetAccessControl(AccessControlSections.Access);
            if (add) sec.AddAccessRule(rule); else sec.RemoveAccessRuleSpecific(rule);
            fi.SetAccessControl(sec);
        }
    }
}
