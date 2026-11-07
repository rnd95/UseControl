using Microsoft.Extensions.Logging.Abstractions;
using UseControl.Core;
using UseControl.Core.Updates;

namespace UseControl.Service.Tests;

internal sealed class FakeUpdateSource : IUpdateSource
{
    public ReleaseInfo? Latest { get; set; }
    public UcException? CheckError { get; set; }
    public UcException? DownloadError { get; set; }
    public bool CanInstall { get; set; } = true;
    public int Checks { get; private set; }
    public List<string> Started { get; } = [];

    public Task<ReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        Checks++;
        return CheckError is { } e ? Task.FromException<ReleaseInfo?>(e) : Task.FromResult(Latest);
    }

    public Task<string> DownloadAsync(ReleaseInfo release, string folder, CancellationToken ct) =>
        DownloadError is { } e ? Task.FromException<string>(e) : Task.FromResult(Path.Combine(folder, release.MsiName));

    public void StartInstall(string installer, string log) => Started.Add(installer);
}

public sealed class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uc-updater-" + Guid.NewGuid().ToString("N"));
    private readonly FakeUpdateSource _source = new();
    private readonly ManualTime _time = new();
    private readonly UpdateSettings _settings = new();

    public UpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Updater New() => new(_dir, _source, _time, () => _settings, "1.0.0", NullLogger.Instance, TimeSpan.Zero);

    private static ReleaseInfo Release(string v) => new(v, "https://github.com/rnd95/UseControl/releases/tag/v" + v, ReleaseVersion.MsiName(v), "https://github.com/x", new string('A', 64));

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
    }

    [Fact]
    public async Task A_check_shows_a_newer_version_and_remembers_it_after_a_restart()
    {
        using var u = New();
        Assert.Null(u.Status.LastCheck);
        _source.Latest = Release("1.1.0");
        var st = await u.CheckAsync(default);
        Assert.True(st.Available);
        Assert.Equal("1.1.0", st.Latest);
        Assert.Equal("1.0.0", st.Current);

        using var restarted = New();
        Assert.True(restarted.Status.Available);
        Assert.NotNull(restarted.Status.LastCheck);
    }

    [Fact]
    public async Task The_same_or_an_older_version_is_not_offered()
    {
        using var u = New();
        _source.Latest = Release("1.0.0");
        Assert.False((await u.CheckAsync(default)).Available);
        var e = await Assert.ThrowsAsync<UcException>(() => u.InstallAsync(default));
        Assert.Equal(ErrorCodes.NoUpdate, e.Code);
        Assert.Empty(_source.Started);
    }

    [Fact]
    public async Task Installing_downloads_and_starts_the_installer_once()
    {
        using var u = New();
        _source.Latest = Release("1.1.0");
        var st = await u.InstallAsync(default);
        Assert.True(st.Installing);
        await Until(() => _source.Started.Count > 0);
        Assert.Equal(Path.Combine(_dir, "updates", "UseControl-1.1.0-x64.msi"), Assert.Single(_source.Started));

        var e = await Assert.ThrowsAsync<UcException>(() => u.InstallAsync(default));
        Assert.Equal(ErrorCodes.UpdateInProgress, e.Code);
    }

    [Fact]
    public async Task A_copy_installed_without_the_installer_does_not_update_itself()
    {
        using var u = New();
        _source.Latest = Release("1.1.0");
        _source.CanInstall = false;
        var e = await Assert.ThrowsAsync<UcException>(() => u.InstallAsync(default));
        Assert.Equal(ErrorCodes.UpdateNeedsInstaller, e.Code);
        Assert.False((await u.CheckAsync(default)).CanInstall);
    }

    [Fact]
    public async Task A_failed_check_is_reported_and_a_failed_download_installs_nothing()
    {
        using var u = New();
        _source.CheckError = new UcException(ErrorCodes.UpdateCheckFailed, "no network");
        var st = await u.CheckAsync(default);
        Assert.Equal(ErrorCodes.UpdateCheckFailed, st.ErrorCode);
        Assert.Equal(["no network"], st.ErrorArgs);

        _source.CheckError = null;
        _source.Latest = Release("1.1.0");
        _source.DownloadError = new UcException(ErrorCodes.UpdateNotVerified, "SHA-256");
        var e = await Assert.ThrowsAsync<UcException>(() => u.InstallAsync(default));
        Assert.Equal(ErrorCodes.UpdateNotVerified, e.Code);
        Assert.False(u.Status.Installing);
        Assert.Empty(_source.Started);
    }

    [Fact]
    public async Task The_daily_check_runs_once_a_day_and_installs_only_when_switched_on()
    {
        using var u = New();
        _source.Latest = Release("1.1.0");
        await u.TickAsync(default);
        await u.TickAsync(default);
        Assert.Equal(1, _source.Checks);
        Assert.Empty(_source.Started);   // found, but not installed by itself

        _settings.InstallAutomatically = true;
        _time.Advance(TimeSpan.FromHours(25));
        await u.TickAsync(default);
        await Until(() => _source.Started.Count > 0);
        Assert.Single(_source.Started);
    }

    [Fact]
    public async Task An_automatic_installation_that_failed_is_not_repeated_for_the_same_version()
    {
        using var u = New();
        _settings.InstallAutomatically = true;
        _source.Latest = Release("1.1.0");
        _source.DownloadError = new UcException(ErrorCodes.UpdateNotVerified, "SHA-256");
        await u.TickAsync(default);
        Assert.Equal(ErrorCodes.UpdateNotVerified, u.Status.ErrorCode);

        _source.DownloadError = null;
        _time.Advance(TimeSpan.FromDays(2));
        await u.TickAsync(default);
        Assert.Empty(_source.Started);

        _source.Latest = Release("1.2.0");   // a newer one is tried
        _time.Advance(TimeSpan.FromDays(2));
        await u.TickAsync(default);
        await Until(() => _source.Started.Count > 0);
        Assert.Single(_source.Started);
    }

    [Fact]
    public async Task Nothing_is_checked_when_both_switches_are_off()
    {
        using var u = New();
        _settings.CheckDaily = false;
        await u.TickAsync(default);
        Assert.Equal(0, _source.Checks);
    }
}
