// Writes the icons that ship with the app from the default logo:
//   src/GameDeck.App/GameDeck.ico (Explorer, Start menu, taskbar, installer)
//   web/public/apple-touch-icon.png (phone home screens)
//   extension/icons/icon*.png (browser toolbar)
//   installer/art/*.png (the setup wizard background, side image and corner mark)
// Rerun with `dotnet run --project tools/GameDeck.IconGen` after changing the default logo.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using GameDeck.App.Tray;
using GameDeck.Core.Config;

var root = new DirectoryInfo(Directory.GetCurrentDirectory());
while (root is not null && !File.Exists(Path.Combine(root.FullName, "GameDeck.sln"))) root = root.Parent;
if (root is null) throw new InvalidOperationException("Run this inside the GameDeck repository.");

const string model = LogoCatalog.DefaultModel, finish = LogoCatalog.DefaultFinish;
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

var images = sizes.Select(size =>
{
    using var bitmap = LogoRenderer.RenderAppIcon(model, finish, size);
    return IconEntry(bitmap);
}).ToList();

var icoPath = Path.Combine(root.FullName, "src", "GameDeck.App", "GameDeck.ico");
using (var file = File.Create(icoPath))
using (var w = new BinaryWriter(file))
{
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] % 256));
        w.Write((byte)(sizes[i] % 256));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write((uint)images[i].Length);
        w.Write((uint)offset);
        offset += images[i].Length;
    }
    images.ForEach(w.Write);
}
Console.WriteLine($"Wrote {icoPath}");

// iOS rounds the corners itself and shows transparency as black, so this one is opaque.
const int touchSize = 180;
using (var touch = new Bitmap(touchSize, touchSize, PixelFormat.Format32bppArgb))
using (var mark = LogoRenderer.RenderMark(model, finish, 112, lightSurface: false))
using (var g = Graphics.FromImage(touch))
{
    g.Clear(ColorTranslator.FromHtml("#0d0e12"));
    g.DrawImage(mark, (touchSize - mark.Width) / 2 - 2, (touchSize - mark.Height) / 2 + 2, mark.Width, mark.Height);
    var touchPath = Path.Combine(root.FullName, "web", "public", "apple-touch-icon.png");
    touch.Save(touchPath, ImageFormat.Png);
    Console.WriteLine($"Wrote {touchPath}");
}

// The browser extension's toolbar and store-style icons.
var extensionIcons = Directory.CreateDirectory(Path.Combine(root.FullName, "extension", "icons")).FullName;
foreach (var size in new[] { 16, 32, 48, 128 })
{
    using var icon = LogoRenderer.RenderAppIcon(model, finish, size);
    icon.Save(Path.Combine(extensionIcons, $"icon{size}.png"), ImageFormat.Png);
}
Console.WriteLine($"Wrote {extensionIcons}");

// The setup wizard: a dark background lit in cobalt, the logo and name beside the welcome text, and
// the mark in the corner of the other pages. Sizes follow the image areas Inno Setup 7 documents
// for 100 %, 125 %, 150 % and 200 % display scaling; Setup picks the closest one.
var art = Directory.CreateDirectory(Path.Combine(root.FullName, "installer", "art")).FullName;
foreach (var (w, h) in new[] { (596, 432), (796, 576), (994, 720), (1272, 922) })
{
    using var back = InstallerBackground(w, h);
    back.Save(Path.Combine(art, $"back-{w}.png"), ImageFormat.Png);
}
foreach (var (w, h) in new[] { (202, 386), (269, 515), (336, 643), (430, 824) })
{
    using var side = InstallerSide(model, finish, w, h);
    side.Save(Path.Combine(art, $"side-{w}.png"), ImageFormat.Png);
}
foreach (var size in new[] { 58, 77, 97, 124 })
{
    using var corner = Square(LogoRenderer.RenderMark(model, finish, (int)(size * 0.86), lightSurface: false), size);
    corner.Save(Path.Combine(art, $"small-{size}.png"), ImageFormat.Png);
}
Console.WriteLine($"Wrote {art}");

