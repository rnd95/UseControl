using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace UseControl.Core.Configuration;

/// <summary>Reads and writes config.json, upgrading files written by older versions.</summary>
public static class ConfigSerializer
{
    /// <summary>
    /// Version of the config format. Increase it when the format changes and add a step to
    /// <see cref="Migrate"/>. 0 = the PowerShell version (no schemaVersion field).
    /// </summary>
    public const int CurrentSchemaVersion = 1;

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

    /// <summary>The configuration a new installation starts with (games, VPNs and tools of common programs).</summary>
    public static UcConfig LoadDefault()
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

        obj["schemaVersion"] = version;
    }

    /// <summary>Replaces nulls (from "key": null in the file) with empty values, so code never meets them.</summary>
    internal static UcConfig Normalize(UcConfig c)
    {
        c.Users = [.. (c.Users ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim())];
        c.Auto ??= new AutoBlockOptions();
        c.BlockedPaths = [.. (c.BlockedPaths ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path))];
        c.AllowedPaths = [.. (c.AllowedPaths ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path))];
        c.BlockedNames = [.. (c.BlockedNames ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Name))];
        c.BlockedFiles = [.. (c.BlockedFiles ?? []).Where(e => e is not null)];
        c.FileAccess = [.. (c.FileAccess ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path))];
        foreach (var r in c.FileAccess) r.Users ??= [];

        c.Browsers ??= new BrowserSettings();
        c.Browsers.Chrome = NormalizeChromium(c.Browsers.Chrome);
        c.Browsers.Edge = NormalizeChromium(c.Browsers.Edge);
        c.Browsers.Firefox ??= new FirefoxSettings();
        c.Browsers.Firefox.ExtensionsAllowed = Clean(c.Browsers.Firefox.ExtensionsAllowed);
        c.Browsers.Firefox.Policies ??= [];

        c.Audit ??= new AuditSettings();
        c.Audit.Admins = Clean(c.Audit.Admins);
        c.Audit.PrivateFolders = Clean(c.Audit.PrivateFolders);
        c.Audit.HiddenAccounts = Clean(c.Audit.HiddenAccounts);
        c.Audit.KnownRemoteServices = Clean(c.Audit.KnownRemoteServices);
        return c;

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
