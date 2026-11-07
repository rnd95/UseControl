using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using UseControl.Core;
using UseControl.Core.Updates;

namespace UseControl.Windows.Updates;

/// <summary>
/// Releases from GitHub: the latest release of the repository, its installer (UseControl-x.y.z-x64.msi)
/// and its checksum file (SHA256SUMS.txt). A download is used only when its SHA-256 matches the checksum
/// file and, when this copy is signed, when the installer is signed by the same publisher.
/// </summary>
public sealed class GitHubUpdateSource : IUpdateSource, IDisposable
{
    public const string DefaultRepository = "rnd95/UseControl";

    /// <summary>Written by the installer; a copy installed with "uc install" has no such value.</summary>
    public const string InstallKey = @"SOFTWARE\UseControl";

    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly string? _program;

    /// <param name="program">The running program: when it is signed, updates must be signed by the same publisher. Default: this process.</param>
    public GitHubUpdateSource(string repository = DefaultRepository, HttpMessageHandler? handler = null, string? program = "")
    {
        _repository = repository;
        _program = program == "" ? Environment.ProcessPath : program;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(10);
        var version = typeof(GitHubUpdateSource).Assembly.GetName().Version?.ToString(3) ?? "0";
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UseControl", version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    private string DownloadPrefix => $"https://github.com/{_repository}/releases/download/";

    public bool CanInstall
    {
        get
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(InstallKey);
            return key?.GetValue("InstalledBy") is "msi";
        }
    }

    public async Task<ReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri($"https://api.github.com/repos/{_repository}/releases/latest"), ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // nothing published yet
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = ParseRelease(json, DownloadPrefix);
            if (release is null) return null;
            if (release.Sha256 is null && FindAsset(json, ReleaseVersion.ChecksumFile) is { } sumsUrl && sumsUrl.StartsWith(DownloadPrefix, StringComparison.Ordinal))
            {
                var sums = await _http.GetStringAsync(new Uri(sumsUrl), ct).ConfigureAwait(false);
                release = release with { Sha256 = ReleaseVersion.HashFor(sums, release.MsiName) };
            }
            return release;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw new UcException(ErrorCodes.UpdateCheckFailed, e.Message);
        }
    }

    /// <summary>The release in the answer of the GitHub API, when it is a final release with an installer from this repository.</summary>
    public static ReleaseInfo? ParseRelease(string json, string downloadPrefix)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        if (ReleaseVersion.Parse(Text(r, "tag_name")) is not { } v) return null;
        var version = ReleaseVersion.Format(v);
        var msiName = ReleaseVersion.MsiName(version);
        var msiUrl = FindAsset(json, msiName);
        if (msiUrl is null || !msiUrl.StartsWith(downloadPrefix, StringComparison.Ordinal)) return null;
        return new ReleaseInfo(version, Text(r, "html_url") ?? "", msiName, msiUrl, null);
    }

    private static string? FindAsset(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        return assets.EnumerateArray()
            .Where(a => string.Equals(Text(a, "name"), name, StringComparison.OrdinalIgnoreCase))
            .Select(a => Text(a, "browser_download_url"))
            .FirstOrDefault();
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    public async Task<string> DownloadAsync(ReleaseInfo release, string folder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!release.MsiUrl.StartsWith(DownloadPrefix, StringComparison.Ordinal)) throw new UcException(ErrorCodes.UpdateNotVerified, release.MsiUrl);
        if (release.Sha256 is null) throw new UcException(ErrorCodes.UpdateNotVerified, ReleaseVersion.ChecksumFile);

        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder)) File.Delete(old);
        var target = Path.Combine(folder, release.MsiName);
        var part = target + ".part";
        try
        {
            using (var response = await _http.GetAsync(new Uri(release.MsiUrl), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (file.ConfigureAwait(false))
                {
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            File.Delete(part);
            throw new UcException(ErrorCodes.UpdateCheckFailed, e.Message);
        }

        try
        {
            Verify(part, release.Sha256, _program);
        }
        catch
        {
            File.Delete(part);
            throw;
        }
        File.Move(part, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// The file must have the expected SHA-256. When the running program is signed, the file must also
    /// carry a valid signature of the same publisher (so a changed release cannot replace UseControl).
    /// </summary>
    public static void Verify(string file, string sha256, string? runningProgram)
    {
        string actual;
        using (var s = File.OpenRead(file)) actual = Convert.ToHexString(SHA256.HashData(s));
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new UcException(ErrorCodes.UpdateNotVerified, "SHA-256");

        if (runningProgram is not null && Signature.Publisher(runningProgram) is { } publisher && Signature.Publisher(file) != publisher)
            throw new UcException(ErrorCodes.UpdateNotVerified, publisher);
    }

    public void StartInstall(string installer, string log)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "/i", installer, "/qn", "/norestart", "/l*v", log }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new UcException(ErrorCodes.UpdateCheckFailed, "msiexec");
    }

    public void Dispose() => _http.Dispose();
}
