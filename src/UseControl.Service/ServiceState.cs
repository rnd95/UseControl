using System.Text.Json;
using UseControl.Core.Configuration;
using UseControl.Core.Games;
using UseControl.Windows;

namespace UseControl.Service;

/// <summary>What the service remembers between restarts (state.json next to config.json).</summary>
public sealed class ServiceState
{
    /// <summary>False after "suspend": restrictions are off and are not put back until "apply".</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Games time of each restricted account: what an administrator allowed, what was played today.</summary>
    public Dictionary<string, GamesState> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? LastApplied { get; set; }

    public List<AccountResult> LastResults { get; set; } = [];

    public static ServiceState Load(string path)
    {
        if (!File.Exists(path)) return new ServiceState();
        try
        {
            var s = JsonSerializer.Deserialize<ServiceState>(File.ReadAllText(path), ConfigSerializer.Options) ?? new ServiceState();
            s.Games = new Dictionary<string, GamesState>(s.Games ?? [], StringComparer.OrdinalIgnoreCase);
            s.LastResults ??= [];
            return s;
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
