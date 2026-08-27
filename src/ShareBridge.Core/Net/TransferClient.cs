using System.Diagnostics;
using System.Net.Sockets;
using ShareBridge.Core.Models;
using ShareBridge.Core.Protocol;

namespace ShareBridge.Core.Net;

/// <summary>Raised when the far side refuses an offer, so the UI can say why.</summary>
public sealed class OfferRejectedException(string reason) : Exception(reason);

/// <summary>
/// Outbound half of the protocol. Opens a fresh TCP connection per exchange, which
/// keeps failure handling simple: a dead socket only ever kills one message or transfer.
/// </summary>
public sealed class TransferClient
{
    private const int CopyBufferSize = 128 * 1024;
    private const int ProgressIntervalMs = 100;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    private readonly PeerIdentity _self;

    public TransferClient(PeerIdentity self) => _self = self;

    public event EventHandler<TransferEventArgs>? TransferStarted;
    public event EventHandler<TransferEventArgs>? TransferChanged;
    public event EventHandler<TransferEventArgs>? TransferFinished;

    public async Task SendMessageAsync(Peer peer, ChatMessage message, CancellationToken ct)
    {
        using var client = await ConnectAsync(peer, ct).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var frame = new MessageFrame
        {
            From = _self,
            Id = message.Id,
            Text = message.Text,
            SentAt = message.Timestamp,
            ReplyToId = message.ReplyToId,
            ReplyToPreview = message.ReplyToPreview,
            ReplyToFromSelf = message.ReplyToFromSelf,
        };

        await FrameIO.WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);

