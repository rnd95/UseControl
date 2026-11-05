using System.Windows;
using Microsoft.Win32;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Localization;

namespace UseControl.Admin.Infrastructure;

/// <summary>Windows dialogs for the view models.</summary>
public sealed class DialogService : IDialogService
{
    public bool Confirm(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, Loc.T("Ui.App.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? PickFolder(string title)
    {
        var d = new OpenFolderDialog { Title = title, Multiselect = false };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FolderName : null;
    }

    public string? PickFile(string title, string filter)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultName)
    {
        var d = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }
}
