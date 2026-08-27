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

    /// <summary>Body text, or the caption when the message carries a picture.
    /// Mutable so a message can be edited in place after sending.</summary>
    public required string Text { get; set; }

    public required MessageDirection Direction { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public MessageStatus Status { get; set; } = MessageStatus.Delivered;

    /// <summary>True once the text has been changed after the message was first sent.</summary>
    public bool Edited { get; set; }

    /// <summary>True once the message has been unsent; the bubble becomes a placeholder.</summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// Local path of the picture this message carries, or null for a plain text message.
    /// Both ends keep their copy in the media store, so a bubble still renders after the
    /// original file is moved or deleted.
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>File name the picture was sent under; used when saving a copy elsewhere.</summary>
    public string? ImageName { get; set; }

    /// <summary>Pixel size of the picture, shown next to it. Zero when it could not be read.</summary>
    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    /// <summary>Size of the picture file in bytes.</summary>
    public long ImageBytes { get; set; }

    public bool HasImage => !string.IsNullOrEmpty(ImagePath);

    /// <summary>Id of the message this one replies to, or null for an ordinary message.</summary>
    public string? ReplyToId { get; set; }

    /// <summary>
    /// One line of the quoted message, captured when the reply was written. Kept alongside
    /// the id so the quote still reads correctly if the original is later deleted, or was
    /// never on this machine at all.
    /// </summary>
    public string? ReplyToPreview { get; set; }

    /// <summary>
    /// True when the quoted message was written on this machine. Always from the local point
    /// of view, so it is flipped when a reply arrives from the other side.
    /// </summary>
    public bool ReplyToFromSelf { get; set; }

    public bool IsReply => !string.IsNullOrEmpty(ReplyToId);

    /// <summary>
    /// Emoji this machine's user put on the message, or null for none. A conversation has
    /// exactly two participants, so one slot each is all a reaction set can ever need.
    /// </summary>
    public string? MyReaction { get; set; }

    /// <summary>Emoji the other side put on the message.</summary>
    public string? PeerReaction { get; set; }

    public bool HasReactions => !string.IsNullOrEmpty(MyReaction) || !string.IsNullOrEmpty(PeerReaction);
}
