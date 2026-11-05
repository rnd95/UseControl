using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UseControl.Admin.Infrastructure;

/// <summary>
/// Lists inside a scrolling page swallow the mouse wheel even when they have nothing left to scroll.
/// With WheelScroll.Bubble="True" the wheel goes to the page once the list reaches its top or bottom
/// (or cannot scroll at all). Set for every DataGrid, ListBox and multi-line TextBox in Theme.xaml.
/// </summary>
public static class WheelScroll
{
    public static readonly DependencyProperty BubbleProperty = DependencyProperty.RegisterAttached(
        "Bubble", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false, OnBubbleChanged));

    public static bool GetBubble(DependencyObject d) => (bool)d.GetValue(BubbleProperty);

    public static void SetBubble(DependencyObject d, bool value) => d.SetValue(BubbleProperty, value);

    private static void OnBubbleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject d) return;
        var inner = FindScrollViewer(d);
        var canScroll = inner is not null && inner.ScrollableHeight > 0 &&
            (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight);
        if (canScroll) return;

        e.Handled = true;
        if (VisualTreeHelper.GetParent(d) is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        }
        return null;
    }
}
