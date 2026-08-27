using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using ShareBridge.Core;
using ShareBridge.Wpf.ViewModels;
using ShareBridge.Wpf.Views;

namespace ShareBridge.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly NotificationService _notifications = new();
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private MessageViewModel? _highlighted;
    private TrayIcon? _tray;
    private bool _closing;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(AppSettings.Load(), Dispatcher)
        {
            RequestPickFiles = PickFiles,
            RequestPickFolder = PickFolder,
            RequestExport = ExportConversation,
            RequestSettings = ShowSettings,
            RequestOfferDecision = AskAboutOfferAsync,
            RequestAttention = FlashIfInactive,
            RequestNotification = ShowMessageNotification,
        };

        DataContext = _viewModel;
        Title = $"ShareBridge {AppInfo.Version}";

        _viewModel.ScrollToMessageRequested += OnScrollToMessage;
        _highlightTimer.Tick += ClearHighlight;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _tray = new TrayIcon();
        _tray.OpenRequested += RestoreFromTray;
        _tray.SettingsRequested += ShowSettings;
        _tray.ExitRequested += ExitApp;
        _viewModel.UnreadTotalChanged += count => _tray?.SetUnread(count);
        StateChanged += OnStateChanged;

        // Keep the view model's notion of "in the foreground" current, so messages that
        // arrive while the window is in the background or tray count as unread.
        Activated += (_, _) => _viewModel.WindowActive = true;
        Deactivated += (_, _) => _viewModel.WindowActive = false;

        try
        {
            await _viewModel.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"ShareBridge could not start its network listeners.\n\n{ex.Message}\n\n" +
                "Check that the discovery port is free and that Windows Firewall allows this app on your private network.",
                "ShareBridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing)
            return;

        // Closing the window keeps the app alive in the tray, unless the user chose Exit
        // (or turned the option off).
        if (!_exiting && _viewModel.Settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Let the node broadcast BYE and close its sockets before the process exits.
        _closing = true;
        e.Cancel = true;

        _notifications.CloseAll();
        _tray?.Dispose();
        await _viewModel.DisposeAsync();
        Close();
    }

    // -----------------------------------------------------------------------
    // System tray
    // -----------------------------------------------------------------------

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.Settings.MinimizeToTray)
            HideToTray();
    }

    private void HideToTray()
    {
        _viewModel.WindowActive = false;
        Hide();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloon("ShareBridge is still running",
                "It stays here in the tray. Right-click the icon to exit.");
        }
    }

    /// <summary>Brings the window up front, e.g. when a second launch is redirected here.</summary>
    public void BringToForeground() => RestoreFromTray();

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        // Nudge to the foreground without leaving the window permanently on top.
        Topmost = true;
        Topmost = false;

        _viewModel.WindowActive = true;
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    // -----------------------------------------------------------------------
    // Pickers
    // -----------------------------------------------------------------------

    /// <summary>
    /// The shell has no dialog that takes a mixed selection of files and folders — and it
    /// will not close the file dialog on an empty name box, so a folder can only come back
    /// from a placeholder parked in that box. Rather than show one, the attach button asks
    /// which kind first and opens the matching native picker, both with an empty name box.
    /// </summary>
    private void OnAttachClick(object sender, RoutedEventArgs e)
    {
        if (AttachButton.ContextMenu is not { } menu)
            return;

        menu.PlacementTarget = AttachButton;
        menu.IsOpen = true;
    }

    private async void PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose files to send",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        await _viewModel.SendPathsAsync(dialog.FileNames);
    }

    private async void PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose folders to send",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        await _viewModel.SendPathsAsync(dialog.FolderNames);
    }

    private void ExportConversation()
    {
        if (_viewModel.SelectedPeer is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export conversation",
            Filter = "Text file (*.txt)|*.txt|Markdown (*.md)|*.md",
            FileName = _viewModel.SuggestedExportName + ".txt",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) == true)
            _viewModel.ExportConversationTo(dialog.FileName);
    }

    private void ShowSettings()
    {
        var window = new SettingsWindow(
            _viewModel.Settings,
            openHistory: _viewModel.OpenHistoryFolder,
            clearHistory: _viewModel.ClearAllHistory)
        {
            Owner = this,
        };

        if (window.ShowDialog() == true)
            _ = _viewModel.ApplySettingsAsync();
    }

    private Task<bool> AskAboutOfferAsync(TransferViewModel transfer, CancellationToken senderGone)
    {
        FlashIfInactive();

        // The dialog closes itself if the sender disconnects before the user answers.
        var window = new IncomingOfferWindow(transfer, senderGone) { Owner = this };
        return Task.FromResult(window.ShowDialog() == true);
    }

    // -----------------------------------------------------------------------
    // Search: focus shortcut, scroll-to and flash
    // -----------------------------------------------------------------------

    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Enter cycles to the next match in the open conversation, Shift+Enter to the previous.</summary>
    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        var command = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
            ? _viewModel.PreviousMatchCommand
            : _viewModel.NextMatchCommand;

        if (command.CanExecute(null))
            command.Execute(null);

        e.Handled = true;
    }

    /// <summary>
    /// Brings a message into view and flashes it. Switching to the peer regenerates the
    /// timeline, so the container may not exist yet; retry once layout has run.
    /// </summary>
    private void OnScrollToMessage(MessageViewModel message)
    {
        ClearHighlight(this, EventArgs.Empty);

        _highlighted = message;
        message.IsHighlighted = true;
        _highlightTimer.Stop();
        _highlightTimer.Start();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Timeline.UpdateLayout();
            if (Timeline.ItemContainerGenerator.ContainerFromItem(message) is FrameworkElement container)
                container.BringIntoView();
        });
    }

    private void ClearHighlight(object? sender, EventArgs e)
    {
        _highlightTimer.Stop();
        if (_highlighted is not null)
        {
            _highlighted.IsHighlighted = false;
            _highlighted = null;
        }
    }

    // -----------------------------------------------------------------------
    // Composer
    // -----------------------------------------------------------------------

    private void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        // Arrow-Up from an empty composer edits your last message (Slack/Discord style).
        if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None && string.IsNullOrEmpty(_viewModel.Draft))
        {
            if (_viewModel.BeginEditLastMessage())
                e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return;

        e.Handled = true;

        if (_viewModel.SendMessageCommand.CanExecute(null))
            _viewModel.SendMessageCommand.Execute(null);
    }

    // -----------------------------------------------------------------------
    // Drag and drop
    // -----------------------------------------------------------------------

    private void OnConversationDragOver(object sender, DragEventArgs e)
    {
        var paths = GetDroppedPaths(e);

        if (paths.Count == 0 || _viewModel.SelectedPeer is null)
        {
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            e.Effects = DragDropEffects.Copy;
            DropText.Text = $"Send to {_viewModel.SelectedPeer.Name}";
            DropSubText.Text = paths.Count == 1
                ? Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar))
                : $"{paths.Count} items";
            DropOverlay.Visibility = Visibility.Visible;
        }

        e.Handled = true;
    }

    private void OnConversationDragLeave(object sender, DragEventArgs e) =>
        DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnConversationDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        var paths = GetDroppedPaths(e);
        if (paths.Count > 0)
            await _viewModel.SendPathsAsync(paths);
    }

    private static List<string> GetDroppedPaths(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return [];

        return e.Data.GetData(DataFormats.FileDrop) is not string[] paths
            ? []
            : [.. paths.Where(p => File.Exists(p) || Directory.Exists(p))];
    }

    // -----------------------------------------------------------------------
    // Taskbar attention
    // -----------------------------------------------------------------------

    private const uint FlashAll = 3;
    private const uint FlashTimerNoForeground = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    /// <summary>
    /// Pops a toast for an incoming message, unless the user is already looking at that
    /// exact conversation (window active and the sender selected).
    /// </summary>
    private void ShowMessageNotification(ViewModels.PeerViewModel peer, string text)
    {
        if (IsActive && ReferenceEquals(_viewModel.SelectedPeer, peer))
            return;

        _notifications.Show(peer.Name, text, peer.Initials, () =>
        {
            RestoreFromTray();
            _viewModel.SelectedPeer = peer;
        });
    }

    /// <summary>Blinks the taskbar button when something arrives and we are not in focus.</summary>
    private void FlashIfInactive()
    {
        if (IsActive)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        var info = new FlashWindowInfo
        {
            cbSize = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            hwnd = handle,
            dwFlags = FlashAll | FlashTimerNoForeground,
            uCount = 3,
            dwTimeout = 0,
        };

        FlashWindowEx(ref info);
    }
}
