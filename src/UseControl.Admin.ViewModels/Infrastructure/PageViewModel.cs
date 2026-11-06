using CommunityToolkit.Mvvm.ComponentModel;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Infrastructure;

/// <summary>One page of the panel. Pages rebuild their lists when the configuration was reloaded or the language changed.</summary>
public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(PanelSession session, string titleKey, string icon)
    {
        Session = session;
        TitleKey = titleKey;
        Icon = icon;
        session.Reloaded += (_, _) => Rebuild();
        TranslationSource.Instance.CultureChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            Rebuild();
        };
    }

    public PanelSession Session { get; }

    /// <summary>Resource key of the page name.</summary>
    public string TitleKey { get; }

    public string Title => Loc.T(TitleKey);

    /// <summary>Glyph in the Segoe MDL2 Assets font.</summary>
    public string Icon { get; }

    /// <summary>True for pages that show the settings of one preset (the preset bar is shown above them).</summary>
    public virtual bool UsesPreset => false;

    /// <summary>Called when the page is shown: connects first if the panel is not connected yet.</summary>
    public virtual Task ActivateAsync() => Session.IsConnected ? Task.CompletedTask : Session.RefreshAsync();

    /// <summary>Rebuilds the page's lists and texts from <see cref="PanelSession.Config"/> and <see cref="PanelSession.Status"/>.</summary>
    protected abstract void Rebuild();

    protected static void Replace<T>(System.Collections.ObjectModel.ObservableCollection<T> target, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
