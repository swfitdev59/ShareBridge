using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ShareBridge.Wpf;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _reportedError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            // Another instance already owns this machine session; ask it to surface, then quit.
            SignalRunningInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    /// <summary>
    /// Names the shared handles. Normally every launch shares one name, so only one instance
    /// runs. When SHAREBRIDGE_ID is set (multi-instance testing) the name is scoped to it, so
    /// those deliberately-separate instances each get their own slot.
    /// </summary>
    private static string InstanceKey()
    {
        var id = Environment.GetEnvironmentVariable("SHAREBRIDGE_ID");
        if (string.IsNullOrWhiteSpace(id))
            return "ShareBridge.SingleInstance";

        var safe = new string(id.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return $"ShareBridge.SingleInstance.{safe}";
    }

    private bool ClaimSingleInstance()
    {
        // Local\ scope = per Windows session, which is what "already running on this machine"
        // means for a desktop app.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, $"Local\\{InstanceKey()}", out var createdNew);
        if (!createdNew)
            return false;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{InstanceKey()}.Show");
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(BringExistingToFront),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: false);

        return true;
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting($"Local\\{InstanceKey()}.Show");
            handle.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            // The other instance is starting up or shutting down; nothing more we can do.
        }
    }

    private void BringExistingToFront() => (MainWindow as MainWindow)?.BringToForeground();

    protected override void OnExit(ExitEventArgs e)
    {
        _registeredWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// A background socket fault should never take the window down; surface it and carry on.
    /// Only the first one gets a dialog — a fault inside a template repeats per item,
    /// and a stack of identical message boxes helps nobody.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        if (_reportedError)
            return;

        _reportedError = true;

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n" +
            "ShareBridge will keep running. Further errors are suppressed for this session.",
            "ShareBridge",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
