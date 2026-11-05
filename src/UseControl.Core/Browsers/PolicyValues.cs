using System.Globalization;
using System.Text.Json;
using UseControl.Core.Configuration;

namespace UseControl.Core.Browsers;

public enum PolicyValueKind
{
    DWord,

    /// <summary>REG_SZ.</summary>
    Text,
}

/// <summary>A browser policy as it goes into the registry.</summary>
public sealed record PolicyValue(string Name, PolicyValueKind Kind, object Data);

public static class PolicyValues
{
    /// <summary>
    /// Converts the policies of the config to registry values: numbers and true/false become DWORD,
    /// text stays text, null means "not set" and is skipped.
    /// </summary>
    public static IReadOnlyList<PolicyValue> FromConfig(IReadOnlyDictionary<string, JsonElement> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var list = new List<PolicyValue>();
        foreach (var (name, v) in policies.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    continue;
                case JsonValueKind.True:
                    list.Add(new PolicyValue(name, PolicyValueKind.DWord, 1));
                    break;
                case JsonValueKind.False:
                    list.Add(new PolicyValue(name, PolicyValueKind.DWord, 0));
                    break;
                case JsonValueKind.Number when v.TryGetInt32(out var i):
                    list.Add(new PolicyValue(name, PolicyValueKind.DWord, i));
                    break;
                case JsonValueKind.String:
                    list.Add(new PolicyValue(name, PolicyValueKind.Text, v.GetString()!));
                    break;
                default:
                    throw new UcException(ErrorCodes.InvalidPolicyValue, name);
            }
        }
        return list;
    }

    /// <summary>Firefox ExtensionSettings: everything blocked except the allowed IDs.</summary>
    public static string FirefoxExtensionSettings(FirefoxSettings firefox)
    {
        ArgumentNullException.ThrowIfNull(firefox);
        var settings = new Dictionary<string, object>
        {
            ["*"] = new Dictionary<string, string>
            {
                ["installation_mode"] = "blocked",
                ["blocked_install_message"] = "Ask the administrator to allow this extension.",
            },
        };
        foreach (var id in firefox.ExtensionsAllowed) settings[id] = new Dictionary<string, string> { ["installation_mode"] = "allowed" };
        return JsonSerializer.Serialize(settings);
    }

    public static string Describe(PolicyValue v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Kind == PolicyValueKind.DWord
            ? string.Format(CultureInfo.InvariantCulture, "{0} = {1} (DWORD)", v.Name, v.Data)
            : string.Format(CultureInfo.InvariantCulture, "{0} = \"{1}\"", v.Name, v.Data);
    }
}
