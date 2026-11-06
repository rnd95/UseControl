namespace UseControl.Admin.ViewModels.Infrastructure;

/// <summary>Dialogs the view models need; the WPF project implements them, tests replace them.</summary>
public interface IDialogService
{
    /// <summary>Yes/No question; true for Yes.</summary>
    bool Confirm(string message);

    /// <summary>Folders chosen by the user (several at once); empty when cancelled.</summary>
    IReadOnlyList<string> PickFolders(string title);

    /// <summary>An existing file chosen by the user, or null. Filter like "Programs|*.exe".</summary>
    string? PickFile(string title, string filter);

    /// <summary>Existing files chosen by the user (several at once); empty when cancelled.</summary>
    IReadOnlyList<string> PickFiles(string title, string filter);

    /// <summary>A file name to save to, or null.</summary>
    string? PickSaveFile(string title, string filter, string defaultName);

    /// <summary>A line of text typed by the user, or null when cancelled.</summary>
    string? Prompt(string message, string initial);
}
