using System.Globalization;
using System.Text.Json;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Core.Validation;
using UseControl.Ipc;
using UseControl.Localization;
using UseControl.Windows;
using UseControl.Windows.Accounts;
using UseControl.Windows.Audit;
using UseControl.Windows.FileSystem;

namespace UseControl.Cli;

/// <summary>Usage error: shown with the hint to run "uc help", exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>The commands of uc.exe. Every change goes through the service, which validates and applies it.</summary>
internal sealed class CliApp
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;

    private readonly IServiceClient _client;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly TextReader _in;
    private readonly ISystemPaths _sp;

    public CliApp(IServiceClient client, TextWriter output, TextWriter error, TextReader input, ISystemPaths sp)
    {
        _client = client;
        _out = output;
        _err = error;
        _in = input;
        _sp = sp;
    }

    /// <summary>How a program file is fingerprinted (replaced in tests).</summary>
    public Func<string, RuleCategory, FileHashEntry> Fingerprint { get; init; } = (p, c) => FileFingerprint.Compute(p, c);

    /// <summary>Standard accounts on this PC, for "users list" (replaced in tests).</summary>
    public Func<IReadOnlyList<string>> StandardAccounts { get; init; } = () => [.. LocalAccounts.GetStandardAccounts().Select(a => a.Name)];

    public async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var a = new CliArgs(args);
        if (a.Option("lang") is { } lang) Loc.Culture = Loc.Pick(lang);
        try
        {
            return await DispatchAsync(a).ConfigureAwait(false);
        }
        catch (UsageException e)
        {
            await _err.WriteLineAsync(e.Message).ConfigureAwait(false);
            return Usage;
        }
        catch (UcException e)
        {
            await _err.WriteLineAsync(Loc.T("Cli.ErrorPrefix", Loc.Error(e))).ConfigureAwait(false);
            return Failed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            await _err.WriteLineAsync(Loc.T("Cli.ErrorPrefix", e.Message)).ConfigureAwait(false);
            return Failed;
        }
    }

    private Task<int> DispatchAsync(CliArgs a) => a.Word(0) switch
    {
        "" or "help" or "-h" or "/?" => Help(),
        "ping" or "version" => Ping(),
        "status" => Status(a),
        "apply" => Apply(),
        "suspend" => Suspend(a),
        "preview" => Preview(a),
        "audit" => Audit(a),
        "users" => Users(a),
        "games" => Games(a),
        "block" => Block(a),
        "unblock" => Unblock(a),
        "allow" => Allow(a),
        "access" => Access(a),
        "ext" or "extension" => Extension(a),
        "policy" => Policy(a),
        "config" => Config(a),
        _ => throw new UsageException(Loc.T("Cli.UnknownCommand", a.Value(0))),
    };

    // ---------------------------------------------------------------- simple commands

    private async Task<int> Help()
    {
        await _out.WriteLineAsync(Loc.T("Cli.Help")).ConfigureAwait(false);
        return Ok;
    }

    private async Task<int> Ping()
    {
        var p = await _client.PingAsync().ConfigureAwait(false);
        await _out.WriteLineAsync(Loc.T("Cli.Ping", p.Version, p.ProcessId)).ConfigureAwait(false);
        return Ok;
    }

    private async Task<int> Status(CliArgs a)
    {
        var s = await _client.GetStatusAsync().ConfigureAwait(false);
        if (a.Flag("json")) return await Json(s).ConfigureAwait(false);
        await Line("Cli.StatusService", s.Version).ConfigureAwait(false);
        await Line(s.Enabled ? "Cli.StatusEnabled" : "Cli.StatusSuspended").ConfigureAwait(false);
        await (s.GamesAllowedUntil is { } until ? Line("Cli.StatusGamesAllowed", Local(until)) : Line("Cli.StatusGamesBlocked")).ConfigureAwait(false);
        await Line("Cli.StatusLastApplied", s.LastApplied is { } la ? Local(la) : Loc.T("Cli.StatusNever")).ConfigureAwait(false);
        await Line(s.StoreInstallsAdminOnly ? "Cli.StatusStoreAdmins" : "Cli.StatusStoreEveryone").ConfigureAwait(false);
        await _out.WriteLineAsync().ConfigureAwait(false);
        if (s.Accounts.Count == 0) await Line("Cli.StatusNoAccounts").ConfigureAwait(false);
        foreach (var acc in s.Accounts)
        {
            if (acc.State is not { } st) await Line("Cli.StatusAccountUnreadable", acc.User).ConfigureAwait(false);
            else if (!st.Applied) await Line("Cli.StatusAccountNotApplied", acc.User).ConfigureAwait(false);
            else await Line("Cli.StatusAccount", acc.User, st.BlockedPathRules, st.AllowedPathRules, st.HashRules / 2, st.BlockedNames, YesNo(st.KeysLocked)).ConfigureAwait(false);
        }
        foreach (var r in s.LastResults.Where(r => !r.Success)) await Line("Cli.AccountFailed", r.User, r.Error).ConfigureAwait(false);
        return Ok;
    }

    private async Task<int> Apply()
    {
        await PrintApply(await _client.ApplyAsync().ConfigureAwait(false)).ConfigureAwait(false);
        return Ok;
    }

    private async Task<int> Suspend(CliArgs a)
    {
        if (!a.Flag("yes"))
        {
            await _out.WriteAsync(Loc.T("Cli.ConfirmSuspend") + ": ").ConfigureAwait(false);
            if (!string.Equals((await _in.ReadLineAsync().ConfigureAwait(false))?.Trim(), "YES", StringComparison.Ordinal))
            {
                await Line("Cli.Cancelled").ConfigureAwait(false);
                return Failed;
            }
        }
        var r = await _client.SuspendAsync().ConfigureAwait(false);
        foreach (var acc in r.Accounts)
        {
            await (acc.Success ? Line("Cli.AccountRemoved", acc.User) : Line("Cli.AccountFailed", acc.User, acc.Error)).ConfigureAwait(false);
        }
        await Line("Cli.Suspended").ConfigureAwait(false);
        return Ok;
    }

    private async Task<int> Preview(CliArgs a)
    {
        var p = await _client.PreviewAsync().ConfigureAwait(false);
        if (a.Flag("json")) return await Json(p).ConfigureAwait(false);
        foreach (var acc in p.Accounts)
        {
            await Line("Cli.PreviewAccount", acc.User).ConfigureAwait(false);
            if (acc.Error is not null) await _out.WriteLineAsync("  " + acc.Error).ConfigureAwait(false);
            await Line("Cli.PreviewBlocked").ConfigureAwait(false);
            foreach (var r in acc.Rules.Blocked) await _out.WriteLineAsync($"  {r.Path,-60} {r.Source.ToString().ToLowerInvariant()}").ConfigureAwait(false);
            await Line("Cli.PreviewAllowed").ConfigureAwait(false);
            foreach (var r in acc.Rules.Allowed) await _out.WriteLineAsync($"  {r.Path,-60} {r.Source.ToString().ToLowerInvariant()}").ConfigureAwait(false);
            await Line("Cli.PreviewNames", string.Join(", ", acc.Rules.BlockedNames)).ConfigureAwait(false);
            await Line("Cli.PreviewFiles", acc.Rules.BlockedFiles.Count == 0 ? Loc.T("Cli.None") : string.Join(", ", acc.Rules.BlockedFiles.Select(f => f.FileName))).ConfigureAwait(false);
            await _out.WriteLineAsync().ConfigureAwait(false);
        }
        await Line("Cli.PreviewAuto").ConfigureAwait(false);
        foreach (var x in p.AutoPaths) await _out.WriteLineAsync($"  {x.Path,-60} {x.Reason}").ConfigureAwait(false);
        return Ok;
    }

    private async Task<int> Audit(CliArgs a)
    {
        var checks = await _client.AuditAsync().ConfigureAwait(false);
        if (a.Flag("json")) return await Json(checks).ConfigureAwait(false);
        foreach (var c in checks)
        {
            var name = Loc.Has("Audit." + c.Id) ? Loc.T("Audit." + c.Id) : c.Id;
            if (c.Subject is not null) name += ": " + c.Subject;
            await _out.WriteLineAsync($"[{Loc.T("Audit." + c.Status),-7}] {name}").ConfigureAwait(false);
            await _out.WriteLineAsync("          " + c.Detail).ConfigureAwait(false);
        }
        var fail = checks.Count(c => c.Status == AuditStatus.Fail);
        var warn = checks.Count(c => c.Status == AuditStatus.Warning);
        await _out.WriteLineAsync().ConfigureAwait(false);
        await Line("Cli.AuditSummary", checks.Count, fail, warn).ConfigureAwait(false);
        return fail > 0 ? Failed : Ok;
    }

    // ---------------------------------------------------------------- accounts and games

    private async Task<int> Users(CliArgs a)
    {
        switch (a.Word(1))
        {
            case "" or "list":
                var c = await _client.GetConfigAsync().ConfigureAwait(false);
                await Line("Cli.UsersRestricted", c.Users.Count == 0 ? Loc.T("Cli.None") : string.Join(", ", c.Users)).ConfigureAwait(false);
                var others = StandardAccounts().Where(n => !c.Users.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                await Line("Cli.UsersCandidates", others.Count == 0 ? Loc.T("Cli.None") : string.Join(", ", others)).ConfigureAwait(false);
                return Ok;
            case "add":
                var add = Required(a.Value(2), "<name>");
                return await Edit(cfg => ConfigEditor.AddUser(cfg, add)).ConfigureAwait(false);
            case "remove":
                var remove = Required(a.Value(2), "<name>");
                return await Edit(cfg => ConfigEditor.RemoveUser(cfg, remove)).ConfigureAwait(false);
            default:
                throw new UsageException(Loc.T("Cli.UnknownCommand", "users " + a.Value(1)));
        }
    }

    private async Task<int> Games(CliArgs a)
    {
        switch (a.Word(1))
        {
            case "allow":
                var text = Required(a.Value(2), "<hours>").Replace(',', '.');
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)) throw new UcException(ErrorCodes.InvalidArgument, text, "hours");
                var s = await _client.AllowGamesAsync(hours).ConfigureAwait(false);
                await Line("Cli.GamesAllowed", s.GamesAllowedUntil is { } u ? Local(u) : "?").ConfigureAwait(false);
                return Ok;
            case "lock":
                await _client.LockGamesAsync().ConfigureAwait(false);
                await Line("Cli.GamesLocked").ConfigureAwait(false);
                return Ok;
            case "find":
                var found = await _client.FindGamesAsync().ConfigureAwait(false);
                if (a.Flag("json")) return await Json(found).ConfigureAwait(false);
                if (found.Count == 0)
                {
                    await Line("Cli.GamesFoundNone").ConfigureAwait(false);
                    return Ok;
                }
                await Line("Cli.GamesFound").ConfigureAwait(false);
                foreach (var f in found) await _out.WriteLineAsync("  " + f).ConfigureAwait(false);
                if (!a.Flag("add"))
                {
                    await Line("Cli.GamesFoundHint").ConfigureAwait(false);
                    return Ok;
                }
                return await Edit(cfg => { foreach (var f in found) ConfigEditor.AddBlockedPath(cfg, f, RuleCategory.Games, _sp); }).ConfigureAwait(false);
            default:
                throw new UsageException(Loc.T("Cli.UnknownCommand", "games " + a.Value(1)));
        }
    }

    // ---------------------------------------------------------------- programs

    private async Task<int> Block(CliArgs a)
    {
        var category = Category(a.Option("category"));
        switch (a.Word(1))
        {
            case "folder":
                var folder = Required(a.Rest(2), "<path>");
                return await Edit(cfg => ConfigEditor.AddBlockedPath(cfg, folder, category, _sp)).ConfigureAwait(false);
            case "name":
                var name = Required(a.Value(2), "<file.exe>");
                return await Edit(cfg => ConfigEditor.AddBlockedName(cfg, name, category)).ConfigureAwait(false);
            case "file":
                var path = Required(a.Rest(2), "<path to .exe>");
                var entry = Fingerprint(PathRules.Normalize(path), category);
                var code = await Edit(cfg => ConfigEditor.AddBlockedFile(cfg, entry)).ConfigureAwait(false);
                if (code == Ok) await Line("Cli.FileBlocked", entry.FileName).ConfigureAwait(false);
                return code;
            default:
                throw new UsageException(Loc.T("Cli.UnknownCommand", "block " + a.Value(1)));
        }
    }

    private async Task<int> Unblock(CliArgs a)
    {
        switch (a.Word(1))
        {
            case "folder":
                var folder = Required(a.Rest(2), "<path>");
                return await Edit(cfg => ConfigEditor.RemoveBlockedPath(cfg, folder)).ConfigureAwait(false);
            case "name":
                var name = Required(a.Value(2), "<file.exe>");
                return await Edit(cfg => ConfigEditor.RemoveBlockedName(cfg, name)).ConfigureAwait(false);
            case "file":
                var value = Required(a.Rest(2), "<path or SHA-256>");
                var sha = Validators.IsHex(value, 64) ? value : Fingerprint(PathRules.Normalize(value), RuleCategory.Other).Sha256;
                return await Edit(cfg => ConfigEditor.RemoveBlockedFile(cfg, sha)).ConfigureAwait(false);
            default:
                throw new UsageException(Loc.T("Cli.UnknownCommand", "unblock " + a.Value(1)));
        }
    }

    private async Task<int> Allow(CliArgs a)
    {
        var path = Required(a.Rest(2), "<path>");
        return a.Word(1) switch
        {
            "folder" => await Edit(cfg => ConfigEditor.AddAllowedPath(cfg, path)).ConfigureAwait(false),
            "remove" => await Edit(cfg => ConfigEditor.RemoveAllowedPath(cfg, path)).ConfigureAwait(false),
            _ => throw new UsageException(Loc.T("Cli.UnknownCommand", "allow " + a.Value(1))),
        };
    }

    private async Task<int> Access(CliArgs a)
    {
        var path = Required(a.Rest(2), "<path>");
        switch (a.Word(1))
        {
            case "deny" or "allow":
                var mode = a.Word(1) == "deny" ? FileAccessMode.Deny : FileAccessMode.Allow;
                var rights = Rights(a.Option("rights"));
                var users = (a.Option("users") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return await Edit(cfg => ConfigEditor.SetFileAccess(cfg, path, mode, rights, users, _sp)).ConfigureAwait(false);
            case "remove":
                return await Edit(cfg => ConfigEditor.RemoveFileAccess(cfg, path)).ConfigureAwait(false);
            default:
                throw new UsageException(Loc.T("Cli.UnknownCommand", "access " + a.Value(1)));
        }
    }

    private async Task<int> Extension(CliArgs a)
    {
        var browser = Browser(a.Value(2));
        var id = Required(a.Value(3), "<extension id>");
        return a.Word(1) switch
        {
            "allow" or "add" => await Edit(cfg => ConfigEditor.AddExtension(cfg, browser, id)).ConfigureAwait(false),
            "remove" => await Edit(cfg => ConfigEditor.RemoveExtension(cfg, browser, id)).ConfigureAwait(false),
            _ => throw new UsageException(Loc.T("Cli.UnknownCommand", "ext " + a.Value(1))),
        };
    }

    private async Task<int> Policy(CliArgs a)
    {
        var browser = Browser(a.Value(2));
        var name = Required(a.Value(3), "<name>");
        switch (a.Word(1))
        {
            case "set":
                var value = Required(a.Rest(4), "<value>");
                return await Edit(cfg => ConfigEditor.SetBrowserPolicy(cfg, browser, name, value)).ConfigureAwait(false);
            case "remove":
                return await Edit(cfg => ConfigEditor.RemoveBrowserPolicy(cfg, browser, name)).ConfigureAwait(false);
            default:
                throw new UsageException(Loc.T("Cli.UnknownCommand", "policy " + a.Value(1)));
        }
    }

    private async Task<int> Config(CliArgs a)
    {
        var file = Required(a.Rest(2), "<file>");
        switch (a.Word(1))
        {
            case "export":
                ConfigSerializer.Save(await _client.GetConfigAsync().ConfigureAwait(false), file);
                await Line("Cli.Exported", Path.GetFullPath(file)).ConfigureAwait(false);
                return Ok;
            case "import":
                var imported = ConfigSerializer.Load(file);
                await PrintSetConfig(await _client.SetConfigAsync(imported).ConfigureAwait(false)).ConfigureAwait(false);
                await Line("Cli.Imported").ConfigureAwait(false);
                return Ok;
            default:
                throw new UsageException(Loc.T("Cli.UnknownCommand", "config " + a.Value(1)));
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Reads the configuration from the service, changes it, sends it back (the service validates and applies).</summary>
    private async Task<int> Edit(Action<UcConfig> change)
    {
        var cfg = await _client.GetConfigAsync().ConfigureAwait(false);
        change(cfg);
        await PrintSetConfig(await _client.SetConfigAsync(cfg).ConfigureAwait(false)).ConfigureAwait(false);
        return Ok;
    }

    private Task<int> Edit(Func<UcConfig, string> change) => Edit(cfg => { change(cfg); });

    private async Task PrintSetConfig(SetConfigResult r)
    {
        foreach (var removed in r.Removed)
        {
            await (removed.Success ? Line("Cli.AccountRemoved", removed.User) : Line("Cli.AccountFailed", removed.User, removed.Error)).ConfigureAwait(false);
        }
        if (r.Applied is null) await Line("Cli.SavedNotApplied").ConfigureAwait(false);
        else await PrintApply(r.Applied).ConfigureAwait(false);
    }

    private async Task PrintApply(ApplyResult r)
    {
        foreach (var acc in r.Accounts)
        {
            if (!acc.Success)
            {
                await Line("Cli.AccountFailed", acc.User, acc.Error).ConfigureAwait(false);
                continue;
            }
            await Line("Cli.AccountApplied", acc.User, acc.BlockedPaths, acc.AllowedPaths, acc.BlockedNames, acc.BlockedFiles).ConfigureAwait(false);
            if (acc.SignedIn) await Line("Cli.AccountSignedInHint", acc.User).ConfigureAwait(false);
        }
        foreach (var f in r.FileAccess.Where(f => f.Error is not null)) await Line("Cli.FileAccessFailed", f.Path, f.Account, f.Error).ConfigureAwait(false);
        if (r.Accounts.Count == 0) await Line("Cli.Done").ConfigureAwait(false);
    }

    private Task Line(string key, params object?[] args) => _out.WriteLineAsync(Loc.T(key, args));

    private async Task<int> Json<T>(T value)
    {
        await _out.WriteLineAsync(JsonSerializer.Serialize(value, IpcJson.Options)).ConfigureAwait(false);
        return Ok;
    }

    private static string Required(string? value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new UsageException(Loc.T("Cli.MissingArgument", what)) : value;

    private static string Local(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string YesNo(bool v) => Loc.T(v ? "Cli.Yes" : "Cli.No");

    private static RuleCategory Category(string? value) =>
        value is null ? RuleCategory.Other
        : Enum.TryParse<RuleCategory>(value, ignoreCase: true, out var c) && Enum.IsDefined(c) && c != RuleCategory.Writable ? c
        : throw new UcException(ErrorCodes.InvalidArgument, value, "--category");

    private static BrowserKind Browser(string? value) =>
        Enum.TryParse<BrowserKind>(Required(value, "chrome|edge|firefox"), ignoreCase: true, out var b) && Enum.IsDefined(b) ? b
        : throw new UcException(ErrorCodes.InvalidArgument, value ?? "", "chrome|edge|firefox");

    private static FileAccessRights Rights(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return FileAccessRights.Full;
        var r = FileAccessRights.None;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<FileAccessRights>(part, ignoreCase: true, out var p) || p == FileAccessRights.None || int.TryParse(part, out _))
                throw new UcException(ErrorCodes.InvalidArgument, part, "--rights");
            r |= p;
        }
        return r;
    }
}
