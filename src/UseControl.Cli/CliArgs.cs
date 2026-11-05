namespace UseControl.Cli;

/// <summary>
/// "uc block folder D:\Games --category games --yes": words in order, plus --name value options and
/// --flag switches. Values with spaces come quoted from the shell.
/// </summary>
internal sealed class CliArgs
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase) { "category", "rights", "users", "lang" };

    private readonly List<string> _words = [];
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public CliArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                var name = a[2..];
                var eq = name.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0) _options[name[..eq]] = name[(eq + 1)..];
                else if (ValueOptions.Contains(name) && i + 1 < args.Count) _options[name] = args[++i];
                else _flags.Add(name);
            }
            else
            {
                _words.Add(a);
            }
        }
    }

    public int Count => _words.Count;

    /// <summary>The n-th word in lower case (for command names), or "" when missing.</summary>
    public string Word(int index) => index < _words.Count ? _words[index].ToLowerInvariant() : "";

    /// <summary>The n-th word as typed, or null.</summary>
    public string? Value(int index) => index < _words.Count ? _words[index] : null;

    /// <summary>Everything from the n-th word on, joined with spaces (for unquoted paths and values).</summary>
    public string? Rest(int index) => index < _words.Count ? string.Join(' ', _words.Skip(index)) : null;

    public string? Option(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public bool Flag(string name) => _flags.Contains(name);
}
