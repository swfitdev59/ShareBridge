namespace ShareBridge.Core.Models;

public enum MessageDirection
{
    Incoming,
    Outgoing,
}

public enum MessageStatus
{
    Sending,
    Delivered,
    Failed,
}

public sealed class ChatMessage
{
    public required string Id { get; init; }
    public required string PeerId { get; init; }

    /// <summary>Body text. Mutable so a message can be edited in place after sending.</summary>
    public required string Text { get; set; }

    public required MessageDirection Direction { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public MessageStatus Status { get; set; } = MessageStatus.Delivered;

    /// <summary>True once the text has been changed after the message was first sent.</summary>
    public bool Edited { get; set; }

    /// <summary>True once the message has been unsent; the bubble becomes a placeholder.</summary>
    public bool Deleted { get; set; }
}
