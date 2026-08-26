using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using ShareBridge.Core.Models;
using ShareBridge.Wpf.Infrastructure;

namespace ShareBridge.Wpf.ViewModels;

public sealed class TransferViewModel : ObservableObject
{
    public TransferViewModel(Transfer model)
    {
        Model = model;
        CancelCommand = new RelayCommand(_ => Model.Cancel(), _ => Model.IsActive);
        OpenFolderCommand = new RelayCommand(_ => OpenDestination(), _ => CanOpenFolder);
    }

    public Transfer Model { get; }

    public ICommand CancelCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public bool IsOutgoing => Model.Direction == TransferDirection.Outgoing;

    public string Title => Model.Title;

    public string PeerName => Model.PeerName;
    public string DirectionGlyph => IsOutgoing ? "↑" : "↓";

    public string HeaderText => IsOutgoing ? $"Sending to {Model.PeerName}" : $"Receiving from {Model.PeerName}";

    public double Percent => Model.Percent;

    public bool IsActive => Model.IsActive;

    public bool IsIndeterminate => Model.Status == TransferStatus.Negotiating;

    public bool CanOpenFolder =>
        !IsOutgoing && Model.Status == TransferStatus.Completed && Directory.Exists(Model.DestinationFolder);

    public string SizeText => Model.IsActive && Model.Status == TransferStatus.Running
        ? $"{Format.Bytes(Model.TransferredBytes)} of {Format.Bytes(Model.TotalBytes)}"
        : Format.Bytes(Model.TotalBytes);

    public string DetailText
    {
        get
        {
            var parts = new List<string> { $"{Model.Items.Count} file{(Model.Items.Count == 1 ? "" : "s")}", SizeText };

            if (Model.Status == TransferStatus.Running)
            {
                var rate = Format.Rate(Model.BytesPerSecond);
                if (rate.Length > 0)
                    parts.Add(rate);

                var eta = Format.Eta(Model.TotalBytes - Model.TransferredBytes, Model.BytesPerSecond);
                if (eta.Length > 0)
                    parts.Add(eta);
            }

            return string.Join(" · ", parts);
        }
    }

    public string StatusText => Model.Status switch
    {
        TransferStatus.Pending => "Waiting…",
        TransferStatus.Negotiating => IsOutgoing ? "Waiting for the other side…" : "Awaiting your decision…",
        TransferStatus.Running => $"{Model.Percent:0.#}%",
        TransferStatus.Completed => "Completed",
        TransferStatus.Rejected => "Declined",
        TransferStatus.Cancelled => "Cancelled",
        TransferStatus.Failed => "Failed",
        _ => string.Empty,
    };

    public string? ErrorText => Model.Status is TransferStatus.Failed or TransferStatus.Rejected ? Model.Error : null;

    public bool IsFaulted => Model.Status is TransferStatus.Failed or TransferStatus.Cancelled or TransferStatus.Rejected;

    public bool IsCompleted => Model.Status == TransferStatus.Completed;

    public string TimestampText => Format.Timestamp(Model.StartedAt);

    /// <summary>Pushes the latest model state into the UI. Called on the dispatcher thread.</summary>
    public void Refresh() => RaiseAll(
        nameof(Percent), nameof(IsActive), nameof(IsIndeterminate), nameof(SizeText), nameof(DetailText),
        nameof(StatusText), nameof(ErrorText), nameof(IsFaulted), nameof(IsCompleted), nameof(CanOpenFolder));

    private void OpenDestination()
    {
        var folder = Model.DestinationFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
