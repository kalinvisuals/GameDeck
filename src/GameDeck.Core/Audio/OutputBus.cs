using GameDeck.Core.Localization;
using System.Collections.Concurrent;
using GameDeck.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GameDeck.Core.Audio;

/// <summary>
/// One WASAPI shared-mode render stream. The engine owns two of these: the headphone monitor and
/// the virtual microphone output. A bus knows nothing about which driver backs its device, so any
/// virtual cable (or a future GameDeck driver) plugs in here.
///
///   voices ──► sound sub-mixer ──► × SoundGain ──┐
///   persistent input (mic, virtual bus only) ────┴──► mixer ──► × master + limiter ──► device
///
/// Sounds and the mic meet only in the final mixer, so the soundboard level never scales the mic
/// and a silent soundboard never interrupts it.
/// </summary>
internal sealed class OutputBus : IDisposable
{
    private readonly object _gate = new();
    // Concurrent because voices finish on the audio thread, which must never wait on _gate
    // (Close holds _gate while WasapiOut.Stop waits for that same thread).
    private readonly ConcurrentDictionary<Voice, byte> _voices = new();
    private readonly GainSampleProvider _soundGain = new();
    private WasapiOut? _output;
    private MixingSampleProvider? _mixer;
    private MixingSampleProvider? _sounds;
    private MasterSampleProvider? _master;
    private ISampleProvider? _persistentInput;
    private float _volume = 1f;

    public string Name { get; }

    /// <summary>Post-master, post-limiter level: what actually reaches the device.</summary>
    public PeakMeter Meter { get; } = new();
    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }
    public string? Error { get; private set; }
    public bool IsOpen => _output is not null && Error is null;

    /// <summary>Sound voices still attached (diagnostics/tests).</summary>
    internal int VoiceCount => _voices.Count;

    /// <summary>Sound voices the sub-mixer is still pulling from (diagnostics/tests).</summary>
    internal int MixerInputCount
    {
        get { lock (_gate) return _sounds?.MixerInputs.Count() ?? 0; }
    }

    /// <summary>Whether the persistent input (the mic) is attached to the live mixer.</summary>
    internal bool PersistentInputAttached
    {
        get { lock (_gate) return _persistentInput is not null && _mixer?.MixerInputs.Contains(_persistentInput) == true; }
    }

    /// <summary>Gain applied to soundboard voices only (not to the persistent mic input).</summary>
    public float SoundGain
    {
        get => _soundGain.Target;
        set => _soundGain.Target = value;
    }

    /// <summary>Raised (on a worker thread) when the stream dies, e.g. the device was unplugged.</summary>
    public event Action<OutputBus>? Faulted;

    public OutputBus(string name) => Name = name;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            if (_master is not null) _master.Gain = value;
        }
    }

    public void Open(MMDevice device, int latencyMs)
    {
        lock (_gate)
        {
            CloseLocked(null);
            DeviceId = device.ID;
            DeviceName = device.FriendlyName;
            try
            {
                _sounds = new MixingSampleProvider(AudioEngine.MixFormat) { ReadFully = true };
                _soundGain.Source = _sounds;
                _mixer = new MixingSampleProvider(AudioEngine.MixFormat) { ReadFully = true };
                _mixer.AddMixerInput(_soundGain);
                _master = new MasterSampleProvider(_mixer, Meter) { Gain = _volume };
                if (_persistentInput is not null) _mixer.AddMixerInput(_persistentInput);

                var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latencyMs);
                output.PlaybackStopped += OnPlaybackStopped;
                output.Init(new SampleToWaveProvider(_master));
                output.Play();
                _output = output;
                Error = null;
                Log.Info($"{Name}: opened '{DeviceName}' ({latencyMs} ms)");
            }
            catch (Exception ex)
            {
                Error = L.T("engine.openFailed", ex.Message);
                Log.Error($"{Name}: failed to open '{DeviceName}'", ex);
                DisposeOutputLocked();
            }
        }
    }

    /// <summary>Closes the stream. <paramref name="reason"/> is shown in the UI when set.</summary>
    public void Close(string? reason = null)
    {
        lock (_gate) CloseLocked(reason);
    }

    public bool TryAddVoice(Voice voice)
    {
        lock (_gate)
        {
            if (_sounds is null || Error is not null) return false;
            _voices[voice] = 0;
            voice.Finished += OnVoiceFinished;
            _sounds.AddMixerInput(voice);
            return true;
        }
    }

    /// <summary>An input that lives as long as the bus, re-attached on reopen (the microphone).</summary>
    public void SetPersistentInput(ISampleProvider? input)
    {
        lock (_gate)
        {
            if (ReferenceEquals(input, _persistentInput)) return;
            if (_persistentInput is not null) _mixer?.RemoveMixerInput(_persistentInput);
            _persistentInput = input;
            if (input is not null) _mixer?.AddMixerInput(input);
        }
    }

    private void OnVoiceFinished(Voice voice) => _voices.TryRemove(voice, out _);

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _output)) return;
            Error = L.T("engine.stopped", e.Exception.Message);
        }
        Log.Warn($"{Name}: playback stopped on '{DeviceName}'", e.Exception);
        // Never dispose WASAPI from inside its own callback.
        ThreadPool.QueueUserWorkItem(_ => Faulted?.Invoke(this));
    }

    private void CloseLocked(string? reason)
    {
        DisposeOutputLocked();
        foreach (var v in _voices.Keys) v.Abandon();
        _voices.Clear();
        _mixer = null;
        _sounds = null;
        _soundGain.Source = null;
        _master = null;
        if (reason is not null)
        {
            Error = reason;
        }
        else
        {
            Error = null;
            DeviceId = null;
            DeviceName = null;
        }
    }

    private void DisposeOutputLocked()
    {
        var output = _output;
        _output = null;
        if (output is null) return;
        output.PlaybackStopped -= OnPlaybackStopped;
        try { output.Stop(); } catch { /* device may already be gone */ }
        try { output.Dispose(); } catch { /* same */ }
    }

    public void Dispose() => Close();

    /// <summary>Never-ending gain stage with a per-buffer ramp, so volume moves without zipper noise.</summary>
    private sealed class GainSampleProvider : ISampleProvider
    {
        public volatile ISampleProvider? Source;
        public float Target { get; set; } = 1f;
        private float _current = 1f;
        public WaveFormat WaveFormat => AudioEngine.MixFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = Source?.Read(buffer, offset, count) ?? 0;
            Array.Clear(buffer, offset + read, count - read);
            var target = Target;
            var step = count > 0 ? (target - _current) / count : 0;
            for (var i = offset; i < offset + count; i++)
            {
                _current += step;
                buffer[i] *= _current;
            }
            _current = target;
            return count;
        }
    }

    /// <summary>Master gain plus a soft limiter so stacked sounds distort gently instead of clipping hard.</summary>
    private sealed class MasterSampleProvider(ISampleProvider source, PeakMeter meter) : ISampleProvider
    {
        public float Gain { get; set; } = 1f;
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var gain = Gain;
            for (var i = offset; i < offset + read; i++)
            {
                var x = buffer[i] * gain;
                var a = Math.Abs(x);
                // Linear below 0.8, then a smooth knee that approaches but never exceeds 1.0.
                if (a > 0.8f) x = Math.Sign(x) * (0.8f + 0.2f * MathF.Tanh((a - 0.8f) / 0.2f));
                buffer[i] = x;
            }
            meter.Report(buffer.AsSpan(offset, read));
            return read;
        }
    }
}
