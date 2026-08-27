using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using ShareBridge.Core.Persistence;

namespace ShareBridge.Wpf.Infrastructure;

/// <summary>
/// Decoding, clipboard and shell plumbing for picture messages. Everything here is
/// best-effort: a picture that will not decode returns null rather than throwing, so a
/// broken file costs one placeholder bubble instead of the conversation.
/// </summary>
public static class ImageTools
{
    /// <summary>Widest a bubble preview is ever shown, and so the widest we bother decoding.</summary>
    public const int PreviewWidth = 640;

    /// <summary>Clipboard flavour written by browsers and modern shot tools; keeps alpha,
    /// which the plain <see cref="Clipboard.GetImage"/> (DIB) path flattens to black.</summary>
    private const string PngFormat = "PNG";

    private static readonly string[] EmptyPaths = [];

    public static bool IsPicture(string? path) => MediaStore.IsPictureName(path);

    /// <summary>
    /// True when a payload carries raw pixels. A cheap format check only — safe to call on
    /// every drag-over, unlike <see cref="BitmapFrom"/>, which actually decodes.
    /// </summary>
    public static bool HasBitmap(IDataObject? data)
    {
        try
        {
            return data is not null
                   && (data.GetDataPresent(PngFormat) || data.GetDataPresent(DataFormats.Bitmap));
        }
        catch (Exception ex) when (IsClipboardFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Reads a picture's pixel size without decoding its pixels. (0, 0) if unreadable.</summary>
    public static (int Width, int Height) Measure(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Loads a picture for display, scaled down to <paramref name="maxWidth"/> and detached
    /// from the file so nothing stays locked. Null when the file is missing or not readable.
    /// </summary>
    public static BitmapSource? Load(string? path, int maxWidth = PreviewWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        var (width, _) = Measure(path);

        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;

            // OnLoad reads the whole picture up front, which is what releases the file.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

            // Only ever scale down — decoding a 32px icon at 640 would just blur it.
            if (maxWidth > 0 && width > maxWidth)
                image.DecodePixelWidth = maxWidth;

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Decodes picture bytes that are already in memory (a clipboard PNG).</summary>
    public static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls a bitmap out of a clipboard or drag payload — a screenshot, or an image copied
    /// from a browser or document. Null when the payload carries no picture.
    /// </summary>
    public static BitmapSource? BitmapFrom(IDataObject? data)
    {
        if (data is null)
            return null;

        try
        {
            if (data.GetDataPresent(PngFormat) && data.GetData(PngFormat) is MemoryStream png && png.Length > 0)
            {
                var decoded = Decode(png.ToArray());
                if (decoded is not null)
                    return decoded;
            }

            // The Bitmap flavour is the shell's DIB — what Snipping Tool, PrintScreen and
            // most drag sources hand over. Read it from the payload we were given: for a
            // drag that is not the clipboard, so going via Clipboard would grab the wrong
            // picture entirely.
            if (!data.GetDataPresent(DataFormats.Bitmap) || data.GetData(DataFormats.Bitmap) is not BitmapSource image)
                return null;

            if (image.CanFreeze)
                image.Freeze();

            return image;
        }
        catch (Exception ex) when (IsClipboardFailure(ex) || IsDecodeFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Picture files inside a clipboard or drag payload, in the order given.</summary>
    public static IReadOnlyList<string> PictureFiles(IDataObject? data)
    {
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
            return EmptyPaths;

        try
        {
            return data.GetData(DataFormats.FileDrop) is not string[] paths
                ? EmptyPaths
                : [.. paths.Where(p => IsPicture(p) && File.Exists(p))];
        }
        catch (Exception ex) when (IsClipboardFailure(ex))
        {
            return EmptyPaths;
        }
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    /// <summary>Puts a picture on the clipboard so it can be pasted into another app.</summary>
    public static bool CopyToClipboard(string? path)
    {
        var image = Load(path, maxWidth: 0);
        if (image is null)
            return false;

        try
        {
            Clipboard.SetImage(image);
            return true;
        }
        catch (Exception ex) when (IsClipboardFailure(ex))
        {
            // The clipboard is briefly lockable by any other app; a failed copy is not fatal.
            return false;
        }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowInFolder(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        Start("explorer.exe", $"/select,\"{path}\"");
    }

    /// <summary>Hands the picture to whatever app owns its file type.</summary>
    public static void OpenExternally(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>Copies a picture out of the media store to somewhere the user picked.</summary>
    public static void SaveCopy(string sourcePath, string targetPath) =>
        File.Copy(sourcePath, targetPath, overwrite: true);

    private static void Start(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static bool IsDecodeFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or FileFormatException or OverflowException or InvalidOperationException;

    private static bool IsClipboardFailure(Exception ex) =>
        ex is COMException or ExternalException or OutOfMemoryException or InvalidOperationException;
}
