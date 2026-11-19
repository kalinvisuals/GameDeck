using System.Buffers.Binary;

namespace GameDeck.Core.Deck;

/// <param name="Extension">"png", "jpg", "gif" or "webp".</param>
/// <param name="Frames">Frames in an animation; 1 for a still image.</param>
public sealed record ImageInfo(string Extension, int Width, int Height, bool Animated, int Frames);

/// <summary>
/// Recognises a button image by its content and checks that its container is complete and consistent:
/// every chunk, segment or block fits inside the file and the file ends where the format says it does.
/// A truncated, padded or disguised file is refused whatever its name. Pixel data itself is decoded only
/// by the browser, never by GameDeck. SVG is not accepted here: the dashboard turns it into a PNG first.
/// </summary>
public static class ImageCheck
{
    public const int MaxSide = 4096;

    public static ImageInfo? Inspect(ReadOnlySpan<byte> d)
    {
        try
        {
            var info = d.Length < 12 ? null
                : d.StartsWith(Png) ? InspectPng(d)
                : d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF ? InspectJpeg(d)
                : d.StartsWith("GIF87a"u8) || d.StartsWith("GIF89a"u8) ? InspectGif(d)
                : d.StartsWith("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8) ? InspectWebp(d)
                : null;
            return info is { Width: > 0 and <= MaxSide, Height: > 0 and <= MaxSide } ? info : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // a length field pointed outside the file
        }
    }

    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Chunks from IHDR to IEND, each inside the file; acTL marks an animated PNG.</summary>
    private static ImageInfo? InspectPng(ReadOnlySpan<byte> d)
    {
        var pos = 8;
        int width = 0, height = 0, frames = 1;
        var animated = false;
        var first = true;
        while (pos + 12 <= d.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(d[pos..]);
            var type = d.Slice(pos + 4, 4);
            if (length > int.MaxValue || pos + 12 + (long)length > d.Length) return null;
            var data = d.Slice(pos + 8, (int)length);
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) return null;
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                first = false;
            }
            else if (type.SequenceEqual("acTL"u8) && length == 8)
            {
                frames = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data), int.MaxValue);
                animated = frames > 1;
            }
            pos += 12 + (int)length;
            if (type.SequenceEqual("IEND"u8)) return pos == d.Length ? new ImageInfo("png", width, height, animated, frames) : null;
        }
        return null;
    }

    /// <summary>Segments up to the frame header (size), and the end-of-image marker at the end.</summary>
    private static ImageInfo? InspectJpeg(ReadOnlySpan<byte> d)
    {
        // Some cameras pad after the end marker; accept a little of that, nothing more.
        var tail = d[Math.Max(0, d.Length - 64)..];
        var end = tail.LastIndexOf((ReadOnlySpan<byte>)[0xFF, 0xD9]);
        if (end < 0 || tail[(end + 2)..].IndexOfAnyExcept((byte)0) >= 0) return null;

        var pos = 2;
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF) return null;
            var marker = d[pos + 1];
            if (marker == 0xFF) { pos++; continue; }                        // fill byte
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { pos += 2; continue; } // no length
            if (marker is 0xD9 or 0xDA) return null;                        // image data before any frame header
            var length = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 2)..]);
            if (length < 2 || pos + 2 + length > d.Length) return null;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 7) return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 5)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 7)..]);
                return new ImageInfo("jpg", width, height, false, 1);
            }
            pos += 2 + length;
        }
        return null;
    }

    /// <summary>Header, colour table, then extension and image blocks each made of sub-blocks, ending with the trailer.</summary>
    private static ImageInfo? InspectGif(ReadOnlySpan<byte> d)
    {
        var width = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]);
        var flags = d[10];
        var pos = 13 + ((flags & 0x80) != 0 ? 3 << ((flags & 7) + 1) : 0);
        var frames = 0;
        while (pos < d.Length)
        {
            switch (d[pos])
            {
                case 0x3B: // trailer
                    return frames > 0 && pos + 1 == d.Length ? new ImageInfo("gif", width, height, frames > 1, frames) : null;
                case 0x21: // extension: label, then sub-blocks
                    pos = SkipSubBlocks(d, pos + 2);
                    break;
                case 0x2C: // image descriptor (9 bytes), local colour table, LZW code size, then sub-blocks
                    if (pos + 10 > d.Length) return null;
                    var local = d[pos + 9];
                    pos += 10 + ((local & 0x80) != 0 ? 3 << ((local & 7) + 1) : 0);
                    if (pos >= d.Length || d[pos] is < 2 or > 11) return null;
                    pos = SkipSubBlocks(d, pos + 1);
                    frames++;
                    break;
                default:
                    return null;
            }
            if (pos < 0) return null;
        }
        return null; // no trailer: truncated
    }

    /// <returns>The position after the block terminator, or -1 when a sub-block runs past the end.</returns>
    private static int SkipSubBlocks(ReadOnlySpan<byte> d, int pos)
    {
        while (pos < d.Length)
        {
            var size = d[pos];
            if (size == 0) return pos + 1;
            pos += 1 + size;
        }
        return -1;
    }

    /// <summary>RIFF container whose size matches the file, made of chunks that each fit, with a known first chunk.</summary>
    private static ImageInfo? InspectWebp(ReadOnlySpan<byte> d)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(d[4..]) + 8L != d.Length) return null;
        var pos = 12;
        int width = 0, height = 0, frames = 0;
        var animated = false;
        var first = true;
        while (pos < d.Length)
        {
            if (pos + 8 > d.Length) return null;
            var type = d.Slice(pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 4)..]);
            if (pos + 8L + size > d.Length) return null;
            var data = d.Slice(pos + 8, (int)size);
            if (first)
            {
                if (type.SequenceEqual("VP8X"u8) && size >= 10)
                {
                    animated = (data[0] & 0x02) != 0;
                    width = 1 + (data[4] | data[5] << 8 | data[6] << 16);
                    height = 1 + (data[7] | data[8] << 8 | data[9] << 16);
                }
                else if (type.SequenceEqual("VP8 "u8) && size >= 10 && data[3] == 0x9D && data[4] == 0x01 && data[5] == 0x2A)
                {
                    width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3FFF;
                    height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3FFF;
                }
                else if (type.SequenceEqual("VP8L"u8) && size >= 5 && data[0] == 0x2F)
                {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
                    width = (int)(bits & 0x3FFF) + 1;
                    height = (int)((bits >> 14) & 0x3FFF) + 1;
                }
                else return null;
                first = false;
            }
            if (type.SequenceEqual("ANMF"u8))
            {
                if (!IsWebpFrame(data)) return null;
                frames++;
            }
            pos += 8 + (int)size + (int)(size & 1);
        }
        if (pos != d.Length || first || (animated && frames == 0)) return null;
        return new ImageInfo("webp", width, height, animated && frames > 1, Math.Max(frames, 1));
    }

    /// <summary>
    /// One animation frame: a 16-byte header, then an optional ALPH chunk and exactly one VP8 or VP8L
    /// chunk (unknown chunks may follow). Container-level chunks such as ICCP or VP8X inside a frame make
    /// browsers reject the whole file, so they are refused here too.
    /// </summary>
    private static bool IsWebpFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 16 + 8) return false;
        var pos = 16;
        var image = 0;
        while (pos < frame.Length)
        {
            if (pos + 8 > frame.Length) return false;
            var type = frame.Slice(pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(frame[(pos + 4)..]);
            if (pos + 8L + size > frame.Length) return false;
            if (type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8)) image++;
            else if (type.SequenceEqual("VP8X"u8) || type.SequenceEqual("ICCP"u8) || type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8) || type.SequenceEqual("EXIF"u8) || type.SequenceEqual("XMP "u8))
                return false;
            pos += 8 + (int)size + (int)(size & 1);
        }
        return image == 1 && pos == frame.Length;
    }
}
