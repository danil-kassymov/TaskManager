#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property PublishTrimmed=false
// Renders the logo (same geometry as logo.svg) to PNGs and a multi-size .ico.
// Usage: dotnet run assets/make-icon.cs -- <output dir>
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = new List<byte[]>();
var t = new Thread(() =>
{
    foreach (var size in sizes) pngs.Add(Render(size));
    File.WriteAllBytes(Path.Combine(outDir, "logo-512.png"), Render(512));
});
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();

using (var ico = new BinaryWriter(File.Create(Path.Combine(outDir, "app.ico"))))
{
    ico.Write((short)0); ico.Write((short)1); ico.Write((short)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)0); ico.Write((byte)0);
        ico.Write((short)1); ico.Write((short)32);
        ico.Write(pngs[i].Length); ico.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var p in pngs) ico.Write(p);
}
Console.WriteLine($"wrote app.ico ({string.Join(",", sizes)}) and logo-512.png to {outDir}");

static byte[] Render(int size)
{
    var s = size / 256.0;
    var small = size <= 24; // thicker strokes and no padding so the glyph survives at 16px
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        var bg = new LinearGradientBrush(Color.FromRgb(0x74, 0x80, 0xEA), Color.FromRgb(0x45, 0x4F, 0xBF), 45);
        var pad = small ? 0 : 8 * s;
        dc.DrawRoundedRectangle(bg, null, new Rect(pad, pad, size - 2 * pad, size - 2 * pad), 56 * s, 56 * s);

        var c = new Point(128 * s, 128 * s);
        var r = 70 * s;
        var ring = (small ? 24 : 18) * s;
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), ring), c, r, r);
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - r), false, false);
            g.ArcTo(new Point(c.X - r, c.Y), new Size(r, r), 0, true, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, new Pen(Brushes.White, ring) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);

        var check = new StreamGeometry();
        using (var g = check.Open())
        {
            g.BeginFigure(new Point(96 * s, 130 * s), false, false);
            g.LineTo(new Point(119 * s, 153 * s), true, false);
            g.LineTo(new Point(162 * s, 106 * s), true, false);
        }
        dc.DrawGeometry(null, new Pen(Brushes.White, (small ? 26 : 20) * s)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        }, check);
    }
    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(dv);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}
