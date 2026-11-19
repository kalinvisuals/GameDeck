namespace GameDeck.Tests;

internal static class TestAudio
{
    /// <summary>A 16-bit PCM WAV. amplitude 0 gives digital silence (used where real devices are opened).</summary>
    public static byte[] Wav(double seconds, int sampleRate = 44100, short channels = 1, double amplitude = 0.0, double freq = 440)
    {
        var frames = (int)(seconds * sampleRate);
        var dataBytes = frames * channels * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        for (var i = 0; i < frames; i++)
        {
            var v = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * freq * i / sampleRate));
            for (var c = 0; c < channels; c++) w.Write(v);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gamedeck-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
