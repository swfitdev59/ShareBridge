using System.Windows;
using System.Windows.Controls;

namespace ShareBridge.Wpf.Infrastructure;

/// <summary>
/// Keeps a ScrollViewer pinned to the bottom as content is appended, but stops
/// fighting the user the moment they scroll up to read back.
/// </summary>
public static class AutoScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty PinnedProperty = DependencyProperty.RegisterAttached(
        "Pinned", typeof(bool), typeof(AutoScroll), new PropertyMetadata(true));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
            return;

        if ((bool)e.NewValue)
            viewer.ScrollChanged += OnScrollChanged;
        else
            viewer.ScrollChanged -= OnScrollChanged;
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var viewer = (ScrollViewer)sender;

        // A vertical change with no extent change means the user moved the thumb.
        if (Math.Abs(e.ExtentHeightChange) < 0.5 && Math.Abs(e.ViewportHeightChange) < 0.5)
        {
            var atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight - 2;
            viewer.SetValue(PinnedProperty, atBottom);
            return;
        }

        if ((bool)viewer.GetValue(PinnedProperty))
            viewer.ScrollToEnd();
    }
}
