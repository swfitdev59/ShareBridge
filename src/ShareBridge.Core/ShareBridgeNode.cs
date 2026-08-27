using ShareBridge.Core.Models;
using ShareBridge.Core.Net;

namespace ShareBridge.Core;

/// <summary>
/// One running ShareBridge participant: presence, inbound listener and outbound sender.
/// The UI talks only to this class.
/// </summary>
public sealed class ShareBridgeNode : IAsyncDisposable
{
    private readonly DiscoveryService _discovery;
    private readonly TransferServer _server;
    private readonly TransferClient _client;

    public ShareBridgeNode(AppSettings settings)
    {
        Settings = settings;

        // Stable per-user identity so chat history stays attributable across restarts.
        // SHAREBRIDGE_ID overrides it, which lets several instances run on one machine
        // for testing (they would otherwise share an id and filter each other out).
        var envId = Environment.GetEnvironmentVariable("SHAREBRIDGE_ID");
        var id = !string.IsNullOrWhiteSpace(envId)
            ? envId.Trim()
            : string.IsNullOrWhiteSpace(settings.NodeId) ? Guid.NewGuid().ToString("N") : settings.NodeId;

        Self = new PeerIdentity
        {
            Id = id,
            Name = settings.DisplayName,
            Host = Environment.MachineName,
        };

        _discovery = new DiscoveryService(settings, Self);
        _server = new TransferServer(settings, Self);
        _client = new TransferClient(Self);

        _discovery.PeerDiscovered += (_, e) => PeerDiscovered?.Invoke(this, e);
        _discovery.PeerUpdated += (_, e) => PeerUpdated?.Invoke(this, e);
        _discovery.PeerLost += (_, e) => PeerLost?.Invoke(this, e);
        _discovery.Log += (_, m) => Log?.Invoke(this, m);

        _server.Log += (_, m) => Log?.Invoke(this, m);
        _server.MessageReceived += OnServerMessageReceived;
        _server.MessageEdited += (_, e) => MessageEdited?.Invoke(this, e);
        _server.MessageDeleted += (_, e) => MessageDeleted?.Invoke(this, e);
        _server.PeerSeen += (_, e) => _discovery.TrackExternal(e.Identity, e.Address);
        _server.TransferStarted += (_, e) => TransferStarted?.Invoke(this, e);
        _server.TransferChanged += (_, e) => TransferChanged?.Invoke(this, e);
        _server.TransferFinished += (_, e) => TransferFinished?.Invoke(this, e);

        _client.TransferStarted += (_, e) => TransferStarted?.Invoke(this, e);
        _client.TransferChanged += (_, e) => TransferChanged?.Invoke(this, e);
        _client.TransferFinished += (_, e) => TransferFinished?.Invoke(this, e);
    }

    public AppSettings Settings { get; }

    public PeerIdentity Self { get; }

    /// <summary>Port the listener actually bound; valid after <see cref="StartAsync"/>.</summary>
    public int ListeningPort => _server.Port;

    public IReadOnlyCollection<Peer> Peers => _discovery.Peers;

    /// <summary>Asked whether to accept each inbound offer. See <see cref="TransferServer.OfferDecision"/>.</summary>
    public Func<Transfer, CancellationToken, Task<bool>>? OfferDecision
    {
        get => _server.OfferDecision;
        set => _server.OfferDecision = value;
    }

    public event EventHandler<PeerEventArgs>? PeerDiscovered;
    public event EventHandler<PeerEventArgs>? PeerUpdated;
    public event EventHandler<PeerEventArgs>? PeerLost;
    public event EventHandler<IncomingMessageEventArgs>? MessageReceived;
    public event EventHandler<MessageEditEventArgs>? MessageEdited;
    public event EventHandler<MessageDeleteEventArgs>? MessageDeleted;
    public event EventHandler<TransferEventArgs>? TransferStarted;
    public event EventHandler<TransferEventArgs>? TransferChanged;
    public event EventHandler<TransferEventArgs>? TransferFinished;
    public event EventHandler<string>? Log;

