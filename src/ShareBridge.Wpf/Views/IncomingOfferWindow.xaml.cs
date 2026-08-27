using System.Windows;
using System.Windows.Threading;
using ShareBridge.Wpf.Infrastructure;
using ShareBridge.Wpf.ViewModels;

namespace ShareBridge.Wpf.Views;

public partial class IncomingOfferWindow : Window
{
    /// <summary>The sender blocks while this dialog is open, so it cannot wait forever.</summary>
    private const int AutoDeclineSeconds = 90;

    private readonly DispatcherTimer _timer;
    private CancellationTokenRegistration _senderGoneRegistration;
    private int _secondsLeft = AutoDeclineSeconds;

    public IncomingOfferWindow(TransferViewModel transfer, CancellationToken senderGone = default)
    {
        InitializeComponent();

        var model = transfer.Model;

        HeadlineText.Text = $"{model.PeerName} wants to send you files";
        SummaryText.Text = $"{model.Items.Count} file{(model.Items.Count == 1 ? "" : "s")} · {Format.Bytes(model.TotalBytes)}";
        DestinationText.Text = $"They will be saved to {model.DestinationFolder}";

        FileList.ItemsSource = model.Items
            .Select(i => new { Name = i.RelativePath, Size = Format.Bytes(i.Size) })
            .ToList();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        // If the sender cancels/disconnects while we wait, dismiss the prompt automatically.
        _senderGoneRegistration = senderGone.Register(() => Dispatcher.BeginInvoke(CloseFromSenderGone));

        Loaded += (_, _) =>
        {
            UpdateCountdown();
            _timer.Start();
            Activate();
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _senderGoneRegistration.Dispose();
        };
    }

    private void CloseFromSenderGone()
    {
        if (!IsLoaded && !IsVisible)
            return;

        _timer.Stop();
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        DialogResult = true;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _secondsLeft--;

        if (_secondsLeft <= 0)
        {
            _timer.Stop();
            DialogResult = false;
            return;
        }

        UpdateCountdown();
    }

    private void UpdateCountdown() => CountdownText.Text = $"Declining automatically in {_secondsLeft}s";
}
