using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Localization;

namespace UseControl.Admin.Infrastructure;

/// <summary>Windows dialogs for the view models.</summary>
public sealed class DialogService : IDialogService
{
    public bool Confirm(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, Loc.T("Ui.App.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public IReadOnlyList<string> PickFolders(string title)
    {
        var d = new OpenFolderDialog { Title = title, Multiselect = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FolderNames : [];
    }

    public string? PickFile(string title, string filter)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }

    public IReadOnlyList<string> PickFiles(string title, string filter)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileNames : [];
    }

    public string? PickSaveFile(string title, string filter, string defaultName)
    {
        var d = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }

    /// <summary>A small window with a message, a text box, OK and Cancel, in the colours of the panel.</summary>
    public string? Prompt(string message, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 16), MinWidth = 320 };
        var ok = new Button { Content = Loc.T("Ui.Ok"), IsDefault = true, MinWidth = 90 };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = Loc.T("Ui.Cancel"), IsCancel = true, MinWidth = 90, Margin = new Thickness(0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var w = new Window
        {
            Title = Loc.T("Ui.App.Title"),
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
            Content = new StackPanel { Margin = new Thickness(20), Children = { text, box, buttons } },
        };
        w.SetResourceReference(Control.BackgroundProperty, "CardBackground");
        w.SourceInitialized += (_, _) => ThemeManager.SetTitleBar(w);
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return w.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
