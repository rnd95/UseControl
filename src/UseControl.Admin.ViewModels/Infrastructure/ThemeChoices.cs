namespace UseControl.Admin.ViewModels.Infrastructure;

public enum PanelTheme
{
    /// <summary>Light or dark like the Windows apps setting.</summary>
    System,
    Light,
    Dark,
}

/// <summary>The theme the panel starts with and how to switch it (the WPF project does the switching).</summary>
public sealed record ThemeChoices(PanelTheme Current, Action<PanelTheme> Apply)
{
    /// <summary>For tests: no switching.</summary>
    public static ThemeChoices None { get; } = new(PanelTheme.Light, _ => { });
}
