using System.Drawing.Drawing2D;

namespace GameDeck.App.Tray;

internal static class Theme
{
    // Obsidian tokens, mirrored from web/src/styles/tokens.css.
    public static readonly Color Background = Color.FromArgb(9, 10, 12);
    public static readonly Color Surface = Color.FromArgb(20, 22, 26);
    public static readonly Color Border = Color.FromArgb(44, 47, 53);
    public static readonly Color Text = Color.FromArgb(244, 245, 247);
    public static readonly Color Muted = Color.FromArgb(164, 169, 179);
    public static readonly Color Accent = Color.FromArgb(169, 214, 255);
    public static readonly Color AccentInk = Color.FromArgb(8, 19, 29);

    /// <summary>The chosen logo on its tile, for window title bars.</summary>
    public static Icon CreateWindowIcon(GameDeckService service)
    {
        var (model, finish) = service.Logo;
        using var bitmap = LogoRenderer.RenderAppIcon(model, finish, 32);
        return LogoRenderer.ToIcon(bitmap);
    }

    /// <summary>The chosen logo for the notification area, matched to the taskbar's light or dark theme.</summary>
    public static Icon CreateTrayIcon(GameDeckService service)
    {
        var (model, finish) = service.Logo;
        using var bitmap = LogoRenderer.RenderMark(model, finish, SystemInformation.SmallIconSize.Width, TaskbarIsLight());
        return LogoRenderer.ToIcon(bitmap);
    }

    private static bool TaskbarIsLight()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int and not 0;
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Button MakeButton(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Surface,
            ForeColor = primary ? AccentInk : Text,
            Font = new Font("Segoe UI Semibold", 9.5f),
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Border;
        return b;
    }
}
