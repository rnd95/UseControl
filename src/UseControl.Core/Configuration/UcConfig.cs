using System.Text.Json;

namespace UseControl.Core.Configuration;

/// <summary>Everything UseControl does, as stored in config.json.</summary>
public sealed class UcConfig
{
    public int SchemaVersion { get; set; } = ConfigSerializer.CurrentSchemaVersion;

    /// <summary>Local standard accounts that are restricted.</summary>
    public List<string> Users { get; set; } = [];

    /// <summary>Only administrators may install Store/MSIX apps (machine-wide).</summary>
    public bool BlockStoreInstalls { get; set; } = true;

    /// <summary>Places found on the PC and blocked automatically.</summary>
    public AutoBlockOptions Auto { get; set; } = new();

    /// <summary>Folders (or files) from which programs may not start.</summary>
    public List<PathEntry> BlockedPaths { get; set; } = [];

    /// <summary>Program folders that run although they are inside a blocked place.</summary>
    public List<AllowedPathEntry> AllowedPaths { get; set; } = [];

    /// <summary>Program file names blocked wherever they are (when started from Explorer).</summary>
    public List<NameEntry> BlockedNames { get; set; } = [];

    /// <summary>Programs blocked by their content, whatever they are called and wherever they are.</summary>
    public List<FileHashEntry> BlockedFiles { get; set; } = [];

    /// <summary>NTFS access of restricted accounts to folders and files.</summary>
    public List<FileAccessRule> FileAccess { get; set; } = [];

    public BrowserSettings Browsers { get; set; } = new();

    public AuditSettings Audit { get; set; } = new();
}

public enum RuleCategory
{
    Writable,
    Games,
    Vpn,
    Tools,
    Other,
}

public sealed class AutoBlockOptions
{
    /// <summary>The root of the system drive: ordinary users can create C:\anything.</summary>
    public bool SystemDriveRoot { get; set; } = true;

    /// <summary>Other fixed drives whose root ordinary users can write to.</summary>
    public bool WritableDriveRoots { get; set; } = true;

    /// <summary>Every drive letter that is not a fixed drive (USB, network, future drives).</summary>
    public bool OtherDriveLetters { get; set; } = true;

    public bool RecycleBins { get; set; } = true;

    /// <summary>Folders inside Windows that ordinary users can write to, Users\Public and ProgramData.</summary>
    public bool SystemWritableFolders { get; set; } = true;

    /// <summary>Program folders (in Program Files and on other drives) that ordinary users can write to.</summary>
    public bool WritableProgramFolders { get; set; } = true;
}

public sealed class PathEntry
{
    public string Path { get; set; } = "";
    public RuleCategory Category { get; set; } = RuleCategory.Other;
    public string? Note { get; set; }
}

public sealed class AllowedPathEntry
{
    public string Path { get; set; } = "";
    public string? Note { get; set; }
}

public sealed class NameEntry
{
    public string Name { get; set; } = "";
    public RuleCategory Category { get; set; } = RuleCategory.Other;
    public string? Note { get; set; }
}

/// <summary>A program identified by its content. Windows hash rules need MD5 and the size; SHA-256 identifies it for people.</summary>
public sealed class FileHashEntry
{
    /// <summary>The file name when it was added, for display.</summary>
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Md5 { get; set; } = "";
    public long Size { get; set; }
    public RuleCategory Category { get; set; } = RuleCategory.Other;
    public string? Note { get; set; }
}

public enum FileAccessMode
{
    /// <summary>Take access away (an explicit deny entry, stronger than any allow).</summary>
    Deny,

    /// <summary>Give access (an explicit allow entry).</summary>
    Allow,
}

[Flags]
public enum FileAccessRights
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
    Delete = 8,
    Full = Read | Write | Execute | Delete,
}

public sealed class FileAccessRule
{
    public string Path { get; set; } = "";
    public FileAccessMode Mode { get; set; } = FileAccessMode.Deny;
    public FileAccessRights Rights { get; set; } = FileAccessRights.Full;

    /// <summary>Accounts the rule is for; empty means every restricted account.</summary>
    public List<string> Users { get; set; } = [];

    public string? Note { get; set; }
}

public sealed class BrowserSettings
{
    public ChromiumSettings Chrome { get; set; } = new();
    public ChromiumSettings Edge { get; set; } = new();
    public FirefoxSettings Firefox { get; set; } = new();
}

/// <summary>Chrome or Edge: an extension allowlist and any policy by its registry name.</summary>
public sealed class ChromiumSettings
{
    public bool BlockExtensions { get; set; } = true;
    public List<string> ExtensionsAllowed { get; set; } = [];

    /// <summary>Policy name to value (number, true/false or text). null means the policy is not set.</summary>
    public Dictionary<string, JsonElement> Policies { get; set; } = [];
}

public sealed class FirefoxSettings
{
    public bool BlockExtensions { get; set; } = true;
    public List<string> ExtensionsAllowed { get; set; } = [];
    public bool DisableDnsOverHttps { get; set; } = true;
    public bool DisablePrivateBrowsing { get; set; } = true;

    /// <summary>Other simple Firefox policies by name (number, true/false or text).</summary>
    public Dictionary<string, JsonElement> Policies { get; set; } = [];
}

/// <summary>What the security check expects on this PC.</summary>
public sealed class AuditSettings
{
    /// <summary>Accounts expected in the Administrators group; others are reported. Empty: no check.</summary>
    public List<string> Admins { get; set; } = [];

    /// <summary>Folders that only administrators and SYSTEM may open.</summary>
    public List<string> PrivateFolders { get; set; } = [];

    /// <summary>Accounts that should not appear on the sign-in screen.</summary>
    public List<string> HiddenAccounts { get; set; } = [];

    /// <summary>Remote-access services the administrator knows about (by service name).</summary>
    public List<string> KnownRemoteServices { get; set; } = [];
}
