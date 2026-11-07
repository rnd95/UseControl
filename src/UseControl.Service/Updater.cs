using System.Text.Json;
using Microsoft.Extensions.Logging;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Core.Updates;
using UseControl.Ipc;

namespace UseControl.Service;

/// <summary>What the updater remembers between restarts (update.json next to config.json).</summary>
public sealed class UpdateState
{
    public DateTimeOffset? LastCheck { get; set; }

    public ReleaseInfo? Latest { get; set; }

    public string? ErrorCode { get; set; }

    public List<string> ErrorArgs { get; set; } = [];

    /// <summary>A version whose automatic installation failed: not tried again automatically.</summary>
    public string? FailedVersion { get; set; }
}

/// <summary>
/// Keeps UseControl up to date: looks for a new release once a day (when the settings allow it), and
/// installs it when an administrator asks or, if switched on, by itself. Installing = downloading the
/// installer, checking it, and starting it; the installer then stops the service, replaces the programs
/// and starts the new service. The restrictions stay in place all the time (they live in the registry).
/// </summary>
public sealed partial class Updater : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly IUpdateSource _source;
    private readonly TimeProvider _time;
    private readonly Func<UpdateSettings> _settings;
    private readonly ILogger _log;
    private readonly string _statePath;
    private readonly string _folder;
    private readonly TimeSpan _installDelay;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private UpdateState _state;
    private bool _installing;

    /// <param name="installDelay">Pause between answering "install" and starting the installer, so the answer reaches the panel first.</param>
    public Updater(string dataDirectory, IUpdateSource source, TimeProvider time, Func<UpdateSettings> settings, string currentVersion, ILogger log, TimeSpan installDelay)
    {
        _source = source;
        _time = time;
        _settings = settings;
        _log = log;
        Current = currentVersion;
        _statePath = Path.Combine(dataDirectory, "update.json");
        _folder = Path.Combine(dataDirectory, "updates");
        _installDelay = installDelay;
        _state = Load(_statePath);
    }

    /// <summary>The running version, "1.2.3".</summary>
    public string Current { get; }

    /// <summary>The installer has been started (the service is about to be stopped by it).</summary>
    public bool Installing => _installing;

    public UpdateStatus Status
    {
        get
        {
            var latest = _state.Latest;
            return new UpdateStatus(
                Current,
                latest?.Version,
                latest is not null && ReleaseVersion.IsNewer(latest.Version, Current),
                _state.LastCheck,
                _state.ErrorCode,
                _state.ErrorArgs,
                _installing,
                latest?.PageUrl,
                _source.CanInstall);
        }
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CheckLockedAsync(ct).ConfigureAwait(false);
            return Status;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Installs the newest release, checking for it first. Throws when there is nothing newer or it cannot be installed.</summary>
    public async Task<UpdateStatus> InstallAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_installing) throw new UcException(ErrorCodes.UpdateInProgress);
            if (!_source.CanInstall) throw new UcException(ErrorCodes.UpdateNeedsInstaller);
            await CheckLockedAsync(ct).ConfigureAwait(false);
            if (_state.ErrorCode is { } code) throw new UcException(code, [.. _state.ErrorArgs]);
            if (!Status.Available || _state.Latest is not { } release) throw new UcException(ErrorCodes.NoUpdate);
            await InstallLockedAsync(release, ct).ConfigureAwait(false);
            return Status;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Called every minute: the daily check, and the automatic installation when it is switched on.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var settings = _settings();
        if (!settings.CheckDaily && !settings.InstallAutomatically) return;
        if (_installing || _state.LastCheck is { } last && _time.GetUtcNow() - last < CheckInterval) return;
        if (!await _lock.WaitAsync(0, ct).ConfigureAwait(false)) return;   // an administrator is checking right now
        try
        {
            await CheckLockedAsync(ct).ConfigureAwait(false);
            if (settings.InstallAutomatically && _source.CanInstall && Status.Available && _state.Latest is { } release && release.Version != _state.FailedVersion)
            {
                try
                {
                    await InstallLockedAsync(release, ct).ConfigureAwait(false);
                }
                catch (UcException e)
                {
                    _state.FailedVersion = release.Version;
                    Fail(e);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task CheckLockedAsync(CancellationToken ct)
    {
        try
        {
            var latest = await _source.LatestAsync(ct).ConfigureAwait(false);
            _state.Latest = latest;
            _state.ErrorCode = null;
            _state.ErrorArgs = [];
            if (latest is not null && ReleaseVersion.IsNewer(latest.Version, Current)) LogFound(_log, latest.Version);
        }
        catch (UcException e)
        {
            Fail(e);
        }
        finally
        {
            _state.LastCheck = _time.GetUtcNow();
            Save();
        }
    }

    private async Task InstallLockedAsync(ReleaseInfo release, CancellationToken ct)
    {
        var installer = await _source.DownloadAsync(release, _folder, ct).ConfigureAwait(false);
        var log = Path.Combine(_folder, "install.log");
        _installing = true;
        LogInstalling(_log, release.Version);
        _ = Task.Run(async () =>
        {
            try
            {
                if (_installDelay > TimeSpan.Zero) await Task.Delay(_installDelay, CancellationToken.None).ConfigureAwait(false);
                _source.StartInstall(installer, log);
            }
            catch (Exception e) when (e is UcException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _installing = false;
                LogInstallFailed(_log, e.Message);
            }
        }, CancellationToken.None);
    }

    private void Fail(UcException e)
    {
        _state.ErrorCode = e.Code;
        _state.ErrorArgs = [.. e.Args.Select(a => a?.ToString() ?? "")];
        LogFailed(_log, e.Message);
        Save();
    }

    private static UpdateState Load(string path)
    {
        if (!File.Exists(path)) return new UpdateState();
        try
        {
            var s = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path), ConfigSerializer.Options) ?? new UpdateState();
            s.ErrorArgs ??= [];
            return s;
        }
        catch (JsonException)
        {
            return new UpdateState();
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, ConfigSerializer.Options));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogFailed(_log, e.Message);
        }
    }

    public void Dispose() => _lock.Dispose();

    [LoggerMessage(20, LogLevel.Information, "UseControl {Version} is available")]
    private static partial void LogFound(ILogger log, string version);

    [LoggerMessage(21, LogLevel.Warning, "Installing UseControl {Version}: the service restarts")]
    private static partial void LogInstalling(ILogger log, string version);

    [LoggerMessage(22, LogLevel.Warning, "Update: {Error}")]
    private static partial void LogFailed(ILogger log, string error);

    [LoggerMessage(23, LogLevel.Error, "The installer could not be started: {Error}")]
    private static partial void LogInstallFailed(ILogger log, string error);
}
