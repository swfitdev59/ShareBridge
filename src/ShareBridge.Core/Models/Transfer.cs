namespace ShareBridge.Core.Models;

public enum TransferDirection
{
    Outgoing,
    Incoming,
}

public enum TransferStatus
{
    Pending,
    Negotiating,
    Running,
    Completed,
    Rejected,
    Cancelled,
    Failed,
}

/// <summary>One file inside a transfer. <see cref="RelativePath"/> uses '/' separators.</summary>
public sealed class TransferItem
{
    public required string RelativePath { get; set; }
    public required long Size { get; set; }

    /// <summary>Absolute source path. Sender side only; never put on the wire.</summary>
    public string? SourcePath { get; set; }
}

/// <summary>
/// Live state of one transfer. Mutated by the transport layer; the UI subscribes to
/// <see cref="ShareBridgeNode.TransferChanged"/> rather than polling.
/// </summary>
public sealed class Transfer
{
    public required string Id { get; init; }
    public required TransferDirection Direction { get; init; }
    public required string PeerId { get; init; }
    public required string PeerName { get; init; }
    public required IReadOnlyList<TransferItem> Items { get; init; }
    public required long TotalBytes { get; init; }

    public long TransferredBytes { get; internal set; }
    public TransferStatus Status { get; internal set; } = TransferStatus.Pending;
    public string? Error { get; internal set; }
    public string? DestinationFolder { get; internal set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; internal set; }

    /// <summary>Bytes per second over the life of the transfer; 0 until measurable.</summary>
    public double BytesPerSecond { get; internal set; }

    /// <summary>
    /// Rebuilds a finished transfer from saved history for display only. Items are
    /// placeholders sized to reproduce the title and totals; bytes are not re-read.
    /// </summary>
    public static Transfer CreateHistorical(
        string id,
        TransferDirection direction,
        string peerId,
        string peerName,
        string title,
        int itemCount,
        long totalBytes,
        TransferStatus status,
        string? destinationFolder,
        DateTimeOffset startedAt)
    {
        // A single-item transfer takes its title from the first item's name, so seed
        // that name to reproduce the original label; multi-item titles are just a count.
        var count = Math.Max(1, itemCount);
        var items = new List<TransferItem>(count);
        for (var i = 0; i < count; i++)
        {
            var rel = count == 1 && !string.IsNullOrWhiteSpace(title) ? title : $"item{i}";
            items.Add(new TransferItem { RelativePath = rel, Size = 0 });
        }

        return new Transfer
        {
            Id = id,
            Direction = direction,
            PeerId = peerId,
            PeerName = peerName,
            Items = items,
            TotalBytes = totalBytes,
            TransferredBytes = status == TransferStatus.Completed ? totalBytes : 0,
            Status = status,
            DestinationFolder = destinationFolder,
            StartedAt = startedAt,
            FinishedAt = startedAt,
        };
    }

    internal CancellationTokenSource Cancellation { get; private set; } = new();

    public bool IsActive => Status is TransferStatus.Pending or TransferStatus.Negotiating or TransferStatus.Running;

    /// <summary>Rewinds a finished transfer so it can run again on the same object (retry).</summary>
    internal void ResetForRetry()
    {
        DisposeCancellation();
        Cancellation = new CancellationTokenSource();

        TransferredBytes = 0;
        BytesPerSecond = 0;
        Error = null;
        FinishedAt = null;
        Status = TransferStatus.Negotiating;
    }

    public double Percent => TotalBytes <= 0 ? 0 : Math.Round(TransferredBytes * 100.0 / TotalBytes, 1);

    public string Title => Items.Count == 1
        ? Path.GetFileName(Items[0].RelativePath)
        : $"{Items.Count} files";

    public void Cancel()
    {
        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void DisposeCancellation()
    {
        try
        {
            Cancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
