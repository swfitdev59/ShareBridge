using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ShareBridge.Core.Models;
using ShareBridge.Wpf.Infrastructure;

namespace ShareBridge.Wpf.ViewModels;

/// <summary>One emoji on a message, and whether this machine's user is behind it.</summary>
public sealed class ReactionChip(string emoji, int count, bool mine, ICommand toggle)
{
    public string Emoji { get; } = emoji;

    public int Count { get; } = count;

    /// <summary>True when the local user is one of the people who put this emoji on.</summary>
    public bool Mine { get; } = mine;

    /// <summary>Blank for a single reaction — a lone "1" beside the emoji is just noise.</summary>
    public string CountText { get; } = count > 1 ? count.ToString() : string.Empty;

    /// <summary>
    /// Clicking the chip puts this emoji on, or takes it back when it was already yours.
    /// The chip carries its own command because the click arrives through an InputBinding,
    /// which sits outside the visual tree and so cannot resolve a RelativeSource binding.
    /// </summary>
    public ICommand Toggle { get; } = toggle;
}

public sealed class MessageViewModel : ObservableObject
{
    /// <summary>Longest a quoted line gets before it is cut short.</summary>
    private const int QuoteLength = 80;

    /// <summary>The emoji offered by the reaction picker.</summary>
    public static IReadOnlyList<string> ReactionChoices { get; } =
        ["👍", "❤️", "😂", "😮", "😢", "🙏", "🔥", "🎉"];

    private readonly PeerViewModel _peer;

    private bool _isHighlighted;
    private bool _isEditing;
    private bool _isReactionPickerOpen;
    private string _editDraft = string.Empty;
    private BitmapSource? _preview;
    private bool _previewLoaded;

    public MessageViewModel(ChatMessage message, PeerViewModel peer)
    {
        Model = message;
        _peer = peer;

        ReplyCommand = new RelayCommand(_ => ReplyRequested?.Invoke());
        OpenReplyCommand = new RelayCommand(_ => ReplyJumpRequested?.Invoke(), _ => IsReply);
        ReactCommand = new RelayCommand(emoji => React(emoji as string));
        OpenReactionPickerCommand = new RelayCommand(_ => IsReactionPickerOpen = true);

        BeginEditCommand = new RelayCommand(_ => BeginEdit(), _ => CanModify);
        CommitEditCommand = new RelayCommand(_ => CommitEdit());
        CancelEditCommand = new RelayCommand(_ => CancelEdit());
        DeleteCommand = new RelayCommand(_ => DeleteRequested?.Invoke(), _ => CanModify);
        CopyCommand = new RelayCommand(_ => CopyText(), _ => Model.Text.Length > 0);
        RetryCommand = new RelayCommand(_ => RetryRequested?.Invoke(), _ => IsFailed);
        OpenImageCommand = new RelayCommand(_ => OpenRequested?.Invoke(), _ => ImageReady);
        CopyImageCommand = new RelayCommand(_ => ImageTools.CopyToClipboard(Model.ImagePath), _ => ImageReady);
        SaveImageCommand = new RelayCommand(_ => SaveRequested?.Invoke(), _ => ImageReady);
        ShowInFolderCommand = new RelayCommand(_ => ImageTools.ShowInFolder(Model.ImagePath), _ => ImageReady);
    }

    public ChatMessage Model { get; }

    /// <summary>Raised when the user confirms an edit, carrying the new text.</summary>
    public event Action<string>? EditCommitted;

    /// <summary>Raised when the user chooses to delete this message.</summary>
    public event Action? DeleteRequested;

    /// <summary>Raised when the user retries a failed message.</summary>
    public event Action? RetryRequested;

    /// <summary>Raised when the user opens this message's picture full size.</summary>
    public event Action? OpenRequested;

    /// <summary>Raised when the user saves a copy of this message's picture elsewhere.</summary>
    public event Action? SaveRequested;

    /// <summary>Raised when the user starts a reply to this message.</summary>
    public event Action? ReplyRequested;

    /// <summary>Raised when the user clicks this message's quote, to go to what it quotes.</summary>
    public event Action? ReplyJumpRequested;

    /// <summary>Raised when the user picks an emoji, or clears theirs by picking it again.</summary>
    public event Action<string>? ReactionRequested;

    public string Text => Model.Text;

    /// <summary>False for a picture sent without a caption, whose bubble is the picture alone.</summary>
    public bool HasText => Model.Text.Length > 0;

    public bool IsOutgoing => Model.Direction == MessageDirection.Outgoing;

    /// <summary>Only your own delivered messages can be edited or deleted.</summary>
    public bool CanModify => IsOutgoing && Model.Status != MessageStatus.Sending;

    public bool Edited => Model.Edited;

    public string TimestampText => Format.Timestamp(Model.Timestamp);

    public bool IsFailed => Model.Status == MessageStatus.Failed;

    public bool IsSending => Model.Status == MessageStatus.Sending;

    public string StatusText => Model.Status switch
    {
        MessageStatus.Sending => "sending…",
        MessageStatus.Failed => "not delivered",
        _ => string.Empty,
    };

