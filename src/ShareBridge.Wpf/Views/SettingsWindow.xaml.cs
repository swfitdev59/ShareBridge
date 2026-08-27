using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ShareBridge.Core;

namespace ShareBridge.Wpf.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action? _openHistory;
    private readonly Action? _clearHistory;

    public SettingsWindow(AppSettings settings, Action? openHistory = null, Action? clearHistory = null)
    {
        InitializeComponent();

        _settings = settings;
        _openHistory = openHistory;
        _clearHistory = clearHistory;

        ShowValues(settings);

        Loaded += (_, _) => DisplayNameBox.Focus();
    }

    /// <summary>Fills the form from a settings object without committing anything.</summary>
    private void ShowValues(AppSettings values)
    {
        DisplayNameBox.Text = values.DisplayName;
        DownloadFolderBox.Text = values.DownloadFolder;
        AutoAcceptBox.IsChecked = values.AutoAcceptFiles;
        NotificationsBox.IsChecked = values.ShowNotifications;
        MinimizeToTrayBox.IsChecked = values.MinimizeToTray;
        CloseToTrayBox.IsChecked = values.CloseToTray;
        SaveHistoryBox.IsChecked = values.SaveHistory;
        DiscoveryPortBox.Text = values.DiscoveryPort.ToString(CultureInfo.InvariantCulture);
        TransferPortBox.Text = values.TransferPort.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Repopulates the form from a stock <see cref="AppSettings"/>, so the defaults never
    /// drift from the ones the app itself would use on a fresh install. Nothing is written
    /// until Save; Cancel still backs the whole thing out. NodeId and saved conversations
    /// are deliberately untouched — resetting the id would orphan existing history.
    /// </summary>
    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        ShowValues(new AppSettings());
        ErrorText.Visibility = Visibility.Collapsed;
        DisplayNameBox.Focus();
    }

    private void OnOpenHistory(object sender, RoutedEventArgs e) => _openHistory?.Invoke();

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Delete every saved conversation on this PC? This cannot be undone.",
            "Clear all history",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
            _clearHistory?.Invoke();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where incoming files are saved",
            InitialDirectory = Directory.Exists(DownloadFolderBox.Text) ? DownloadFolderBox.Text : string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
            DownloadFolderBox.Text = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = DisplayNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Pick a display name so people can recognise you.");
            return;
        }

        if (!TryReadPort(DiscoveryPortBox.Text, out var discoveryPort))
        {
            ShowError("The discovery port must be a number between 1 and 65535.");
            return;
        }

        if (!TryReadPort(TransferPortBox.Text, out var transferPort))
        {
            ShowError("The transfer port must be a number between 1 and 65535.");
            return;
        }

        if (discoveryPort == transferPort)
        {
            ShowError("The discovery and transfer ports must be different.");
            return;
        }

        var folder = DownloadFolderBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            ShowError($"That download folder cannot be used: {ex.Message}");
            return;
        }

        _settings.DisplayName = name;
        _settings.DownloadFolder = folder;
        _settings.AutoAcceptFiles = AutoAcceptBox.IsChecked == true;
        _settings.ShowNotifications = NotificationsBox.IsChecked == true;
        _settings.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.SaveHistory = SaveHistoryBox.IsChecked == true;
        _settings.DiscoveryPort = discoveryPort;
        _settings.TransferPort = transferPort;

        DialogResult = true;
    }

    private static bool TryReadPort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
        && port is >= 1 and <= 65535;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