        // Wait for the ack so the UI can distinguish "sent" from "went nowhere".
        var (kind, _) = await FrameIO.ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (kind != FrameKind.MessageAck)
            throw new InvalidDataException($"Expected an ack, got '{kind}'.");
    }

    /// <summary>
    /// Sends a picture inline: the header frame, then the file's bytes, then waits for the
    /// ack. Pictures are conversation content rather than a file transfer, so there is no
    /// accept prompt and no progress card — the bubble just settles to delivered or failed.
    /// </summary>
    public async Task SendImageAsync(Peer peer, ChatMessage message, CancellationToken ct)
    {
        var path = message.ImagePath
                   ?? throw new InvalidOperationException("That message has no picture attached.");

        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("That picture is no longer on disk.", path);

        if (info.Length > Wire.MaxImageBytes)
        {
            throw new InvalidOperationException(
                $"Pictures are limited to {Wire.MaxImageBytes / (1024 * 1024)} MB — send it as a file instead.");
        }

        using var client = await ConnectAsync(peer, ct).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var frame = new ImageFrame
        {
            From = _self,
            Id = message.Id,
            FileName = message.ImageName ?? info.Name,
            Size = info.Length,
            Width = message.ImageWidth,
            Height = message.ImageHeight,
            Caption = message.Text,
            SentAt = message.Timestamp,
            ReplyToId = message.ReplyToId,
            ReplyToPreview = message.ReplyToPreview,
            ReplyToFromSelf = message.ReplyToFromSelf,
        };

        await FrameIO.WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);

        // Wait for the go-ahead before streaming. A refusal (too big, or not a picture) then
        // arrives as a reason we can show, instead of the broken pipe the receiver would
        // otherwise leave us with halfway through the bytes.
        var (verdictKind, verdictPayload) = await FrameIO.ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (verdictKind != FrameKind.OfferResult)
            throw new InvalidDataException($"Expected a picture result, got '{verdictKind}'.");

        var verdict = FrameIO.Decode<OfferResultFrame>(verdictPayload);
        if (!verdict.Accept)
            throw new OfferRejectedException(verdict.Reason ?? "The other side refused the picture.");

        await using (var file = new FileStream(
                         path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                         CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await StreamExactlyAsync(file, stream, info.Length, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);

        var (kind, _) = await FrameIO.ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (kind != FrameKind.MessageAck)
            throw new InvalidDataException($"Expected an ack, got '{kind}'.");
    }

    /// <summary>
    /// Writes exactly <paramref name="count"/> bytes, padding if the file shrank underneath
    /// us so the receiver's accounting still lines up instead of hanging.
    /// </summary>
    private static async Task StreamExactlyAsync(Stream source, Stream destination, long count, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        var remaining = count;

        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                Array.Clear(buffer, 0, chunk);
                read = chunk;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }
    }

    public async Task SendEditAsync(Peer peer, string messageId, string newText, CancellationToken ct)
    {
        using var client = await ConnectAsync(peer, ct).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var frame = new MessageEditFrame { From = _self, Id = messageId, Text = newText };
        await FrameIO.WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);
        await ReadAckAsync(stream, ct).ConfigureAwait(false);
    }

    public async Task SendDeleteAsync(Peer peer, string messageId, CancellationToken ct)
    {
        using var client = await ConnectAsync(peer, ct).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var frame = new MessageDeleteFrame { From = _self, Id = messageId };
        await FrameIO.WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);
        await ReadAckAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>Puts the sender's emoji on a message, or clears it when empty.</summary>
    public async Task SendReactionAsync(Peer peer, string messageId, string emoji, CancellationToken ct)
    {
        using var client = await ConnectAsync(peer, ct).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var frame = new ReactionFrame { From = _self, Id = messageId, Emoji = emoji };
        await FrameIO.WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);
        await ReadAckAsync(stream, ct).ConfigureAwait(false);
    }

    private static async Task ReadAckAsync(Stream stream, CancellationToken ct)
    {
        var (kind, _) = await FrameIO.ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (kind != FrameKind.MessageAck)
            throw new InvalidDataException($"Expected an ack, got '{kind}'.");
    }

    /// <summary>
    /// Expands the given files and folders into a flat item list and streams them.
    /// The returned <see cref="Transfer"/> is the same instance carried by the events.
    /// </summary>
    public Task<Transfer> SendFilesAsync(Peer peer, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var items = EnumerateItems(paths);
        if (items.Count == 0)
            throw new InvalidOperationException("Nothing to send: no readable files were found in that selection.");

        return SendItemsAsync(peer, items, ct);
    }

    /// <summary>
    /// Streams an already-resolved set of items (each with a <see cref="TransferItem.SourcePath"/>).
    /// Used to retry a failed transfer while keeping its original relative paths.
    /// </summary>
    public async Task<Transfer> SendItemsAsync(Peer peer, IReadOnlyList<TransferItem> items, CancellationToken ct)
    {
        if (items.Count == 0 || items.Any(i => string.IsNullOrEmpty(i.SourcePath)))
            throw new InvalidOperationException("These files can no longer be located to send.");

        var transfer = new Transfer
        {
            Id = Guid.NewGuid().ToString("N"),
            Direction = TransferDirection.Outgoing,
            PeerId = peer.Id,
            PeerName = peer.Name,
            Items = items,
            TotalBytes = items.Sum(i => i.Size),
            Status = TransferStatus.Negotiating,
        };

        TransferStarted?.Invoke(this, new TransferEventArgs(transfer));
        await RunTransferAsync(peer, transfer, ct).ConfigureAwait(false);
        return transfer;
    }

    /// <summary>
    /// Re-runs an existing (failed/declined/cancelled) transfer in place, so the same card
    /// updates instead of a new one appearing. Reuses the transfer's items and id.
    /// </summary>
    public async Task RetryAsync(Peer peer, Transfer transfer, CancellationToken ct)
    {
        if (transfer.Items.Count == 0 || transfer.Items.Any(i => string.IsNullOrEmpty(i.SourcePath)))
            throw new InvalidOperationException("These files can no longer be located to send.");

        transfer.ResetForRetry();

        // Re-announce the same transfer so the view re-activates its existing card.
        TransferStarted?.Invoke(this, new TransferEventArgs(transfer));
        await RunTransferAsync(peer, transfer, ct).ConfigureAwait(false);
    }

    private async Task RunTransferAsync(Peer peer, Transfer transfer, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, transfer.Cancellation.Token);
        var token = linked.Token;

        TcpClient? client = null;

        // A socket receive that is parked waiting for data does NOT observe token
        // cancellation — the only reliable way to unblock it (e.g. cancel while
        // "waiting for the other side") is to close the socket. So dispose it on cancel.
        await using var cancelReg = token.Register(() =>
        {
            try { client?.Dispose(); }
            catch (ObjectDisposedException) { }
        });

        try
        {
            client = await ConnectAsync(peer, token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            var offer = new OfferFrame
            {
                From = _self,
                TransferId = transfer.Id,
                TotalBytes = transfer.TotalBytes,
                Items = [.. transfer.Items.Select(i => new OfferItem { Rel = i.RelativePath, Size = i.Size })],
            };

            await FrameIO.WriteFrameAsync(stream, offer, token).ConfigureAwait(false);

            var (kind, payload) = await FrameIO.ReadFrameAsync(stream, token).ConfigureAwait(false);
            if (kind != FrameKind.OfferResult)
                throw new InvalidDataException($"Expected an offer result, got '{kind}'.");

            var result = FrameIO.Decode<OfferResultFrame>(payload);
            if (!result.Accept)
                throw new OfferRejectedException(result.Reason ?? "The other side declined.");

            transfer.Status = TransferStatus.Running;
            TransferChanged?.Invoke(this, new TransferEventArgs(transfer));

            await StreamFilesAsync(stream, transfer, token).ConfigureAwait(false);

            // Wait for the receiver to confirm it wrote everything to disk.
            var (finalKind, finalPayload) = await FrameIO.ReadFrameAsync(stream, token).ConfigureAwait(false);
            if (finalKind == FrameKind.Complete)
            {
                var complete = FrameIO.Decode<CompleteFrame>(finalPayload);
                if (!complete.Ok)
                    throw new IOException(complete.Reason ?? "The receiver reported a failure.");
            }

            Finish(transfer, TransferStatus.Completed, null);
        }
        catch (OfferRejectedException ex)
        {
            Finish(transfer, TransferStatus.Rejected, ex.Message);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Includes the ObjectDisposedException/IOException from closing the socket above.
            Finish(transfer, TransferStatus.Cancelled,
                transfer.Cancellation.IsCancellationRequested ? "Cancelled." : "Shutting down.");
        }
        catch (Exception ex)
        {
            Finish(transfer, TransferStatus.Failed, ex.Message);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private async Task StreamFilesAsync(Stream stream, Transfer transfer, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        var clock = Stopwatch.StartNew();
        var lastReport = 0L;

        foreach (var item in transfer.Items)
        {
            await using var file = new FileStream(
                item.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var remaining = item.Size;
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, remaining);
                var read = await file.ReadAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    // The file shrank after we sized it; pad so the receiver's
                    // byte accounting still lines up and it does not hang.
                    Array.Clear(buffer, 0, chunk);
                    read = chunk;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                remaining -= read;
                transfer.TransferredBytes += read;

                if (clock.ElapsedMilliseconds - lastReport >= ProgressIntervalMs)
                {
                    lastReport = clock.ElapsedMilliseconds;
                    ReportProgress(transfer, clock);
                }
            }
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);

        // A transfer that finishes inside one throttle window would otherwise never
        // report at all, leaving the progress bar stuck at zero.
        ReportProgress(transfer, clock);
    }

    private void ReportProgress(Transfer transfer, Stopwatch clock)
    {
        var seconds = clock.Elapsed.TotalSeconds;
        transfer.BytesPerSecond = seconds > 0.05 ? transfer.TransferredBytes / seconds : 0;
        TransferChanged?.Invoke(this, new TransferEventArgs(transfer));
    }

    /// <summary>Flattens files and folders into wire items with '/'-separated relative paths.</summary>
    private static List<TransferItem> EnumerateItems(IReadOnlyList<string> paths)
    {
        var items = new List<TransferItem>();

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                items.Add(new TransferItem
                {
                    RelativePath = info.Name,
                    Size = info.Length,
                    SourcePath = info.FullName,
                });
            }
            else if (Directory.Exists(path))
            {
                var root = new DirectoryInfo(path);
                var parent = root.Parent?.FullName ?? root.FullName;

                foreach (var file in root.EnumerateFiles("*", new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
                {
                    var relative = Path.GetRelativePath(parent, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
                    items.Add(new TransferItem
                    {
                        RelativePath = relative,
                        Size = file.Length,
                        SourcePath = file.FullName,
                    });
                }
            }
        }

        return items;
    }

    private static async Task<TcpClient> ConnectAsync(Peer peer, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);

            await client.ConnectAsync(peer.Address, peer.Port, timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"{peer.DisplayLabel} did not answer on {peer.Address}:{peer.Port}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void Finish(Transfer transfer, TransferStatus status, string? error)
    {
        transfer.Status = status;
        transfer.Error = error;
        transfer.FinishedAt = DateTimeOffset.Now;
        TransferFinished?.Invoke(this, new TransferEventArgs(transfer));
    }
}
