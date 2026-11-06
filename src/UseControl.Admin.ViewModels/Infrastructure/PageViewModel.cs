using CommunityToolkit.Mvvm.ComponentModel;
using UseControl.Localization;

namespace UseControl.Admin.ViewModels.Infrastructure;

/// <summary>A group of pages in the navigation: the PC as a whole, or the profile chosen at the top.</summary>
public sealed class NavSection : ObservableObject
{
    private NavSection(string? titleKey, int order)
    {
        TitleKey = titleKey;
        Order = order;
        TranslationSource.Instance.CultureChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    /// <summary>Overview and accounts: no heading.</summary>
    public static NavSection General { get; } = new(null, 0);

    /// <summary>Pages that show the profile chosen at the top.</summary>
    public static NavSection Profile { get; } = new("Ui.Nav.ProfileSection", 1);

    /// <summary>Settings of the whole PC.</summary>
    public static NavSection Computer { get; } = new("Ui.Nav.ComputerSection", 2);

    public string? TitleKey { get; }

    public int Order { get; }

    public string Title => TitleKey is null ? "" : Loc.T(TitleKey);
}

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
            OnPropertyChanged(nameof(Subtitle));
            Rebuild();
        };
    }

    public PanelSession Session { get; }

    /// <summary>Resource key of the page name.</summary>
    public string TitleKey { get; }

    public string Title => Loc.T(TitleKey);

    /// <summary>One short line under the page title (resource key + ".Subtitle"), or empty.</summary>
    public string Subtitle => Loc.Has(TitleKey + ".Subtitle") ? Loc.T(TitleKey + ".Subtitle") : "";

    /// <summary>Glyph in the Segoe MDL2 Assets font.</summary>
    public string Icon { get; }

    /// <summary>The group of the navigation this page belongs to.</summary>
    public virtual NavSection Section => UsesPreset ? NavSection.Profile : NavSection.General;

    /// <summary>True for pages that show the settings of one profile (the profile bar is shown above them).</summary>
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
