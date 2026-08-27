using System.IO;
using System.Windows;
using Microsoft.Win32;
using ShareBridge.Wpf.Infrastructure;

namespace ShareBridge.Wpf.Views;

/// <summary>
/// The "save a copy of this picture" flow, shared by the bubble's context menu and the
/// full-size viewer. Pictures live in the media store; this is how one gets out of it.
/// </summary>
internal static class PictureActions
{
    /// <summary>
    /// Asks where to put a copy and writes it there. Returns a line for the status bar, or
    /// null when the user cancelled the dialog.
    /// </summary>
    public static string? SaveCopy(Window owner, string? sourcePath, string suggestedName)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            return "That picture is no longer on disk.";

        var extension = Path.GetExtension(suggestedName);
        if (string.IsNullOrEmpty(extension))
            extension = ".png";

        var dialog = new SaveFileDialog
        {
            Title = "Save picture",
            FileName = suggestedName,
            DefaultExt = extension,
            Filter = $"Picture (*{extension})|*{extension}|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(owner) != true)
            return null;

        try
        {
            ImageTools.SaveCopy(sourcePath, dialog.FileName);
            return $"Saved {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"Could not save that picture: {ex.Message}";
        }
    }
}
