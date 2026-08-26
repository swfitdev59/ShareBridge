using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ShareBridge.Core.Models;
using ShareBridge.Core.Protocol;

namespace ShareBridge.Core.Net;

public sealed class IncomingMessageEventArgs(PeerIdentity from, IPAddress address, ChatMessage message) : EventArgs
{
    public PeerIdentity From { get; } = from;
    public IPAddress Address { get; } = address;
    public ChatMessage Message { get; } = message;
}

public sealed class MessageEditEventArgs(PeerIdentity from, string messageId, string text) : EventArgs
{
    public PeerIdentity From { get; } = from;
    public string MessageId { get; } = messageId;
    public string Text { get; } = text;
}

public sealed class MessageDeleteEventArgs(PeerIdentity from, string messageId) : EventArgs
{
    public PeerIdentity From { get; } = from;
    public string MessageId { get; } = messageId;
}

public sealed class TransferEventArgs(Transfer transfer) : EventArgs
{
    public Transfer Transfer { get; } = transfer;
}

public sealed class PeerSeenEventArgs(PeerIdentity identity, IPAddress address) : EventArgs
{
    public PeerIdentity Identity { get; } = identity;
    public IPAddress Address { get; } = address;
}

/// <summary>
/// Accepts inbound TCP connections. Each connection carries exactly one exchange:
/// a chat message, or a file offer followed by the raw bytes.
/// </summary>
public sealed class TransferServer : IAsyncDisposable
{
    private const int CopyBufferSize = 128 * 1024;
    private const int ProgressIntervalMs = 100;

    private readonly AppSettings _settings;
    private readonly PeerIdentity _self;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public TransferServer(AppSettings settings, PeerIdentity self)
    {
        _settings = settings;
        _self = self;
    }

    /// <summary>The port actually bound, which differs from the configured one when it was taken.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Asked for a decision on each inbound offer. Returning false rejects it.
    /// When null, offers are accepted only if <see cref="AppSettings.AutoAcceptFiles"/> is set.
    /// </summary>
    public Func<Transfer, Task<bool>>? OfferDecision { get; set; }

    public event EventHandler<IncomingMessageEventArgs>? MessageReceived;

    public event EventHandler<MessageEditEventArgs>? MessageEdited;

    public event EventHandler<MessageDeleteEventArgs>? MessageDeleted;

    /// <summary>Raised before an offer is surfaced, so the sender can be registered as a peer first.</summary>
    public event EventHandler<PeerSeenEventArgs>? PeerSeen;

    public event EventHandler<TransferEventArgs>? TransferStarted;
    public event EventHandler<TransferEventArgs>? TransferChanged;
    public event EventHandler<TransferEventArgs>? TransferFinished;
    public event EventHandler<string>? Log;

    public void Start()
    {
        if (_listener is not null)
            return;

        _listener = BindListener(_settings.TransferPort);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);

