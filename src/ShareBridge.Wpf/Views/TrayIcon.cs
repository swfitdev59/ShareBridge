using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace ShareBridge.Wpf.Views;

/// <summary>
/// The system-tray presence, wrapping a WinForms <see cref="WinForms.NotifyIcon"/>.
/// All WinForms usage is confined here so the rest of the app stays pure WPF.
/// Events are raised on the UI thread (the notify icon lives on it).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon _baseIcon;
    private IntPtr _badgeHandle;
    private Icon? _badgeIcon;
    private bool _disposed;

    public TrayIcon()
    {
        _baseIcon = LoadBaseIcon();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open ShareBridge", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Settings…", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new WinForms.NotifyIcon
        {
            Text = "ShareBridge",
            Icon = _baseIcon,
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                OpenRequested?.Invoke();
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>Left-click or double-click, or the "Open" menu item.</summary>
    public event Action? OpenRequested;

    public event Action? SettingsRequested;

    public event Action? ExitRequested;

    /// <summary>Overlays an unread count on the tray icon (or clears it when zero).</summary>
    public void SetUnread(int count)
    {
        if (_disposed)
            return;

        if (count <= 0)
        {
            _icon.Icon = _baseIcon;
            _icon.Text = "ShareBridge";
            ReleaseBadge();
            return;
        }

        _icon.Text = $"ShareBridge — {count} unread message{(count == 1 ? "" : "s")}";

        using var composed = TrayBadge.Compose(_baseIcon);
        var handle = composed.GetHicon();
        var newIcon = Icon.FromHandle(handle);

        _icon.Icon = newIcon;

        // Swap in the new handle, then free the previous one (FromHandle does not own it).
        ReleaseBadge();
        _badgeHandle = handle;
        _badgeIcon = newIcon;
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        ReleaseBadge();
        _baseIcon.Dispose();
    }

    private void ReleaseBadge()
    {
        _badgeIcon?.Dispose();
        _badgeIcon = null;

        if (_badgeHandle != IntPtr.Zero)
        {
            DestroyIcon(_badgeHandle);
            _badgeHandle = IntPtr.Zero;
        }
    }

    /// <summary>Loads the app icon at 32&#215;32; falls back to the shell icon.</summary>
    private static Icon LoadBaseIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/appicon.ico"));
            if (info is not null)
            {
                using var stream = info.Stream;
                return new Icon(stream, new Size(32, 32));
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
        }

        return SystemIcons.Application;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>Composites a plain red unread dot onto the app icon for the tray.</summary>
public static class TrayBadge
{
    public static Bitmap Compose(Icon baseIcon)
    {
        var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var baseBmp = baseIcon.ToBitmap())
            g.DrawImage(baseBmp, new Rectangle(0, 0, 32, 32));

        const float diameter = 13f;
        const float margin = 1.5f;
        const float x = 32 - diameter - margin;
        const float y = margin;

        using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            g.FillEllipse(shadow, x - 1, y + 1, diameter, diameter);

        // White ring so the dot stays visible against dark or busy backgrounds.
        using (var ring = new SolidBrush(Color.White))
            g.FillEllipse(ring, x - 1.5f, y - 1.5f, diameter + 3, diameter + 3);

        using (var red = new SolidBrush(Color.FromArgb(255, 0xF0, 0x3A, 0x3A)))
            g.FillEllipse(red, x, y, diameter, diameter);

        return bmp;
    }
}
