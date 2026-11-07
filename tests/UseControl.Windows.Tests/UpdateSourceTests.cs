using System.Net;
using System.Security.Cryptography;
using System.Text;
using UseControl.Core;
using UseControl.Core.Updates;
using UseControl.Windows.Updates;

namespace UseControl.Windows.Tests;

public sealed class UpdateSourceTests : IDisposable
{
    private const string Prefix = "https://github.com/rnd95/UseControl/releases/download/";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uc-upd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string Release(string tag, bool prerelease = false, string? msiUrl = null) => $$"""
        {
          "tag_name": "{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/rnd95/UseControl/releases/tag/{{tag}}",
          "assets": [
            { "name": "UseControl-{{tag.TrimStart('v')}}-x64.msi", "browser_download_url": "{{msiUrl ?? Prefix + tag + "/UseControl-" + tag.TrimStart('v') + "-x64.msi"}}" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "{{Prefix + tag}}/SHA256SUMS.txt" }
          ]
        }
        """;

    [Fact]
    public void A_final_release_with_an_installer_from_the_repository_is_used()
    {
        var r = GitHubUpdateSource.ParseRelease(Release("v1.2.0"), Prefix)!;
        Assert.Equal("1.2.0", r.Version);
        Assert.Equal("UseControl-1.2.0-x64.msi", r.MsiName);
        Assert.StartsWith(Prefix, r.MsiUrl, StringComparison.Ordinal);
        Assert.EndsWith("/tag/v1.2.0", r.PageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Pre_releases_and_installers_from_elsewhere_are_ignored()
    {
        Assert.Null(GitHubUpdateSource.ParseRelease(Release("v1.2.0", prerelease: true), Prefix));
        Assert.Null(GitHubUpdateSource.ParseRelease(Release("v1.2.0-rc1"), Prefix));
        Assert.Null(GitHubUpdateSource.ParseRelease(Release("v1.2.0", msiUrl: "https://evil.example/UseControl-1.2.0-x64.msi"), Prefix));
    }

    [Fact]
    public async Task The_latest_release_gets_its_checksum_and_a_download_is_kept_only_when_it_matches()
    {
        var msi = Encoding.ASCII.GetBytes("pretend installer");
        var hash = Convert.ToHexString(SHA256.HashData(msi));
        var handler = new Answers(new Dictionary<string, byte[]>
        {
            ["https://api.github.com/repos/rnd95/UseControl/releases/latest"] = Encoding.UTF8.GetBytes(Release("v9.0.0")),
            [Prefix + "v9.0.0/SHA256SUMS.txt"] = Encoding.UTF8.GetBytes($"{hash}  UseControl-9.0.0-x64.msi\n"),
            [Prefix + "v9.0.0/UseControl-9.0.0-x64.msi"] = msi,
        });
        using var source = new GitHubUpdateSource(handler: handler, program: null);

        var r = (await source.LatestAsync(default))!;
        Assert.Equal(hash, r.Sha256);
        var file = await source.DownloadAsync(r, _dir, default);
        Assert.Equal(msi, File.ReadAllBytes(file));

        // A different file: refused and nothing is left behind.
        var e = await Assert.ThrowsAsync<UcException>(() => source.DownloadAsync(r with { Sha256 = new string('0', 64) }, _dir, default));
        Assert.Equal(ErrorCodes.UpdateNotVerified, e.Code);
        Assert.Empty(Directory.GetFiles(_dir));

        // Without a checksum nothing is downloaded at all.
        e = await Assert.ThrowsAsync<UcException>(() => source.DownloadAsync(r with { Sha256 = null }, _dir, default));
        Assert.Equal(ErrorCodes.UpdateNotVerified, e.Code);
    }

    [Fact]
    public async Task No_release_yet_is_not_an_error_but_a_network_failure_is()
    {
        using var none = new GitHubUpdateSource(handler: new Answers([]));
        Assert.Null(await none.LatestAsync(default));

        using var broken = new GitHubUpdateSource(handler: new Answers([], HttpStatusCode.InternalServerError));
        var e = await Assert.ThrowsAsync<UcException>(() => broken.LatestAsync(default));
        Assert.Equal(ErrorCodes.UpdateCheckFailed, e.Code);
    }

    [Fact]
    public void When_the_running_program_is_signed_an_unsigned_update_is_refused()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "x.msi");
        File.WriteAllText(file, "x");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        GitHubUpdateSource.Verify(file, hash, null);   // unsigned UseControl: the checksum is enough
        var signed = Environment.ProcessPath!;   // the test host, signed by Microsoft
        Assert.NotNull(Signature.Publisher(signed));
        Assert.Equal(ErrorCodes.UpdateNotVerified, Assert.Throws<UcException>(() => GitHubUpdateSource.Verify(file, hash, signed)).Code);
    }

    [Fact]
    public void Unsigned_files_have_no_publisher() => Assert.Null(Signature.Publisher(typeof(UpdateSourceTests).Assembly.Location));

    /// <summary>Answers requests from a table; anything else gets <paramref name="missing"/>.</summary>
    private sealed class Answers(Dictionary<string, byte[]> answers, HttpStatusCode missing = HttpStatusCode.NotFound) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var found = answers.TryGetValue(request.RequestUri!.ToString(), out var body);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : missing) { Content = new ByteArrayContent(body ?? []) });
        }
    }
}
