using System.Collections;
using System.Windows;
using System.Windows.Data;
using UseControl.Admin.ViewModels;
using UseControl.Admin.ViewModels.Infrastructure;

namespace UseControl.Admin;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel vm) GroupBySection(vm.Pages);
        };
    }

    /// <summary>The navigation shows the pages in groups: general, the chosen profile, this PC.</summary>
    private static void GroupBySection(IEnumerable pages)
    {
        var view = CollectionViewSource.GetDefaultView(pages);
        if (view.GroupDescriptions.Count == 0) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PageViewModel.Section)));
    }
}
