using GameDeck.Core.Localization;
using GameDeck.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GameDeck.Core.Audio;

/// <summary>Continuity counters for the mic path since it was opened.</summary>
public sealed record MicStats(long Underruns, long Drops, double BufferedMs);

/// <summary>
/// Captures a physical microphone and exposes it as a never-ending 48 kHz stereo stream that the
/// engine mixes into the virtual output only, never into the headphone monitor.
///
/// The mic and the output device run on different clocks, so a small jitter buffer sits between
/// them: it prebuffers <see cref="TargetBuffer"/>, re-primes after an underrun instead of chopping
/// silence into the voice sample-by-sample, and trims back to the target only if the backlog
/// exceeds <see cref="MaxBuffer"/>. Both events are counted (<see cref="Stats"/>).
/// </summary>
internal sealed class MicInput : IDisposable
{
    internal static readonly TimeSpan TargetBuffer = TimeSpan.FromMilliseconds(30);
    internal static readonly TimeSpan MaxBuffer = TimeSpan.FromMilliseconds(150);

    private WasapiCapture? _capture;
    private JitterBuffer? _jitter;

    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }
    public string? Error { get; private set; }
    public bool IsOpen => _capture is not null && Error is null;
    public float Volume { get; set; } = 1f;

    /// <summary>Captured mic level after mic gain, i.e. what is sent into the virtual output.</summary>
    public PeakMeter Meter => _output.Meter;

    /// <summary>Stable provider handed to the bus; swaps its source on reopen.</summary>
    public ISampleProvider Output { get; }

    public MicStats Stats => _jitter?.Stats ?? new MicStats(0, 0, 0);

    public event Action<MicInput>? Faulted;

    private readonly SwitchableProvider _output = new();

    public MicInput()
    {
        Output = _output;
        _output.GainSource = () => Volume;
    }

    public void Open(MMDevice device, int latencyMs)
    {
        Close();
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        try
        {
            var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: Math.Max(latencyMs / 2, 10));
            var jitter = new JitterBuffer(capture.WaveFormat, TargetBuffer, MaxBuffer);

            ISampleProvider chain = jitter.ToSampleProvider();
            chain = chain.WaveFormat.Channels switch
            {
                1 => new MonoToStereoSampleProvider(chain),
                2 => chain,
                _ => throw new NotSupportedException(L.T("mic.channels", chain.WaveFormat.Channels)),
            };
            if (chain.WaveFormat.SampleRate != AudioEngine.SampleRate)
                chain = new WdlResamplingSampleProvider(chain, AudioEngine.SampleRate);
            // The voice is sent as mono on both channels (average of left and right).
            chain = new MonoToStereoSampleProvider(new StereoToMonoSampleProvider(chain));

            capture.DataAvailable += (_, e) => jitter.Write(e.Buffer, e.BytesRecorded);
            capture.RecordingStopped += (s, e) =>
            {
                if (e.Exception is null || !ReferenceEquals(s, _capture)) return;
                Error = L.T("mic.stopped", e.Exception.Message);
                Log.Warn("Mic: capture stopped", e.Exception);
                ThreadPool.QueueUserWorkItem(_ => Faulted?.Invoke(this));
            };

            capture.StartRecording();
            _capture = capture;
            _jitter = jitter;
            _output.Source = chain;
            Error = null;
            Log.Info($"Mic: opened '{DeviceName}' {capture.WaveFormat}");
        }
        catch (Exception ex)
        {
            Error = L.T("mic.openFailed", ex.Message);
            Log.Error($"Mic: failed to open '{DeviceName}'", ex);
            DisposeCapture();
        }
    }

    public void Close(string? reason = null)
    {
        DisposeCapture();
        Error = reason;
        if (reason is null)
        {
            DeviceId = null;
            DeviceName = null;
        }
    }

    private void DisposeCapture()
    {
        _output.Source = null;
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        try { capture.StopRecording(); } catch { /* device may be gone */ }
        try { capture.Dispose(); } catch { /* same */ }
        // _jitter is kept so the last stats stay readable until the next Open.
    }

    public void Dispose() => Close();

    /// <summary>Capture-side writer, render-side reader. BufferedWaveProvider locks internally.</summary>
    internal sealed class JitterBuffer : IWaveProvider
    {
        private readonly BufferedWaveProvider _buffer;
        private readonly int _targetBytes;
        private readonly int _maxBytes;
        private readonly byte[] _scratch;
        private volatile bool _primed;
        private long _underruns;
        private long _drops;

        public JitterBuffer(WaveFormat format, TimeSpan target, TimeSpan max)
        {
            _buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };
            _targetBytes = Align(format, format.AverageBytesPerSecond * target.TotalSeconds);
            _maxBytes = Align(format, format.AverageBytesPerSecond * max.TotalSeconds);
            _scratch = new byte[_maxBytes];
        }

        public WaveFormat WaveFormat => _buffer.WaveFormat;

        public MicStats Stats => new(Interlocked.Read(ref _underruns), Interlocked.Read(ref _drops), _buffer.BufferedDuration.TotalMilliseconds);

        public void Write(byte[] data, int count)
        {
            _buffer.AddSamples(data, 0, count);
            var excess = _buffer.BufferedBytes - _maxBytes;
            if (excess <= 0) return;
            // Mic clock running fast: trim back to the target in one go (rare, bounded latency).
            var trim = Align(WaveFormat, _buffer.BufferedBytes - _targetBytes);
            while (trim > 0)
            {
                var n = _buffer.Read(_scratch, 0, Math.Min(trim, _scratch.Length));
                if (n <= 0) break;
                trim -= n;
            }
            Interlocked.Increment(ref _drops);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!_primed)
            {
                if (_buffer.BufferedBytes < _targetBytes)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }
                _primed = true;
            }

            var read = _buffer.Read(buffer, offset, count);
            if (read < count)
            {
                // Mic clock running slow (or capture stalled): fill this one buffer and rebuild the
                // cushion, rather than emitting a stream of tiny gaps.
                Array.Clear(buffer, offset + read, count - read);
                _primed = false;
                Interlocked.Increment(ref _underruns);
            }
            return count;
        }

        private static int Align(WaveFormat f, double bytes) => (int)bytes / f.BlockAlign * f.BlockAlign;
    }

    private sealed class SwitchableProvider : ISampleProvider
    {
        public volatile ISampleProvider? Source;
        public Func<float> GainSource = () => 1f;
        public readonly PeakMeter Meter = new();
        public WaveFormat WaveFormat => AudioEngine.MixFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var source = Source;
            var read = source?.Read(buffer, offset, count) ?? 0;
            var gain = GainSource();
            for (var i = offset; i < offset + read; i++) buffer[i] *= gain;
            Meter.Report(buffer.AsSpan(offset, read));
            Array.Clear(buffer, offset + read, count - read);
            return count; // never ends, so the mixer keeps it attached
        }
    }
}