static Bitmap InstallerBackground(int w, int h)
{
    var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    using (var shade = new LinearGradientBrush(new Rectangle(0, 0, w, h), ColorTranslator.FromHtml("#0c1119"), ColorTranslator.FromHtml("#05060a"), 90f))
        g.FillRectangle(shade, 0, 0, w, h);
    Glow(g, w * 0.95f, h * 0.02f, w * 0.72f, Color.FromArgb(62, 47, 114, 228));
    Glow(g, w * 0.02f, h * 1.02f, w * 0.55f, Color.FromArgb(38, 94, 162, 255));
    // A faint dot grid, fading towards the bottom so the buttons stay calm.
    var step = w / 34f;
    for (var y = step / 2; y < h; y += step)
    {
        var alpha = (int)(14 * (1 - y / h));
        if (alpha <= 0) continue;
        using var dot = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        for (var x = step / 2; x < w; x += step) g.FillEllipse(dot, x, y, step * 0.09f, step * 0.09f);
    }
    return bitmap;
}

static Bitmap InstallerSide(string model, string finish, int w, int h)
{
    var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    var markSize = (int)(w * 0.6);
    var centerY = h * 0.36f;
    Glow(g, w / 2f, centerY, w * 0.62f, Color.FromArgb(92, 47, 114, 228));
    using (var mark = LogoRenderer.RenderMark(model, finish, markSize, lightSurface: false))
        g.DrawImage(mark, (w - mark.Width) / 2f, centerY - mark.Height / 2f, mark.Width, mark.Height);

    var fontSize = w * 0.135f;
    using var bold = new Font(FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
    using var light = new Font(bold.FontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
    using var white = new SolidBrush(Color.FromArgb(245, 247, 250));
    using var grey = new SolidBrush(Color.FromArgb(150, 162, 180));
    var format = StringFormat.GenericTypographic;
    var game = g.MeasureString("Game", bold, int.MaxValue, format);
    var deck = g.MeasureString("Deck", light, int.MaxValue, format);
    var textY = centerY + markSize * 0.62f;
    var left = (w - game.Width - deck.Width) / 2f;
    g.DrawString("Game", bold, white, left, textY, format);
    g.DrawString("Deck", light, grey, left + game.Width, textY, format);

    using var small = new Font(bold.FontFamily, w * 0.047f, FontStyle.Bold, GraphicsUnit.Pixel);
    using var accent = new SolidBrush(Color.FromArgb(130, 94, 162, 255));
    const string tagline = "SOUNDBOARD  ·  SMART DECK";
    var tag = g.MeasureString(tagline, small, int.MaxValue, format);
    g.DrawString(tagline, small, accent, (w - tag.Width) / 2f, textY + game.Height * 1.25f, format);
    return bitmap;
}

static void Glow(Graphics g, float cx, float cy, float radius, Color color)
{
    using var path = new GraphicsPath();
    path.AddEllipse(cx - radius, cy - radius, radius * 2, radius * 2);
    using var brush = new PathGradientBrush(path) { CenterColor = color, SurroundColors = [Color.FromArgb(0, color)] };
    g.FillPath(brush, path);
}

static Bitmap Square(Bitmap mark, int size)
{
    using (mark)
    {
        var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var scale = Math.Min((float)size / mark.Width, (float)size / mark.Height) * 0.94f;
        var (w, h) = (mark.Width * scale, mark.Height * scale);
        g.DrawImage(mark, (size - w) / 2, (size - h) / 2, w, h);
        return canvas;
    }
}

// 256 px as PNG; smaller sizes as 32-bit DIBs, which every Windows API can read.
static byte[] IconEntry(Bitmap bitmap)
{
    var size = bitmap.Width;
    using var stream = new MemoryStream();
    if (size >= 256)
    {
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
    using var w = new BinaryWriter(stream);
    var maskStride = (int)Math.Ceiling(size / 32d) * 4;
    w.Write(40u);
    w.Write(size);
    w.Write(size * 2); // color rows plus the AND mask rows
    w.Write((ushort)1);
    w.Write((ushort)32);
    w.Write(0u);
    w.Write((uint)(size * size * 4 + maskStride * size));
    w.Write(0);
    w.Write(0);
    w.Write(0u);
    w.Write(0u);
    for (var y = size - 1; y >= 0; y--)
    {
        for (var x = 0; x < size; x++)
        {
            var c = bitmap.GetPixel(x, y);
            w.Write(c.B);
            w.Write(c.G);
            w.Write(c.R);
            w.Write(c.A);
        }
    }
    w.Write(new byte[maskStride * size]); // the alpha channel carries transparency
    w.Flush();
    return stream.ToArray();
}
