using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GameDeck.App.Tray;

/// <summary>
/// Draws the GameDeck logo for Windows (tray, windows, the .ico). Shapes and colors match
/// web/src/lib/logo.ts, on the same 64-unit grid.
/// </summary>
internal static class LogoRenderer
{
    private enum KeyStyle { Accent, Body, Frame }
    private sealed record KeySpec(float X, float Y, float Size, float Radius, float Turn, KeyStyle Style = KeyStyle.Accent);
    private sealed record ModelSpec(KeySpec Key, bool BigHole = false, bool SoftNotch = true, bool LitCore = false, bool NobleBody = false);
    private sealed record Pair(Color From, Color To);
    private sealed record FinishSpec(Pair BodyDark, Pair AccentDark, Pair BodyLight, Pair AccentLight);

    private static readonly KeySpec StandardKey = new(46, 3, 15, 4, 10);

    private static readonly Dictionary<string, ModelSpec> Models = new()
    {
        ["original"] = new(StandardKey, SoftNotch: false),
        ["fine"] = new(StandardKey),
        ["bigd"] = new(StandardKey, BigHole: true),
        ["bigkey"] = new(new KeySpec(45, 1.5f, 17.5f, 4.6f, 10)),
        ["straight"] = new(new KeySpec(46, 4, 15, 4, 0)),
        ["dynamic"] = new(new KeySpec(46.5f, 1.5f, 15, 4, 22)),
        ["outline"] = new(new KeySpec(47.5f, 4.5f, 12, 3, 10, KeyStyle.Frame)),
        ["litd"] = new(StandardKey, LitCore: true),
        ["core"] = new(StandardKey with { Style = KeyStyle.Body }, LitCore: true),
        ["noble"] = new(StandardKey with { Style = KeyStyle.Body }, NobleBody: true),
        ["mono"] = new(StandardKey with { Style = KeyStyle.Body }),
    };

    private static readonly Dictionary<string, FinishSpec> Finishes = new()
    {
        ["champagne"] = Finish("#ffffff", "#dfe2e7", "#f6e6bf", "#b98f50", "#24262c", "#08090b", "#c49a54", "#8c652c"),
        ["platinum"] = Finish("#ffffff", "#aeb5c0", "#ffffff", "#c4cad3", "#30333a", "#08090b", "#858c98", "#565c67"),
        ["cobalt"] = Finish("#ffffff", "#dfe4ec", "#8fc4ff", "#2f72e4", "#1c2638", "#080c14", "#2f7cf0", "#164fb4"),
        ["arctic"] = Finish("#ffffff", "#dce5ea", "#a4efff", "#1ea8d4", "#17272e", "#070d10", "#16a4cf", "#0b6f90"),
        ["emerald"] = Finish("#ffffff", "#dde6e2", "#8df0cd", "#129c74", "#16261f", "#070d0a", "#16a079", "#0b6c51"),
        ["steel"] = Finish("#ffffff", "#d7dee7", "#c5d5e6", "#6e8cad", "#1d2630", "#090c10", "#5f7fa2", "#3e5873"),
        ["bronze"] = Finish("#ffffff", "#e3e0dc", "#ecc39c", "#9c673b", "#29221d", "#0c0907", "#ad7443", "#774a26"),
        ["monochrome"] = Finish("#ffffff", "#ffffff", "#ffffff", "#ffffff", "#0b0c0f", "#0b0c0f", "#0b0c0f", "#0b0c0f"),
    };

    private static FinishSpec Finish(params string[] c) => new(
        new(Hex(c[0]), Hex(c[1])), new(Hex(c[2]), Hex(c[3])), new(Hex(c[4]), Hex(c[5])), new(Hex(c[6]), Hex(c[7])));
    private static Color Hex(string value) => ColorTranslator.FromHtml(value);

    public static bool Knows(string model, string finish) => Models.ContainsKey(model) && Finishes.ContainsKey(finish);

    /// <summary>The bare logo for a dark or light background, e.g. the taskbar notification area.</summary>
    public static Bitmap RenderMark(string model, string finish, int size, bool lightSurface) =>
        Render(size, g => DrawMark(g, Models[model], Finishes[finish], lightSurface));

    /// <summary>The logo on its dark tile, readable on any background: windows and the .exe.</summary>
    public static Bitmap RenderAppIcon(string model, string finish, int size) => Render(size, g =>
    {
        using var tile = Superellipse(30, 5);
        using var fill = Gradient(new PointF(32, 2), new PointF(32, 62), new Pair(Hex("#2a2d34"), Hex("#0d0e12")));
        using var edge = new Pen(Color.FromArgb(46, 255, 255, 255), 0.8f);
        g.FillPath(fill, tile);
        g.DrawPath(edge, tile);
        var state = g.Save();
        g.TranslateTransform(31, 33);
        g.ScaleTransform(0.62f, 0.62f);
        g.TranslateTransform(-32, -32);
        DrawMark(g, Models[model], Finishes[finish], lightSurface: false);
        g.Restore(state);
    });

