using ShareBridge.Core.Net;

namespace ShareBridge.Core.Persistence;

/// <summary>
/// Owns the folder behind picture messages (<c>%APPDATA%\ShareBridge\media</c>), one
/// sub-folder per peer. Both ends keep a copy here: the sender so a bubble survives the
/// original being moved or deleted, the receiver so pasted screenshots do not pile up in
/// the downloads folder. Names are claimed under a lock, so two pictures arriving at once
/// can never land on the same file.
/// </summary>
public sealed class MediaStore
{
    /// <summary>Longest file name stem kept from a peer-supplied name, before the extension.</summary>
    private const int MaxStemLength = 60;

    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff",
    };

    private readonly object _gate = new();

    public MediaStore(string folder) => Folder = folder;

    public string Folder { get; }

    /// <summary>True for names this app is willing to treat as a picture rather than a file.</summary>
    public static bool IsPictureName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && PictureExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>
    /// Claims a free path for a picture that is about to be written, creating the file so
    /// the name cannot be handed out twice. The caller is expected to fill or delete it.
    /// </summary>
    public string Reserve(string peerLabel, string? fileName)
    {
        var folder = Path.Combine(Folder, SafePath.SanitizeFolderName(peerLabel));

        lock (_gate)
        {
            Directory.CreateDirectory(folder);
            var path = SafePath.MakeUnique(Path.Combine(folder, SafeLeaf(fileName)));
            using (File.Create(path))
            {
            }

            return path;
        }
    }

    /// <summary>Copies an existing picture into the store and returns its new path.</summary>
    public string Import(string sourcePath, string peerLabel)
    {
        var target = Reserve(peerLabel, Path.GetFileName(sourcePath));
        try
        {
            File.Copy(sourcePath, target, overwrite: true);
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>Writes already-encoded picture bytes (a pasted screenshot) into the store.</summary>
    public string Write(string peerLabel, string fileName, byte[] bytes)
    {
        var target = Reserve(peerLabel, fileName);
        try
        {
            File.WriteAllBytes(target, bytes);
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// Drops the pictures kept for one peer. Both directions of a conversation live in the
    /// same folder, so clearing that conversation clears exactly this. Outgoing pictures are
    /// copies, so the user's own originals are untouched.
    /// </summary>
    public void ClearPeer(string peerLabel)
    {
        lock (_gate)
            TryDeleteFolder(Path.Combine(Folder, SafePath.SanitizeFolderName(peerLabel)));
    }

    /// <summary>Drops every stored picture, alongside wiping all history.</summary>
    public void ClearAll()
    {
        lock (_gate)
            TryDeleteFolder(Folder);
    }

    /// <summary>Reduces a name that may have come off the wire to a safe, bounded leaf.</summary>
    private static string SafeLeaf(string? fileName)
    {
        var leaf = Path.GetFileName(SafePath.SanitizeRelative(fileName) ?? string.Empty);
        if (!IsPictureName(leaf))
            return "image.png";

        var extension = Path.GetExtension(leaf);
        var stem = Path.GetFileNameWithoutExtension(leaf);
        if (stem.Length > MaxStemLength)
            stem = stem[..MaxStemLength];

        return stem.Length == 0 ? "image" + extension : stem + extension;
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

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A picture held open elsewhere is not worth failing the clear over.
        }
    }
}
