using GameDeck.Core.Localization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GameDeck.Core.Audio;

/// <summary>A sound fully decoded to the engine mix format (48 kHz stereo float) for instant triggering.</summary>
public sealed class CachedSound
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);

    public float[] Samples { get; }
    public TimeSpan Duration => ToTime(Samples.Length);

    /// <summary>Shortest part of a sound that can be selected to play.</summary>
    public const long MinTrimMs = 50;

    internal static TimeSpan ToTime(int samples) => TimeSpan.FromSeconds(samples / (double)(AudioEngine.SampleRate * AudioEngine.Channels));

    /// <summary>
    /// Sample range [start, end) of the part to play, frame aligned. endMs 0 means the end of the file;
    /// a selection that is out of range or shorter than <see cref="MinTrimMs"/> plays the whole sound.
    /// </summary>
    public (int Start, int End) Range(long startMs, long endMs)
    {
        static int ToSamples(long ms) => (int)Math.Min(int.MaxValue / 4, Math.Max(0, ms) * AudioEngine.SampleRate / 1000) * AudioEngine.Channels;
        var start = ToSamples(startMs);
        var end = endMs > 0 ? Math.Min(ToSamples(endMs), Samples.Length) : Samples.Length;
        return end - start >= ToSamples(MinTrimMs) ? (start, end) : (0, Samples.Length);
    }

    internal CachedSound(float[] samples) => Samples = samples;

    public static CachedSound Decode(string path)
    {
        using var reader = OpenReader(path);
        if (reader.TotalTime > MaxDuration)
            throw new InvalidDataException(L.T("sound.tooLong", MaxDuration.TotalMinutes));

        ISampleProvider source = reader.ToSampleProvider();
        source = source.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(source),
            2 => source,
            _ => new FirstTwoChannelsSampleProvider(source),
        };
        if (source.WaveFormat.SampleRate != AudioEngine.SampleRate)
            source = new WdlResamplingSampleProvider(source, AudioEngine.SampleRate);

        var estimate = (int)Math.Min(int.MaxValue / 2,
            reader.TotalTime.TotalSeconds * AudioEngine.SampleRate * AudioEngine.Channels + 4096);
        var data = new float[Math.Max(estimate, 4096)];
        var length = 0;
        var chunk = new float[AudioEngine.SampleRate * AudioEngine.Channels / 10];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (length + read > data.Length) Array.Resize(ref data, Math.Max(data.Length * 2, length + read));
            Array.Copy(chunk, 0, data, length, read);
            length += read;
        }
        if (length == 0) throw new InvalidDataException(L.T("sound.empty"));
        Array.Resize(ref data, length - length % AudioEngine.Channels);
        return new CachedSound(data);
    }

    /// <summary>WAV via the managed reader, everything else (MP3) via Media Foundation.</summary>
    private static WaveStream OpenReader(string path)
    {
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var wav = new WaveFileReader(path);
            if (wav.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
                return wav;
            // Compressed WAV payloads (ADPCM, etc.): let Media Foundation decode them.
            wav.Dispose();
        }
        return new MediaFoundationReader(path);
    }

    private sealed class FirstTwoChannelsSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly int _inChannels = source.WaveFormat.Channels;
        private float[] _buffer = Array.Empty<float>();

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            var needed = frames * _inChannels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            var read = source.Read(_buffer, 0, needed);
            var framesRead = read / _inChannels;
            for (var f = 0; f < framesRead; f++)
            {
                buffer[offset + f * 2] = _buffer[f * _inChannels];
                buffer[offset + f * 2 + 1] = _buffer[f * _inChannels + 1];
            }
            return framesRead * 2;
        }
    }
}
