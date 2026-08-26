using System.Windows;
using System.Windows.Input;
using ShareBridge.Core.Models;
using ShareBridge.Wpf.Infrastructure;

namespace ShareBridge.Wpf.ViewModels;

public sealed class MessageViewModel : ObservableObject
{
    private bool _isHighlighted;
    private bool _isEditing;
    private string _editDraft = string.Empty;

    public MessageViewModel(ChatMessage message)
    {
        Model = message;

        BeginEditCommand = new RelayCommand(_ => BeginEdit(), _ => CanModify);
        CommitEditCommand = new RelayCommand(_ => CommitEdit());
        CancelEditCommand = new RelayCommand(_ => CancelEdit());
        DeleteCommand = new RelayCommand(_ => DeleteRequested?.Invoke(), _ => CanModify);
        CopyCommand = new RelayCommand(_ => CopyText(), _ => !Model.Deleted && Model.Text.Length > 0);
    }

    public ChatMessage Model { get; }

    /// <summary>Raised when the user confirms an edit, carrying the new text.</summary>
    public event Action<string>? EditCommitted;

    /// <summary>Raised when the user chooses to delete this message.</summary>
    public event Action? DeleteRequested;

    public string Text => Model.Text;

    public bool IsOutgoing => Model.Direction == MessageDirection.Outgoing;

    public bool IsDeleted => Model.Deleted;

    /// <summary>Only your own delivered, non-deleted messages can be edited or deleted.</summary>
    public bool CanModify => IsOutgoing && Model.Status != MessageStatus.Sending && !Model.Deleted;

    /// <summary>Show the "edited" marker only for an edit that has not since been deleted.</summary>
    public bool ShowEdited => Model.Edited && !Model.Deleted;

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

    /// <summary>Called after the send completes so the bubble reflects the final state.</summary>
    public void RefreshStatus() =>
        RaiseAll(nameof(IsFailed), nameof(IsSending), nameof(StatusText), nameof(CanModify));

    /// <summary>Re-reads text, edited and deleted state after a change (local or from the peer).</summary>
    public void RefreshText() =>
        RaiseAll(nameof(Text), nameof(ShowEdited), nameof(IsDeleted), nameof(CanModify));

    private void BeginEdit()
    {
        EditDraft = Model.Text;
        IsEditing = true;
    }

    private void CommitEdit()
    {
        var text = EditDraft.Trim();
        IsEditing = false;

        // An empty edit or an unchanged one is treated as a cancel.
        if (text.Length == 0 || text == Model.Text)
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
}
