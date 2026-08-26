using ShareBridge.Core.Models;
using ShareBridge.Wpf.Infrastructure;

namespace ShareBridge.Wpf.ViewModels;

/// <summary>One message that matched a history search, with a link back to its conversation.</summary>
public sealed class SearchResultViewModel(PeerViewModel peer, ChatMessage message)
{
    public PeerViewModel Peer { get; } = peer;
    public ChatMessage Message { get; } = message;

    public string PeerName => Peer.Name;

    public string Sender => Message.Direction == MessageDirection.Outgoing ? "You" : Peer.Name;

    public string Snippet => Message.Text;

    public string TimestampText => Format.Timestamp(Message.Timestamp);
}
