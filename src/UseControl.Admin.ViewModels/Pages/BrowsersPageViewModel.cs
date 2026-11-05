using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Core.Configuration;
using UseControl.Core.Validation;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Pages;

public sealed record PolicyRow(string Name, string Value);

public sealed partial class BrowsersPageViewModel : PageViewModel
{
    private bool _loading;

    public BrowsersPageViewModel(PanelSession session) : base(session, "Ui.Nav.Browsers", "") { }

    public static IReadOnlyList<BrowserKind> Browsers { get; } = [BrowserKind.Chrome, BrowserKind.Edge, BrowserKind.Firefox];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirefox))]
    private BrowserKind _browser = BrowserKind.Chrome;

    public bool IsFirefox => Browser == BrowserKind.Firefox;

    public ObservableCollection<string> Extensions { get; } = [];

    public ObservableCollection<PolicyRow> Policies { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddExtensionCommand))]
    private string _newExtension = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetPolicyCommand))]
    private string _policyName = "";

    private bool HasExtension() => !string.IsNullOrWhiteSpace(NewExtension);

    private bool HasPolicyName() => !string.IsNullOrWhiteSpace(PolicyName);

    [ObservableProperty]
    private string _policyValue = "";

    [ObservableProperty]
    private bool _blockExtensions;

    [ObservableProperty]
    private bool _disableDnsOverHttps;

    [ObservableProperty]
    private bool _disablePrivateBrowsing;

    partial void OnBrowserChanged(BrowserKind value) => Rebuild();

    // Switches are saved as soon as they are clicked.
    partial void OnBlockExtensionsChanged(bool value) => SaveSwitch(c => Settings(c).block(value));

    partial void OnDisableDnsOverHttpsChanged(bool value) => SaveSwitch(c => c.Browsers.Firefox.DisableDnsOverHttps = value);

    partial void OnDisablePrivateBrowsingChanged(bool value) => SaveSwitch(c => c.Browsers.Firefox.DisablePrivateBrowsing = value);

    private void SaveSwitch(Action<UcConfig> change)
    {
        if (_loading) return;
        _ = Session.EditAsync(change);
    }

    [RelayCommand(CanExecute = nameof(HasExtension))]
    private async Task AddExtension()
    {
        var b = Browser;
        var id = NewExtension;
        if (await Session.EditAsync(c => ConfigEditor.AddExtension(c, b, id)).ConfigureAwait(true)) NewExtension = "";
    }

    [RelayCommand]
    private Task RemoveExtension(string? id)
    {
        var b = Browser;
        return id is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.RemoveExtension(c, b, id));
    }

    [RelayCommand(CanExecute = nameof(HasPolicyName))]
    private async Task SetPolicy()
    {
        var b = Browser;
        var name = PolicyName;
        var value = PolicyValue;
        if (await Session.EditAsync(c => ConfigEditor.SetBrowserPolicy(c, b, name, value)).ConfigureAwait(true))
        {
            PolicyName = "";
            PolicyValue = "";
        }
    }

    [RelayCommand]
    private void EditPolicy(PolicyRow? row)
    {
        if (row is null) return;
        PolicyName = row.Name;
        PolicyValue = row.Value;
    }

    [RelayCommand]
    private Task RemovePolicy(PolicyRow? row)
    {
        var b = Browser;
        return row is null ? Task.CompletedTask : Session.EditAsync(c => ConfigEditor.RemoveBrowserPolicy(c, b, row.Name));
    }

    private (List<string> extensions, Dictionary<string, JsonElement> policies, bool blocked, Action<bool> block) Settings(UcConfig c) => Browser switch
    {
        BrowserKind.Chrome => (c.Browsers.Chrome.ExtensionsAllowed, c.Browsers.Chrome.Policies, c.Browsers.Chrome.BlockExtensions, v => c.Browsers.Chrome.BlockExtensions = v),
        BrowserKind.Edge => (c.Browsers.Edge.ExtensionsAllowed, c.Browsers.Edge.Policies, c.Browsers.Edge.BlockExtensions, v => c.Browsers.Edge.BlockExtensions = v),
        _ => (c.Browsers.Firefox.ExtensionsAllowed, c.Browsers.Firefox.Policies, c.Browsers.Firefox.BlockExtensions, v => c.Browsers.Firefox.BlockExtensions = v),
    };

    public static string ValueText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Null => "null",
        _ => v.GetRawText(),
    };

    protected override void Rebuild()
    {
        var c = Session.Config;
        if (c is null) return;
        _loading = true;
        try
        {
            var s = Settings(c);
            Replace(Extensions, s.extensions);
            Replace(Policies, s.policies.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new PolicyRow(p.Key, ValueText(p.Value))));
            BlockExtensions = s.blocked;
            DisableDnsOverHttps = c.Browsers.Firefox.DisableDnsOverHttps;
            DisablePrivateBrowsing = c.Browsers.Firefox.DisablePrivateBrowsing;
        }
        finally
        {
            _loading = false;
        }
    }
}