    public async Task StartAsync()
    {
        // Bind the listener first: discovery must advertise the port we really got.
        _server.Start();
        Self.Port = _server.Port;

        _discovery.Start();
        await _discovery.AnnounceArrivalAsync().ConfigureAwait(false);
    }

    public Peer? FindPeer(string id) => _discovery.Find(id);

    /// <summary>
    /// Sends the given chat message, updating its <see cref="MessageStatus"/> to reflect the
    /// outcome. The message keeps its id on the wire, so later edits/deletes match on the
    /// receiver. Never throws for ordinary network failures.
    /// </summary>
    public async Task<ChatMessage> SendMessageAsync(Peer peer, ChatMessage message, CancellationToken ct = default)
    {
        message.Status = MessageStatus.Sending;

        try
        {
            await _client.SendMessageAsync(peer, message, ct).ConfigureAwait(false);
            message.Status = MessageStatus.Delivered;
        }
        catch (Exception ex)
        {
            message.Status = MessageStatus.Failed;
            Log?.Invoke(this, $"Message to {peer.DisplayLabel} failed: {ex.Message}");
        }

        return message;
    }

    /// <summary>
    /// Best-effort edit of a delivered message. Returns true if the peer confirmed it;
    /// a false result (peer offline, etc.) does not stop the local change from standing.
    /// </summary>
    public async Task<bool> EditMessageAsync(Peer peer, string messageId, string newText, CancellationToken ct = default)
    {
        try
        {
            await _client.SendEditAsync(peer, messageId, newText, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"Edit to {peer.DisplayLabel} did not reach them: {ex.Message}");
            return false;
        }
    }

    /// <summary>Best-effort delete of a delivered message. See <see cref="EditMessageAsync"/>.</summary>
    public async Task<bool> DeleteMessageAsync(Peer peer, string messageId, CancellationToken ct = default)
    {
        try
        {
            await _client.SendDeleteAsync(peer, messageId, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"Delete to {peer.DisplayLabel} did not reach them: {ex.Message}");
            return false;
        }
    }

    public Task<Transfer> SendFilesAsync(Peer peer, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        _client.SendFilesAsync(peer, paths, ct);

    /// <summary>Resends an already-resolved item set as a new transfer.</summary>
    public Task<Transfer> SendItemsAsync(Peer peer, IReadOnlyList<TransferItem> items, CancellationToken ct = default) =>
        _client.SendItemsAsync(peer, items, ct);

    /// <summary>Re-runs a failed transfer in place (same card), reusing its items and id.</summary>
    public Task RetryTransferAsync(Peer peer, Transfer transfer, CancellationToken ct = default) =>
        _client.RetryAsync(peer, transfer, ct);

    /// <summary>
    /// Resends an existing (failed) message, keeping its id so history and any later
    /// edit/delete still refer to the same message. Updates and returns its status.
    /// </summary>
    public async Task<ChatMessage> RetryMessageAsync(Peer peer, ChatMessage message, CancellationToken ct = default)
    {
        message.Status = MessageStatus.Sending;
        try
        {
            await _client.SendMessageAsync(peer, message, ct).ConfigureAwait(false);
            message.Status = MessageStatus.Delivered;
        }
        catch (Exception ex)
        {
            message.Status = MessageStatus.Failed;
            Log?.Invoke(this, $"Retry to {peer.DisplayLabel} failed: {ex.Message}");
        }

        return message;
    }

    /// <summary>Re-broadcasts identity after the display name changes.</summary>
    public async Task RefreshIdentityAsync()
    {
        Self.Name = Settings.DisplayName;
        await _discovery.AnnounceAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _discovery.DisposeAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private void OnServerMessageReceived(object? sender, IncomingMessageEventArgs e)
    {
        // A message can arrive from a peer whose broadcast we missed; register them
        // so the conversation has somewhere to live.
        _discovery.TrackExternal(e.From, e.Address);
        MessageReceived?.Invoke(this, e);
    }
}
