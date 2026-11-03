using System.Windows;
using System.Windows.Media.Imaging;

namespace TaskManager.Services;

/// <summary>Stores pasted/dropped images in the attachments folder and resolves <c>attachment:</c> URLs.</summary>
public sealed class AttachmentService(AppPaths paths)
{
    public const string Scheme = "attachment:";

    static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>Returns the markdown url (<c>attachment:xxx.png</c>).</summary>
    public string SaveBitmap(BitmapSource bitmap)
    {
        var name = $"{Guid.NewGuid():N}.png";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var fs = File.Create(Path.Combine(paths.Attachments, name)))
            encoder.Save(fs);
        return Scheme + name;
    }

    public string SaveFile(string sourcePath)
    {
        var name = $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
        File.Copy(sourcePath, Path.Combine(paths.Attachments, name));
        return Scheme + name;
    }

    /// <summary>Clipboard → saved image url, or null if the clipboard holds no image.</summary>
    public string? SaveFromClipboard()
    {
        // Prefer the PNG stream (keeps transparency, set by browsers and most screenshot tools).
        if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream png)
        {
            var decoder = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return SaveBitmap(decoder.Frames[0]);
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            return SaveBitmap(img);
        if (Clipboard.ContainsFileDropList())
        {
            var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsImageFile);
            if (file is not null) return SaveFile(file);
        }
        return null;
    }

    /// <summary>Maps a markdown url to something BitmapImage can load; null if it can't be resolved.</summary>
    public Uri? Resolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            var file = Path.GetFileName(url[Scheme.Length..]);
            var full = Path.Combine(paths.Attachments, file);
            return File.Exists(full) ? new Uri(full) : null;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs)) return abs;
        return File.Exists(url) ? new Uri(Path.GetFullPath(url)) : null;
    }
}
