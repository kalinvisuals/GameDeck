using NAudio.Wave;

namespace GameDeck.Core.Audio;

/// <summary>
/// One playback of a <see cref="CachedSound"/> on one output bus. Runs on the audio thread:
/// no allocation, no locks. Gain changes are smoothed and stops fade out to avoid clicks.
/// </summary>
internal sealed class Voice : ISampleProvider
{
    private const int FadeSamples = AudioEngine.SampleRate * AudioEngine.Channels / 100; // 10 ms
    private const float GainSmoothing = 0.0015f;
    /// <summary>Ramp at a trimmed edge so a cut in the middle of a waveform does not click (5 ms).</summary>
    private const int EdgeFrames = AudioEngine.SampleRate / 200;

    private readonly float[] _data;
    private readonly int _start;
    private readonly int _end;
    private readonly bool _fadeIn;
    private readonly bool _fadeOut;
    private readonly bool _loop;
    private readonly Func<float> _gain;
    private readonly PeakMeter? _meter;
    private int _position;
    private float _currentGain;
    private volatile bool _stopRequested;
    private int _fadeRemaining = FadeSamples;
    private int _finished;

    public WaveFormat WaveFormat => AudioEngine.MixFormat;
    public bool IsFinished => Volatile.Read(ref _finished) == 1;

    /// <summary>Raised exactly once, possibly on the audio thread. Handlers must not block.</summary>
    public event Action<Voice>? Finished;

    /// <param name="meter">Optional level tap (post sound volume). Only one voice per playback reports to it.</param>
    public Voice(CachedSound sound, bool loop, Func<float> gain, PeakMeter? meter = null)
        : this(sound, 0, sound.Samples.Length, loop, gain, meter) { }

    /// <param name="start">First sample to play (frame aligned).</param>
    /// <param name="end">Sample after the last one to play (frame aligned); loops wrap back to <paramref name="start"/>.</param>
    public Voice(CachedSound sound, int start, int end, bool loop, Func<float> gain, PeakMeter? meter = null)
    {
        _data = sound.Samples;
        _start = start;
        _end = end;
        _position = start;
        _fadeIn = start > 0;
        _fadeOut = end < _data.Length;
        _loop = loop;
        _gain = gain;
        _meter = meter;
        _currentGain = gain();
    }

    public void RequestStop() => _stopRequested = true;

    /// <summary>Marks the voice finished without audio (its bus went away).</summary>
    public void Abandon() => Finish();

    public int Read(float[] buffer, int offset, int count)
    {
        var read = ReadCore(buffer, offset, count);
        _meter?.Report(buffer.AsSpan(offset, read));
        return read;
    }

    private int ReadCore(float[] buffer, int offset, int count)
    {
        if (IsFinished) return 0;

        var target = _gain();
        var written = 0;
        while (written < count)
        {
            if (_position >= _end)
            {
                // Keep wrapping while a stop fade is in progress; the fade itself ends the voice.
                if (_loop) _position = _start;
                else break;
            }

            var n = Math.Min(count - written, _end - _position);
            for (var i = 0; i < n; i++)
            {
                _currentGain += (target - _currentGain) * GainSmoothing;
                var sample = _data[_position + i] * _currentGain;
                if (_fadeIn || _fadeOut) sample *= EdgeGain(_position + i);
                if (_stopRequested)
                {
                    sample *= _fadeRemaining / (float)FadeSamples;
                    if (--_fadeRemaining <= 0)
                    {
                        buffer[offset + written + i] = 0;
                        written += i + 1;
                        _position += i + 1;
                        Finish();
                        return PadToFrame(buffer, offset, written);
                    }
                }
                buffer[offset + written + i] = sample;
            }
            written += n;
            _position += n;
        }

        if (written < count) Finish();
        return written;
    }

    private float EdgeGain(int index)
    {
        var gain = 1f;
        if (_fadeIn)
        {
            var frame = (index - _start) / AudioEngine.Channels;
            if (frame < EdgeFrames) gain = frame / (float)EdgeFrames;
        }
        if (_fadeOut)
        {
            var frame = (_end - 1 - index) / AudioEngine.Channels;
            if (frame < EdgeFrames) gain = Math.Min(gain, frame / (float)EdgeFrames);
        }
        return gain;
    }

    private static int PadToFrame(float[] buffer, int offset, int written)
    {
        if (written % AudioEngine.Channels == 0) return written;
        buffer[offset + written] = 0;
        return written + 1;
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0) Finished?.Invoke(this);
    }
}
