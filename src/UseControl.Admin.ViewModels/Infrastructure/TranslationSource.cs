using System.ComponentModel;
using System.Globalization;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Infrastructure;

/// <summary>
/// The texts of the interface as a bindable object: views bind to <c>this[key]</c>, and changing
/// <see cref="Culture"/> refreshes every bound text at once, without restarting.
/// </summary>
public sealed class TranslationSource : INotifyPropertyChanged
{
    public static TranslationSource Instance { get; } = new();

    private TranslationSource() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed, for view models that build texts themselves.</summary>
    public event EventHandler? CultureChanged;

    public string this[string key] => Loc.T(key);

    public CultureInfo Culture
    {
        get => Loc.Culture;
        set
        {
            if (Equals(Loc.Culture, value)) return;
            Loc.Culture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
            CultureChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
