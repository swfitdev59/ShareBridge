using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ShareBridge.Wpf.Views;

/// <summary>
/// A small self-owned notification card. It never takes focus (so it will not
/// interrupt whatever the user is doing) and dismisses itself after a few seconds.
/// </summary>
public partial class ToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    private readonly DispatcherTimer _dismissTimer;
    private readonly Action _onActivate;
    private bool _closing;

    /// <summary>True once the toast has begun fading out, so the stack can ignore it.</summary>
    public bool IsDismissing => _closing;

    public ToastWindow(string title, string body, string initials, Action onActivate)
    {
        InitializeComponent();

        TitleText.Text = title;
        BodyText.Text = body;
        AvatarText.Text = initials;
        _onActivate = onActivate;

        _dismissTimer = new DispatcherTimer { Interval = Lifetime };
        _dismissTimer.Tick += (_, _) => BeginDismiss();

        Loaded += OnLoaded;

        // A hover pauses the countdown so a message can be read without racing the clock.
        MouseEnter += (_, _) => _dismissTimer.Stop();
        MouseLeave += (_, _) => { if (!_closing) _dismissTimer.Start(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        // NoActivate: never steal focus. ToolWindow: keep it out of Alt-Tab.
        SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _dismissTimer.Start();
    }

    private void OnBodyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _onActivate();
        }
        finally
        {
            BeginDismiss();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => BeginDismiss();

    /// <summary>Fades out and closes. Safe to call more than once.</summary>
    public void BeginDismiss()
    {
        if (_closing)
            return;

        _closing = true;
        _dismissTimer.Stop();

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
