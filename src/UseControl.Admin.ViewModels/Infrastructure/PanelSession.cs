using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Core.Paths;
using UseControl.Ipc;
using UseControl.Localization;
using UseControl.Windows;

namespace UseControl.Admin.ViewModels.Infrastructure;

public enum MessageKind
{
    None,
    Info,
    Success,
    Error,
}

/// <summary>
/// What every page shares: the connection to the service, the current configuration and status,
/// the "busy" flag and the message bar. Every change goes through the service, which validates and applies it.
/// </summary>
public sealed partial class PanelSession : ObservableObject
{
    public PanelSession(IServiceClient client, IDialogService dialogs, ISystemPaths paths)
    {
        Client = client;
        Dialogs = dialogs;
        Paths = paths;
    }

    public IServiceClient Client { get; }

    public IDialogService Dialogs { get; }

    public ISystemPaths Paths { get; }

    [ObservableProperty]
    private UcConfig? _config;

    [ObservableProperty]
    private ServiceStatus? _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    /// <summary>Pages accept input only when connected and not busy.</summary>
    public bool CanEdit => IsConnected && !IsBusy;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private MessageKind _messageKind;

    /// <summary>True once the service answered; pages stay read-only until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isConnected;

    /// <summary>Raised after <see cref="Config"/> or <see cref="Status"/> were reloaded.</summary>
    public event EventHandler? Reloaded;

    public void ShowMessage(MessageKind kind, string? text)
    {
        MessageKind = text is null ? MessageKind.None : kind;
        Message = text;
    }

    public void ClearMessage() => ShowMessage(MessageKind.None, null);

    /// <summary>Runs an action with the busy flag on; any error ends up in the message bar.</summary>
    public async Task<bool> RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
            return true;
        }
        catch (UcException e)
        {
            ShowMessage(MessageKind.Error, Loc.Error(e));
            if (e.Code is ErrorCodes.ServiceNotRunning or ErrorCodes.ServiceNotTrusted) IsConnected = false;
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or System.Text.Json.JsonException or InvalidOperationException)
        {
            ShowMessage(MessageKind.Error, e.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task<bool> RefreshAsync() => RunAsync(ReloadAsync);

    /// <summary>
    /// Takes the service's current configuration, applies the change and sends it back. On success the
    /// result of applying is summarised in the message bar.
    /// </summary>
    public Task<bool> EditAsync(Action<UcConfig> change) => RunAsync(async () =>
    {
        var cfg = await Client.GetConfigAsync().ConfigureAwait(true);
        change(cfg);
        var result = await Client.SetConfigAsync(cfg).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
        ShowSetConfigResult(result);
    });

    public void ShowApplyResult(ApplyResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var problems = new StringBuilder();
        foreach (var a in r.Accounts.Where(a => !a.Success)) problems.AppendLine(Loc.T("Cli.AccountFailed", a.User, a.Error));
        foreach (var f in r.FileAccess.Where(f => f.Error is not null)) problems.AppendLine(Loc.T("Cli.FileAccessFailed", f.Path, f.Account, f.Error));
        if (problems.Length > 0)
        {
            ShowMessage(MessageKind.Error, problems.ToString().TrimEnd());
            return;
        }
        var signedIn = r.Accounts.Where(a => a.SignedIn).Select(a => a.User).ToList();
        ShowMessage(MessageKind.Success, signedIn.Count > 0
            ? Loc.T("Ui.SavedSignedIn", string.Join(", ", signedIn))
            : Loc.T("Ui.Saved"));
    }

    private void ShowSetConfigResult(SetConfigResult r)
    {
        if (r.Applied is null) ShowMessage(MessageKind.Info, Loc.T("Cli.SavedNotApplied"));
        else ShowApplyResult(r.Applied);
    }

    private async Task ReloadAsync()
    {
        Config = await Client.GetConfigAsync().ConfigureAwait(true);
        Status = await Client.GetStatusAsync().ConfigureAwait(true);
        IsConnected = true;
        Reloaded?.Invoke(this, EventArgs.Empty);
    }
}
