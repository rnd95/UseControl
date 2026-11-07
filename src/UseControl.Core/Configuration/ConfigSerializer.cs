using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using UseControl.Core.Games;
using UseControl.Core.Updates;

namespace UseControl.Core.Configuration;

/// <summary>Reads and writes config.json, upgrading files written by older versions.</summary>
public static class ConfigSerializer
{
    /// <summary>
    /// Version of the config format. Increase it when the format changes and add a step to
    /// <see cref="Migrate"/>. 0 = the PowerShell version (no schemaVersion field); 1 = one set of rules for
    /// every account; 2 = presets.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Settings that belonged to the whole configuration in schema 1 and belong to a preset since schema 2.</summary>
    private static readonly string[] PresetProperties = ["auto", "restrictions", "blockedPaths", "allowedPaths", "blockedNames", "blockedFiles", "browsers"];

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            // Keep non-Latin folder names readable in the file (it is never embedded in HTML).
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    public static UcConfig Load(string path)
    {
        if (!File.Exists(path)) throw new UcException(ErrorCodes.ConfigNotFound, path);
        return Parse(File.ReadAllText(path), path);
    }

    public static UcConfig Parse(string json, string sourceName = "config.json")
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new UcException(ErrorCodes.ConfigInvalid, sourceName, e.Message);
        }
        if (node is not JsonObject obj) throw new UcException(ErrorCodes.ConfigInvalid, sourceName, "the top level must be an object");

        Migrate(obj, sourceName);

        UcConfig? cfg;
        try
        {
            cfg = obj.Deserialize<UcConfig>(Options);
        }
        catch (JsonException e)
        {
            throw new UcException(ErrorCodes.ConfigInvalid, sourceName, e.Message);
        }
        return Normalize(cfg ?? new UcConfig());
    }

    public static string Serialize(UcConfig config) => JsonSerializer.Serialize(config, Options);

    /// <summary>Writes the file through a temporary file, so a crash never leaves half a config.</summary>
    public static void Save(UcConfig config, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, Serialize(config), new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The configuration a new installation starts with: no restricted accounts, no presets yet.</summary>
    public static UcConfig LoadDefault()
    {
        var c = LoadDefaultFile();
        c.Presets.Clear();
        return c;
    }

    /// <summary>
    /// A new preset with the recommended settings: common games, VPNs and system tools blocked, safe
    /// browser settings, games only when an administrator allows them.
    /// </summary>
    public static Preset DefaultPreset(string name)
    {
        var p = LoadDefaultFile().Presets[0];
        p.Name = name;
        p.Accounts = [];
        return p;
    }

    /// <summary>A deep copy of a preset under another name, without its accounts.</summary>
    public static Preset CopyPreset(Preset source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = Parse(Serialize(new UcConfig { Presets = [source] })).Presets[0];
        copy.Name = name;
        copy.Accounts = [];
        return copy;
    }

    private static UcConfig LoadDefaultFile()
    {
        using var s = typeof(ConfigSerializer).Assembly.GetManifestResourceStream("UseControl.Core.config.default.json")
            ?? throw new InvalidOperationException("The default configuration is missing from the assembly.");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd(), "config.default.json");
    }

    /// <summary>Upgrades the JSON of an older file in place, one schema version at a time.</summary>
    internal static void Migrate(JsonObject obj, string sourceName)
    {
        var version = obj["schemaVersion"]?.GetValue<int>() ?? 0;
        if (version > CurrentSchemaVersion) throw new UcException(ErrorCodes.ConfigTooNew, sourceName, version, CurrentSchemaVersion);

        if (version < 1)
        {
            // PowerShell version: the first releases had a single "user" instead of "users".
            if (obj["users"] is null && obj["user"] is JsonValue single && single.TryGetValue<string>(out var name))
            {
                obj["users"] = new JsonArray(name);
            }
            obj.Remove("user");
            version = 1;
        }

        if (version < 2)
        {
            // Schema 1 had one set of rules for every account: it becomes one preset with all of them,
            // named after the account when there is only one.
            var users = obj["users"] as JsonArray ?? [];
            var names = users.Select(u => u is JsonValue v && v.TryGetValue<string>(out var n) ? n.Trim() : null)
                .Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList();
            var preset = new JsonObject
            {
                ["name"] = names.Count == 1 ? names[0] : "Default",
                ["accounts"] = new JsonArray([.. names.Select(n => (JsonNode)JsonValue.Create(n))]),
            };
            foreach (var key in PresetProperties)
            {
                var found = obj.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (found.Key is null) continue;
                var value = found.Value;
                obj.Remove(found.Key);
                preset[key] = value;
            }
            obj.Remove("users");
            // A file without accounts and without settings gets no preset.
            obj["presets"] = names.Count == 0 && preset.Count == 2 ? new JsonArray() : new JsonArray(preset);
            version = 2;
        }

        obj["schemaVersion"] = version;
    }

    /// <summary>Replaces nulls (from "key": null in the file) with empty values, so code never meets them.</summary>
    internal static UcConfig Normalize(UcConfig c)
    {
        c.Presets = [.. (c.Presets ?? []).Where(p => p is not null).Select(NormalizePreset)];
        c.FileAccess = [.. (c.FileAccess ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path))];
        foreach (var r in c.FileAccess) r.Users ??= [];

        c.Audit ??= new AuditSettings();
        c.Updates ??= new UpdateSettings();
        c.Audit.Admins = Clean(c.Audit.Admins);
        c.Audit.PrivateFolders = Clean(c.Audit.PrivateFolders);
        c.Audit.HiddenAccounts = Clean(c.Audit.HiddenAccounts);
        c.Audit.KnownRemoteServices = Clean(c.Audit.KnownRemoteServices);
        return c;

        static Preset NormalizePreset(Preset p)
        {
            p.Name = (p.Name ?? "").Trim();
            p.Accounts = Clean(p.Accounts);
            p.Auto ??= new AutoBlockOptions();
            p.Restrictions ??= new AccountRestrictions();
            p.BlockedPaths = [.. (p.BlockedPaths ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path))];
            p.AllowedPaths = [.. (p.AllowedPaths ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path))];
            p.BlockedNames = [.. (p.BlockedNames ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Name))];
            p.BlockedFiles = [.. (p.BlockedFiles ?? []).Where(e => e is not null)];
            p.Games ??= new GamesSettings();
            p.Games.Schedule = [.. (p.Games.Schedule ?? []).Where(w => w is not null)];
            foreach (var w in p.Games.Schedule) w.Days = [.. (w.Days ?? []).Distinct().Order()];

            p.Browsers ??= new BrowserSettings();
            p.Browsers.Chrome = NormalizeChromium(p.Browsers.Chrome);
            p.Browsers.Edge = NormalizeChromium(p.Browsers.Edge);
            p.Browsers.Firefox ??= new FirefoxSettings();
            p.Browsers.Firefox.ExtensionsAllowed = Clean(p.Browsers.Firefox.ExtensionsAllowed);
            p.Browsers.Firefox.Policies ??= [];
            return p;
        }

        static ChromiumSettings NormalizeChromium(ChromiumSettings? b)
        {
            b ??= new ChromiumSettings();
            b.ExtensionsAllowed = Clean(b.ExtensionsAllowed);
            b.Policies ??= [];
            return b;
        }

        static List<string> Clean(List<string>? l) => [.. (l ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())];
    }
}
