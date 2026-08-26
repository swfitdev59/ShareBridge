using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ShareBridge.Wpf.Infrastructure;

/// <summary>
/// Forwards mouse-wheel scrolling from a control up to its parent. A selectable
/// (read-only) TextBox otherwise swallows the wheel and stops the surrounding
/// conversation from scrolling.
/// </summary>
public static class WheelToParent
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WheelToParent), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        if ((bool)e.NewValue)
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement element)
            return;

        e.Handled = true;

        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        };

        // Re-raise on the visual parent so it bubbles to the enclosing ScrollViewer.
        (VisualTreeHelper.GetParent(element) as UIElement)?.RaiseEvent(forwarded);
    }
}
