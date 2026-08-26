using System.Windows;

namespace ShareBridge.Wpf.Views;

/// <summary>
/// Shows message notifications as a stack of toast cards in the bottom-right corner of
/// the work area. Newest sits at the bottom; older ones ride up above it.
/// </summary>
public sealed class NotificationService
{
    private const int MaxVisible = 4;
    private const double EdgeMargin = 12;
    private const double Gap = 8;

    private readonly List<ToastWindow> _toasts = [];

    public void Show(string title, string body, string initials, Action onActivate)
    {
        // Keep the stack short: retire the oldest still-visible toast before adding a new
        // one. Dismissing toasts stay tracked (so CloseAll can still reach them) but no
        // longer count against the cap, which also stops this loop from spinning.
        while (_toasts.Count(t => !t.IsDismissing) >= MaxVisible)
        {
            var oldest = _toasts.FirstOrDefault(t => !t.IsDismissing);
            if (oldest is null)
                break;

            oldest.BeginDismiss();
        }

        var toast = new ToastWindow(title, TrimBody(body), initials, onActivate);
        toast.Closed += (_, _) =>
        {
            _toasts.Remove(toast);
            Reflow();
        };

        _toasts.Add(toast);
        toast.Show();
        Reflow();
    }

    /// <summary>Dismisses everything, e.g. on shutdown.</summary>
    public void CloseAll()
    {
        foreach (var toast in _toasts.ToList())
            toast.Close();

        _toasts.Clear();
    }

    private void Reflow()
    {
        var area = SystemParameters.WorkArea;
        var y = area.Bottom - EdgeMargin;

        // Walk newest-first so the most recent card anchors to the bottom. Toasts that are
        // fading out are left where they are rather than reshuffling the live stack.
        for (var i = _toasts.Count - 1; i >= 0; i--)
        {
            var toast = _toasts[i];
            if (toast.IsDismissing)
                continue;

            toast.UpdateLayout();

            var width = toast.ActualWidth > 0 ? toast.ActualWidth : toast.Width;
            var height = toast.ActualHeight > 0 ? toast.ActualHeight : toast.Height;

            toast.Left = area.Right - width - EdgeMargin;
            toast.Top = y - height;
            y -= height + Gap;
        }
    }

    private static string TrimBody(string body)
    {
        var text = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 140 ? text : text[..140].TrimEnd() + "…";
    }
}
