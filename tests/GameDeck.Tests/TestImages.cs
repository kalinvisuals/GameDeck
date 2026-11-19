using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace GameDeck.Tests;

/// <summary>Small, valid images of every accepted kind, built in memory for the tests.</summary>
internal static class TestImages
{
    public static byte[] Png(int size = 16, Color? color = null) => Encode(size, color ?? Color.SteelBlue, ImageFormat.Png);

    public static byte[] Jpeg(int size = 16) => Encode(size, Color.Coral, ImageFormat.Jpeg);

    private static byte[] Encode(int size, Color color, ImageFormat format)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp)) g.Clear(color);
        using var ms = new MemoryStream();
        bmp.Save(ms, format);
        return ms.ToArray();
    }

    /// <summary>
    /// An animated GIF, 1×1 px, whose frames alternate between two colours every <paramref name="delayCs"/>
    /// hundredths of a second, looping forever. Browsers play it, so tests can see it change.
    /// </summary>
    public static byte[] AnimatedGif(int frames = 2, int delayCs = 10, (byte R, byte G, byte B)? a = null, (byte R, byte G, byte B)? b = null)
    {
        var (c0, c1) = (a ?? (255, 0, 0), b ?? (0, 0, 255));
        var g = new List<byte>();
        g.AddRange("GIF89a"u8.ToArray());
        g.AddRange([1, 0, 1, 0, 0x80, 0, 0]);                         // 1×1, global colour table of 2 entries
        g.AddRange([c0.R, c0.G, c0.B, c1.R, c1.G, c1.B]);
        g.AddRange([0x21, 0xFF, 11, .. "NETSCAPE2.0"u8.ToArray(), 3, 1, 0, 0, 0]); // loop forever
        for (var i = 0; i < frames; i++)
        {
            g.AddRange([0x21, 0xF9, 4, 0, (byte)delayCs, 0, 0, 0]);     // graphic control: delay
            g.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0]);             // image descriptor 1×1
            // LZW, minimum code size 2: clear, colour index, end. Index 0 = 0x44 0x01, index 1 = 0x4C 0x01.
            g.AddRange([2, 2, (byte)(i % 2 == 0 ? 0x44 : 0x4C), 0x01, 0]);
        }
        g.Add(0x3B);
        return g.ToArray();
    }

    /// <summary>An animated PNG (APNG): a valid PNG with an acTL chunk announcing two frames.</summary>
    public static byte[] AnimatedPng()
    {
        var png = Png(4);
        // Insert acTL (2 frames, loop forever) right after IHDR: 8-byte signature + 25-byte IHDR chunk.
        var acTL = Chunk("acTL", [0, 0, 0, 2, 0, 0, 0, 0]);
        return [.. png[..33], .. acTL, .. png[33..]];
    }

    /// <summary>A RIFF/WebP container: still (one VP8L chunk) or animated (VP8X + ANIM + ANMF frames).</summary>
    public static byte[] Webp(bool animated = false, int frames = 2)
    {
        // Lossless 1×1 bitstream header: signature 0x2F, then 14-bit width-1, 14-bit height-1 (both 0).
        byte[] vp8l = [0x2F, 0, 0, 0, 0, 0];
        var chunks = new List<byte>();
        if (!animated)
        {
            chunks.AddRange(RiffChunk("VP8L", vp8l));
        }
        else
        {
            chunks.AddRange(RiffChunk("VP8X", [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0])); // animation flag, 1×1 canvas
            chunks.AddRange(RiffChunk("ANIM", [0, 0, 0, 0, 0, 0]));
            for (var i = 0; i < frames; i++)
                chunks.AddRange(RiffChunk("ANMF", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100, 0, 0, 0, .. RiffChunk("VP8L", vp8l)]));
        }
        var body = new List<byte>(Encoding.ASCII.GetBytes("WEBP"));
        body.AddRange(chunks);
        return [.. "RIFF"u8.ToArray(), .. LittleEndian((uint)body.Count), .. body];
    }

    private static byte[] RiffChunk(string type, byte[] data)
    {
        var padded = data.Length % 2 == 1 ? [.. data, 0] : data;
        return [.. Encoding.ASCII.GetBytes(type), .. LittleEndian((uint)data.Length), .. padded];
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var crc = Crc32([.. typeBytes, .. data]);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        return [.. length, .. typeBytes, .. data, .. crcBytes];
    }

    private static byte[] LittleEndian(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var x in data)
        {
            crc ^= x;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
