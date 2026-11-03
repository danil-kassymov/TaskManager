using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskManager.Markdown;
using TaskManager.Services;

namespace TaskManager.Tests;

public sealed class AttachmentTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"tm-att-{Guid.NewGuid():N}");
    readonly AttachmentService _service;

    public AttachmentTests() => _service = new AttachmentService(new AppPaths(_root));

    public void Dispose() => Directory.Delete(_root, true);

    static BitmapSource Pixel()
    {
        var bmp = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        bmp.Freeze();
        return bmp;
    }

    static void OnSta(Action a)
    {
        Exception? error = null;
        var t = new Thread(() => { try { a(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    [Fact]
    public void SavedBitmap_ResolvesToExistingFile()
    {
        var url = _service.SaveBitmap(Pixel());
        Assert.StartsWith("attachment:", url);
        var uri = _service.Resolve(url);
        Assert.NotNull(uri);
        Assert.True(File.Exists(uri!.LocalPath));
    }

    [Fact]
    public void Resolve_RejectsPathTraversalAndMissingFiles()
    {
        Assert.Null(_service.Resolve("attachment:../../secret.png"));
        Assert.Null(_service.Resolve("attachment:missing.png"));
        Assert.Equal("https://example.com/a.png", _service.Resolve("https://example.com/a.png")!.ToString());
    }

    [Fact]
    public void Clipboard_ImageIsSaved()
    {
        string? url = null;
        OnSta(() =>
        {
            Clipboard.SetImage(Pixel());
            url = _service.SaveFromClipboard();
        });
        Assert.NotNull(url);
        Assert.NotNull(_service.Resolve(url));
    }

    [Theory]
    [InlineData("https://github.com/x", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("just text", false)]
    [InlineData("C:\\file.txt", false)]
    public void IsUrl(string text, bool expected) => Assert.Equal(expected, MarkdownEditing.IsUrl(text));
}
