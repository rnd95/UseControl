using System.Globalization;

namespace UseControl.Core.Updates;

/// <summary>How UseControl keeps itself up to date (config.json, "updates").</summary>
public sealed class UpdateSettings
{
    /// <summary>The service looks for a new release once a day.</summary>
    public bool CheckDaily { get; set; } = true;

    /// <summary>A new release found by the daily check is installed without asking.</summary>
    public bool InstallAutomatically { get; set; }
}

/// <summary>A published release with its installer.</summary>
/// <param name="Version">"1.2.3".</param>
/// <param name="PageUrl">The release page (what is new).</param>
/// <param name="MsiName">File name of the installer.</param>
/// <param name="MsiUrl">Where the installer is downloaded from.</param>
/// <param name="Sha256">The installer's SHA-256 from the release's checksum file, or null when the release has none.</param>
public sealed record ReleaseInfo(string Version, string PageUrl, string MsiName, string MsiUrl, string? Sha256);

/// <summary>Where releases come from and how one is installed. The service uses GitHub Releases; tests use a fake.</summary>
public interface IUpdateSource
{
    /// <summary>The newest published release that has an installer, or null when there is none.</summary>
    Task<ReleaseInfo?> LatestAsync(CancellationToken ct);

    /// <summary>Downloads the installer into <paramref name="folder"/>, checks it and returns its path. Throws <see cref="UcException"/> when it cannot be trusted.</summary>
    Task<string> DownloadAsync(ReleaseInfo release, string folder, CancellationToken ct);

    /// <summary>Starts the installer and returns at once (the installer stops and restarts the service).</summary>
    void StartInstall(string installer, string log);

    /// <summary>True when this copy was installed by the installer (only such a copy can update itself).</summary>
    bool CanInstall { get; }
}

/// <summary>Release versions, file names and checksum files.</summary>
public static class ReleaseVersion
{
    public const string ChecksumFile = "SHA256SUMS.txt";

    /// <summary>"v1.2.3" or "1.2.3" as a version; null for anything else (pre-releases such as "1.2.3-beta" included).</summary>
    public static Version? Parse(string? tag)
    {
        var t = (tag ?? "").Trim().TrimStart('v', 'V');
        var parts = t.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return null;
        return Version.TryParse(t, out var v) ? v : null;
    }

    /// <summary>"1.2.3" from a version (the build and revision parts are left out).</summary>
    public static string Format(Version v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}");
    }

    public static bool IsNewer(string candidate, string current) =>
        Parse(candidate) is { } c && Parse(current) is { } cur && c > cur;

    /// <summary>The installer of a release: UseControl-1.2.3-x64.msi.</summary>
    public static string MsiName(string version) => $"UseControl-{version}-x64.msi";

    /// <summary>The SHA-256 of a file in a checksum file ("hash  name" or "hash *name" per line), or null.</summary>
    public static string? HashFor(string checksums, string fileName)
    {
        ArgumentNullException.ThrowIfNull(checksums);
        foreach (var line in checksums.Split('\n'))
        {
            var l = line.Trim();
            var space = l.IndexOf(' ', StringComparison.Ordinal);
            if (space != 64) continue;
            var name = l[space..].Trim().TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && Validation.Validators.IsHex(l[..64], 64)) return l[..64].ToUpperInvariant();
        }
        return null;
    }
}