    /// <summary>Briefly set when the user jumps to this message from search, to flash it.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => Set(ref _isHighlighted, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set => Set(ref _isEditing, value);
    }

    public string EditDraft
    {
        get => _editDraft;
        set => Set(ref _editDraft, value);
    }

    public ICommand BeginEditCommand { get; }
    public ICommand CommitEditCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand OpenImageCommand { get; }
    public ICommand CopyImageCommand { get; }
    public ICommand SaveImageCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand ReplyCommand { get; }
    public ICommand OpenReplyCommand { get; }
    public ICommand ReactCommand { get; }
    public ICommand OpenReactionPickerCommand { get; }

    // -----------------------------------------------------------------------
    // Replies
    // -----------------------------------------------------------------------

    public bool IsReply => Model.IsReply;

    /// <summary>Who wrote the quoted message, as the quote block labels it.</summary>
    public string ReplyAuthor => Model.ReplyToFromSelf ? "You" : _peer.Name;

    public string ReplyPreview => Model.ReplyToPreview ?? string.Empty;

    /// <summary>
    /// The single line that stands in for this message when something quotes it. A picture
    /// has no text to borrow, so it gets a marker instead.
    /// </summary>
    public string QuotePreview
    {
        get
        {
            var text = Shorten(Model.Text);

            if (!Model.HasImage)
                return text;

            return text.Length > 0 ? $"📷 {text}" : "📷 Photo";
        }
    }

    private static string Shorten(string value)
    {
        var flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= QuoteLength ? flat : flat[..(QuoteLength - 1)] + "…";
    }

    // -----------------------------------------------------------------------
    // Reactions
    // -----------------------------------------------------------------------

    /// <summary>The picker's popup is open over this bubble.</summary>
    public bool IsReactionPickerOpen
    {
        get => _isReactionPickerOpen;
        set => Set(ref _isReactionPickerOpen, value);
    }

    public bool HasReactions => Model.HasReactions;

    /// <summary>
    /// The chips shown under the bubble. A conversation has two people, so the same emoji
    /// from both collapses into one chip with a count.
    /// </summary>
    public IReadOnlyList<ReactionChip> ReactionChips
    {
        get
        {
            var mine = Model.MyReaction;
            var theirs = Model.PeerReaction;

            if (string.IsNullOrEmpty(mine))
                return string.IsNullOrEmpty(theirs) ? [] : [Chip(theirs, 1, mine: false)];

            if (string.IsNullOrEmpty(theirs))
                return [Chip(mine, 1, mine: true)];

            return mine == theirs
                ? [Chip(mine, 2, mine: true)]
                : [Chip(mine, 1, mine: true), Chip(theirs, 1, mine: false)];
        }
    }

    private ReactionChip Chip(string emoji, int count, bool mine) =>
        new(emoji, count, mine, new RelayCommand(_ => React(emoji)));

    /// <summary>Re-reads both sides' emoji after either one changes.</summary>
    public void RefreshReactions() => RaiseAll(nameof(HasReactions), nameof(ReactionChips));

    private void React(string? emoji)
    {
        IsReactionPickerOpen = false;

        if (!string.IsNullOrEmpty(emoji))
            ReactionRequested?.Invoke(emoji);
    }

    // -----------------------------------------------------------------------
    // Picture messages
    // -----------------------------------------------------------------------

    public bool HasImage => Model.HasImage;

    public string? ImagePath => Model.ImagePath;

    public string ImageName => Model.ImageName ?? "image";

    /// <summary>
    /// The bubble preview, decoded on first use and then held — the timeline is not
    /// virtualized, so this happens once per picture per session. Null when the file has
    /// gone missing or will not decode, which the bubble shows as a placeholder.
    /// </summary>
    public BitmapSource? Preview
    {
        get
        {
            if (_previewLoaded)
                return _preview;

            _previewLoaded = true;
            _preview = ImageTools.Load(Model.ImagePath);
            return _preview;
        }
    }

    /// <summary>True once there is a picture on disk to open, copy or save.</summary>
    public bool ImageReady => HasImage && Preview is not null;

    /// <summary>True for a picture message whose file is gone — a moved or cleaned-up file.</summary>
    public bool ImageMissing => HasImage && Preview is null;

    /// <summary>"1920 × 1080 · 240 KB", dropping the part that is not known.</summary>
    public string ImageDetailText
    {
        get
        {
            var size = Model.ImageBytes > 0 ? Format.Bytes(Model.ImageBytes) : string.Empty;
            var pixels = Model.ImageWidth > 0 && Model.ImageHeight > 0
                ? $"{Model.ImageWidth} × {Model.ImageHeight}"
                : string.Empty;

            return string.Join(" · ", new[] { pixels, size }.Where(p => p.Length > 0));
        }
    }

    /// <summary>Called after the send completes so the bubble reflects the final state.</summary>
    public void RefreshStatus() =>
        RaiseAll(nameof(IsFailed), nameof(IsSending), nameof(StatusText), nameof(CanModify));

    /// <summary>Re-reads text and edited state after an edit (local or from the peer).</summary>
    public void RefreshText() =>
        RaiseAll(nameof(Text), nameof(HasText), nameof(Edited), nameof(CanModify), nameof(QuotePreview));

    private void BeginEdit()
    {
        EditDraft = Model.Text;
        IsEditing = true;
    }

    private void CommitEdit()
    {
        var text = EditDraft.Trim();
        IsEditing = false;

        // An unchanged edit is a cancel — as is an empty one, unless there is a picture to
        // carry the message, in which case clearing the caption is a real edit.
        if (text == Model.Text || (text.Length == 0 && !HasImage))
            return;

        EditCommitted?.Invoke(text);
    }

    private void CancelEdit() => IsEditing = false;

    private void CopyText()
    {
        try
        {
            Clipboard.SetText(Model.Text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // The clipboard can be briefly locked by another app; a failed copy is not fatal.
        }
    }

    /// <summary>File name the save dialog should offer for this message's picture.</summary>
    public string SuggestedSaveName
    {
        get
        {
            var name = Path.GetFileName(ImageName);
            return string.IsNullOrWhiteSpace(name) ? "image.png" : name;
        }
    }
}
