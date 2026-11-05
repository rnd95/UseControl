using System.Text.Json;
using UseControl.Core.Configuration;
using UseControl.Windows;

namespace UseControl.Service;

/// <summary>What the service remembers between restarts (state.json next to config.json).</summary>
public sealed class ServiceState
{
    /// <summary>False after "suspend": restrictions are off and are not put back until "apply".</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset? GamesAllowedUntil { get; set; }

    public DateTimeOffset? LastApplied { get; set; }

    public List<AccountResult> LastResults { get; set; } = [];

    public static ServiceState Load(string path)
    {
        if (!File.Exists(path)) return new ServiceState();
        try
        {
            return JsonSerializer.Deserialize<ServiceState>(File.ReadAllText(path), ConfigSerializer.Options) ?? new ServiceState();
        }
        catch (JsonException)
        {
            // A damaged state file must not stop the protection: start from the safe defaults (enabled, no games time).
            return new ServiceState();
        }
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, ConfigSerializer.Options));
        File.Move(tmp, path, overwrite: true);
    }
}
