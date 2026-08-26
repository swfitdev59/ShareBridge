using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ShareBridge.Wpf.Infrastructure;

/// <summary>
/// Focuses (and selects all text in) a control the moment it becomes visible — used to
/// drop the caret straight into the inline message editor when editing begins.
/// </summary>
public static class FocusOnVisible
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(FocusOnVisible), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
            element.IsVisibleChanged += OnIsVisibleChanged;
        else
            element.IsVisibleChanged -= OnIsVisibleChanged;
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || !element.IsVisible)
            return;

        // Defer until layout settles so Focus actually lands.
        element.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            element.Focus();
            if (element is TextBox textBox)
                textBox.SelectAll();
        });
    }
}