    /// <summary>Creates an icon that owns its handle, so disposing it releases everything.</summary>
    public static Icon ToIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    // GDI+ regions have hard edges, so the logo is drawn large and scaled down smoothly.
    private static Bitmap Render(int size, Action<Graphics> draw)
    {
        var scale = size >= 128 ? 4 : 8;
        using var large = new Bitmap(size * scale, size * scale, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(large))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(size * scale / 64f, size * scale / 64f);
            draw(g);
        }
        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        using (var attributes = new ImageAttributes())
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(large, new Rectangle(0, 0, size, size), 0, 0, large.Width, large.Height, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }

    private static void DrawMark(Graphics g, ModelSpec model, FinishSpec finish, bool lightSurface)
    {
        var bodyColors = lightSurface ? finish.BodyLight : finish.BodyDark;
        var accentColors = lightSurface ? finish.AccentLight : finish.AccentDark;
        using var bodyBrush = Gradient(new PointF(10, 4), new PointF(54, 60), bodyColors);
        using var accentBrush = Gradient(new PointF(44, 0), new PointF(62, 20), accentColors);

        using var outer = Superellipse(28, 4.5);
        using var hole = model.BigHole ? LetterD(14, 26, 13, 3) : LetterD(16, 27, 11, 2);
        using var notch = model.SoftNotch ? RoundedRect(42, -24, 46, 50, 6) : RoundedRect(42, -10, 32, 36, 0);
        using var body = new Region(outer);
        body.Exclude(hole);
        body.Exclude(notch);
        g.FillRegion(model.NobleBody ? accentBrush : bodyBrush, body);

        if (model.LitCore)
        {
            using var core = LetterD(16, 27, 11, 2);
            using var shrink = ScaleAround(0.66f, 27, 32);
            core.Transform(shrink);
            g.FillPath(accentBrush, core);
        }

        var key = model.Key;
        using var keyPath = RoundedRect(key.X, key.Y, key.Size, key.Size, key.Radius);
        using var turn = new Matrix();
        turn.RotateAt(key.Turn, new PointF(key.X + key.Size / 2, key.Y + key.Size / 2));
        keyPath.Transform(turn);
        if (key.Style == KeyStyle.Frame)
        {
            using var pen = new Pen(accentBrush, 3);
            g.DrawPath(pen, keyPath);
        }
        else
        {
            g.FillPath(key.Style == KeyStyle.Body ? bodyBrush : accentBrush, keyPath);
        }
    }

    private static Matrix ScaleAround(float scale, float x, float y)
    {
        var m = new Matrix();
        m.Translate(x, y);
        m.Scale(scale, scale);
        m.Translate(-x, -y);
        return m;
    }

    // A GDI+ gradient repeats outside its two points; the outer stops hold the end colors instead.
    private static LinearGradientBrush Gradient(PointF from, PointF to, Pair colors)
    {
        const float reach = 10;
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var start = new PointF(from.X - dx * reach, from.Y - dy * reach);
        var end = new PointF(to.X + dx * reach, to.Y + dy * reach);
        var total = 2 * reach + 1;
        return new LinearGradientBrush(start, end, colors.From, colors.To)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [colors.From, colors.From, colors.To, colors.To],
                Positions = [0, reach / total, (reach + 1) / total, 1],
            },
        };
    }

    private static GraphicsPath Superellipse(float half, double n)
    {
        var points = new PointF[180];
        var exponent = 2 / n;
        for (var i = 0; i < points.Length; i++)
        {
            var a = i / (double)points.Length * Math.PI * 2;
            var c = Math.Cos(a);
            var s = Math.Sin(a);
            points[i] = new PointF(
                (float)(32 + half * Math.Sign(c) * Math.Pow(Math.Abs(c), exponent)),
                (float)(32 + half * Math.Sign(s) * Math.Pow(Math.Abs(s), exponent)));
        }
        var path = new GraphicsPath();
        path.AddPolygon(points);
        return path;
    }

    private static GraphicsPath LetterD(float left, float flatEnd, float radius, float corner)
    {
        var top = 32 - radius;
        var bottom = 32 + radius;
        var path = new GraphicsPath();
        path.AddArc(left, top, corner * 2, corner * 2, 180, 90);
        path.AddLine(left + corner, top, flatEnd, top);
        path.AddArc(flatEnd - radius, top, radius * 2, radius * 2, -90, 180);
        path.AddLine(flatEnd, bottom, left + corner, bottom);
        path.AddArc(left, bottom - corner * 2, corner * 2, corner * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        if (r <= 0)
        {
            path.AddRectangle(new RectangleF(x, y, w, h));
            return path;
        }
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