        Log?.Invoke(this, $"Listening for transfers on TCP {Port}");
    }

    /// <summary>
    /// Prefers the configured port but falls back to an ephemeral one, so a second
    /// instance on the same machine still starts. The real port is advertised in discovery.
    /// </summary>
    private static TcpListener BindListener(int preferredPort)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, preferredPort);
            listener.Start();
            return listener;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            var fallback = new TcpListener(IPAddress.Any, 0);
            fallback.Start();
            return fallback;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;

        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();

                var (kind, payload) = await FrameIO.ReadFrameAsync(stream, ct).ConfigureAwait(false);

                switch (kind)
                {
                    case FrameKind.Message:
                        await HandleMessageAsync(stream, payload, remote, ct).ConfigureAwait(false);
                        break;

                    case FrameKind.MessageEdit:
                        await HandleEditAsync(stream, payload, remote, ct).ConfigureAwait(false);
                        break;

                    case FrameKind.MessageDelete:
                        await HandleDeleteAsync(stream, payload, remote, ct).ConfigureAwait(false);
                        break;

                    case FrameKind.Offer:
                        await HandleOfferAsync(stream, payload, remote, ct).ConfigureAwait(false);
                        break;

                    default:
                        Log?.Invoke(this, $"Ignoring unknown frame '{kind}' from {remote}");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                       or EndOfStreamException or InvalidDataException or JsonException)
        {
            Log?.Invoke(this, $"Connection from {remote} ended: {ex.Message}");
        }
    }

    private async Task HandleMessageAsync(Stream stream, byte[] payload, IPAddress remote, CancellationToken ct)
    {
        var frame = FrameIO.Decode<MessageFrame>(payload);

        var message = new ChatMessage
        {
            Id = string.IsNullOrEmpty(frame.Id) ? Guid.NewGuid().ToString("N") : frame.Id,
            PeerId = frame.From.Id,
            Text = frame.Text,
            Direction = MessageDirection.Incoming,
            Timestamp = frame.SentAt,
            Status = MessageStatus.Delivered,
        };

        await FrameIO.WriteFrameAsync(stream, new MessageAckFrame { Id = message.Id }, ct).ConfigureAwait(false);

        MessageReceived?.Invoke(this, new IncomingMessageEventArgs(frame.From, remote, message));
    }

    private async Task HandleEditAsync(Stream stream, byte[] payload, IPAddress remote, CancellationToken ct)
    {
        var frame = FrameIO.Decode<MessageEditFrame>(payload);
        await FrameIO.WriteFrameAsync(stream, new MessageAckFrame { Id = frame.Id }, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(frame.Id))
        {
            PeerSeen?.Invoke(this, new PeerSeenEventArgs(frame.From, remote));
            MessageEdited?.Invoke(this, new MessageEditEventArgs(frame.From, frame.Id, frame.Text));
        }
    }

    private async Task HandleDeleteAsync(Stream stream, byte[] payload, IPAddress remote, CancellationToken ct)
    {
        var frame = FrameIO.Decode<MessageDeleteFrame>(payload);
        await FrameIO.WriteFrameAsync(stream, new MessageAckFrame { Id = frame.Id }, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(frame.Id))
        {
            PeerSeen?.Invoke(this, new PeerSeenEventArgs(frame.From, remote));
            MessageDeleted?.Invoke(this, new MessageDeleteEventArgs(frame.From, frame.Id));
        }
    }

    private async Task HandleOfferAsync(Stream stream, byte[] payload, IPAddress remote, CancellationToken ct)
    {
        var offer = FrameIO.Decode<OfferFrame>(payload);

        var (items, rejection) = ValidateOffer(offer);
        if (rejection is not null)
        {
            await FrameIO.WriteFrameAsync(stream, new OfferResultFrame { Accept = false, Reason = rejection }, ct)
                .ConfigureAwait(false);
            Log?.Invoke(this, $"Rejected offer from {remote}: {rejection}");
            return;
        }

        var peerName = string.IsNullOrWhiteSpace(offer.From.Name) ? remote.ToString() : offer.From.Name;

        // Register the sender before the transfer surfaces, so the UI already has a
        // peer to hang it on even if we never saw their broadcast.
        PeerSeen?.Invoke(this, new PeerSeenEventArgs(offer.From, remote));

        var transfer = new Transfer
        {
            Id = string.IsNullOrEmpty(offer.TransferId) ? Guid.NewGuid().ToString("N") : offer.TransferId,
            Direction = TransferDirection.Incoming,
            PeerId = offer.From.Id,
            PeerName = peerName,
            Items = items,
            TotalBytes = items.Sum(i => i.Size),
            Status = TransferStatus.Negotiating,
            DestinationFolder = Path.Combine(_settings.DownloadFolder, SafePath.SanitizeFolderName(peerName)),
        };

        TransferStarted?.Invoke(this, new TransferEventArgs(transfer));

        bool accepted;
        try
        {
            accepted = _settings.AutoAcceptFiles
                       || (OfferDecision is not null && await OfferDecision(transfer).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            accepted = false;
            Log?.Invoke(this, $"Offer decision failed: {ex.Message}");
        }

        if (!accepted)
        {
            Finish(transfer, TransferStatus.Rejected, "Declined on this machine.");
            await FrameIO.WriteFrameAsync(stream, new OfferResultFrame { Accept = false, Reason = "Declined by user." }, ct)
                .ConfigureAwait(false);
            return;
        }

        await FrameIO.WriteFrameAsync(stream, new OfferResultFrame { Accept = true }, ct).ConfigureAwait(false);

        transfer.Status = TransferStatus.Running;
        TransferChanged?.Invoke(this, new TransferEventArgs(transfer));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, transfer.Cancellation.Token);

        try
        {
            await ReceiveFilesAsync(stream, transfer, linked.Token).ConfigureAwait(false);
            Finish(transfer, TransferStatus.Completed, null);

            await FrameIO.WriteFrameAsync(stream, new CompleteFrame { Ok = true }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(transfer, TransferStatus.Cancelled, "Cancelled on this machine.");
            throw;
        }
        catch (Exception ex)
        {
            Finish(transfer, TransferStatus.Failed, ex.Message);
            throw;
        }
    }

    /// <summary>Checks sizes and path safety before anything touches the disk.</summary>
    private static (List<TransferItem> Items, string? Rejection) ValidateOffer(OfferFrame offer)
    {
        if (offer.V != Wire.ProtocolVersion)
            return ([], $"Unsupported protocol version {offer.V}.");

        if (offer.Items.Count == 0)
            return ([], "Offer contained no files.");

        var items = new List<TransferItem>(offer.Items.Count);
        foreach (var item in offer.Items)
        {
            if (item.Size < 0)
                return ([], "Offer declared a negative file size.");

            var safe = SafePath.SanitizeRelative(item.Rel);
            if (safe is null)
                return ([], $"Unsafe path in offer: '{item.Rel}'.");

            items.Add(new TransferItem { RelativePath = safe.Replace(Path.DirectorySeparatorChar, '/'), Size = item.Size });
        }

        return (items, null);
    }

    private async Task ReceiveFilesAsync(Stream stream, Transfer transfer, CancellationToken ct)
    {
        var root = transfer.DestinationFolder!;
        Directory.CreateDirectory(root);

        var buffer = new byte[CopyBufferSize];
        var clock = Stopwatch.StartNew();
        var lastReport = 0L;

        foreach (var item in transfer.Items)
        {
            var localRelative = item.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var target = SafePath.ResolveUnder(root, localRelative)
                         ?? throw new InvalidDataException($"Path '{item.RelativePath}' escapes the download folder.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            target = SafePath.MakeUnique(target);

            var partial = target + ".sbpart";

            try
            {
                await using (var file = new FileStream(
                                 partial, FileMode.Create, FileAccess.Write, FileShare.None,
                                 CopyBufferSize, FileOptions.Asynchronous))
                {
                    var remaining = item.Size;
                    while (remaining > 0)
                    {
                        var chunk = (int)Math.Min(buffer.Length, remaining);
                        var read = await stream.ReadAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
                        if (read <= 0)
                            throw new EndOfStreamException($"Sender closed the connection with {remaining} bytes left.");

                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                        remaining -= read;
                        transfer.TransferredBytes += read;

                        if (clock.ElapsedMilliseconds - lastReport >= ProgressIntervalMs)
                        {
                            lastReport = clock.ElapsedMilliseconds;
                            ReportProgress(transfer, clock);
                        }
                    }
                }

                File.Move(partial, target, overwrite: true);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }

        ReportProgress(transfer, clock);
    }

    private void ReportProgress(Transfer transfer, Stopwatch clock)
    {
        var seconds = clock.Elapsed.TotalSeconds;
        transfer.BytesPerSecond = seconds > 0.05 ? transfer.TransferredBytes / seconds : 0;
        TransferChanged?.Invoke(this, new TransferEventArgs(transfer));
    }

    private void Finish(Transfer transfer, TransferStatus status, string? error)
    {
        transfer.Status = status;
        transfer.Error = error;
        transfer.FinishedAt = DateTimeOffset.Now;
        TransferFinished?.Invoke(this, new TransferEventArgs(transfer));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
            return;

        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }
        }

        _cts.Dispose();
        _cts = null;
    }
}
