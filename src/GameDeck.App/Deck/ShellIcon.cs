using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GameDeck.App.Deck;

/// <summary>
/// The icon Windows shows for a program, shortcut, file or folder, as a PNG with transparency. Uses
/// the shell's own image factory, so a shortcut gets its target's icon and large sizes are sharp.
/// Icons only: no thumbnail handlers run, and the file itself is never opened or executed.
/// </summary>
internal static class ShellIcon
{
    /// <returns>PNG bytes, or null when Windows has no icon for it.</returns>
    public static Task<byte[]?> ExtractPngAsync(string path, int size = 256)
    {
        // The shell's COM objects want a single-threaded apartment; a short-lived thread provides one.
        var result = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Extract(path, size)); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "ShellIcon" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static byte[]? Extract(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null) return null;
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SiigbfIconOnly | SiigbfBiggerSizeOk, out var hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            try
            {
                using var bitmap = ToBitmap(hbitmap);
                if (bitmap is null) return null;
                using var png = new MemoryStream();
                bitmap.Save(png, ImageFormat.Png);
                return png.ToArray();
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies the shell's 32-bit DIB keeping its alpha channel (Image.FromHbitmap would drop it and
    /// leave a black square around the icon).
    /// </summary>
    private static Bitmap? ToBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
        if (bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero) return Image.FromHbitmap(hbitmap);

        var width = bm.bmWidth;
        var height = Math.Abs(bm.bmHeight);
        var stride = bm.bmWidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(bm.bmBits, pixels, 0, pixels.Length);

        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = result.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            // A DIB section is stored bottom-up: its first row in memory is the bottom of the image.
            for (var y = 0; y < height; y++)
                Marshal.Copy(pixels, (height - 1 - y) * stride, data.Scan0 + y * data.Stride, Math.Min(stride, data.Stride));
        }
        finally
        {
            result.UnlockBits(data);
        }
        return result;
    }

    private const int SiigbfBiggerSizeOk = 0x1;
    private const int SiigbfIconOnly = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
