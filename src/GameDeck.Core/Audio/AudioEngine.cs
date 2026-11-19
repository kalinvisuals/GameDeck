using GameDeck.Core.Localization;
using System.Collections.Concurrent;
using GameDeck.Core.Config;
using GameDeck.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace GameDeck.Core.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, string Adapter, bool IsDefault);

public sealed record AudioDeviceList(IReadOnlyList<AudioDeviceInfo> Outputs, IReadOnlyList<AudioDeviceInfo> Inputs);

/// <param name="TrimStartMs">Where playback starts in the file.</param>
/// <param name="TrimEndMs">Where playback ends; 0 = the end of the file.</param>
public sealed record PlayRequest(string SoundId, string Path, float Volume, bool Loop, PlayMode Mode, long TrimStartMs = 0, long TrimEndMs = 0);

public enum TriggerResult { Started, Stopped }

public sealed record PlayingInfo(Guid InstanceId, string SoundId, double ElapsedMs, double DurationMs, bool Loop);

public sealed record BusStatus(bool Enabled, bool Ok, string? DeviceName, string? Error);

public sealed record EngineStatus(BusStatus Monitor, BusStatus Virtual, BusStatus Mic);

/// <summary>Linear peak (0..1+) of each signal since the previous snapshot.</summary>
public sealed record LevelSnapshot(float Mic, float Sounds, float Monitor, float Virtual)
{
    public bool IsSilent => Mic < 1e-4f && Sounds < 1e-4f && Monitor < 1e-4f && Virtual < 1e-4f;
}

/// <summary>
/// Owns all audio: two output buses (headphone monitor + virtual output), the optional mic input,
/// decoded sound cache and the set of playing sounds. Device-agnostic: it only sees WASAPI endpoints.
/// Signal flow:
///   sounds ──┬──► Monitor bus ──► headphones
///            └──► Virtual bus ──► virtual cable render endpoint ──► (Discord reads the cable's capture side)
///   mic ─────────► Virtual bus only (never the monitor, so there is no self-feedback)
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    private readonly object _gate = new();
    private readonly OutputBus _monitor = new("Monitor");
    private readonly OutputBus _virtual = new("Virtual");
    private readonly MicInput _mic = new();
    private readonly SoundCache _cache = new();
    private readonly PeakMeter _soundsMeter = new();
    private readonly Dictionary<Guid, Instance> _instances = new();
    private readonly ConcurrentDictionary<string, float> _volumes = new();
    private readonly MMDeviceEnumerator _notificationEnumerator;
    private readonly DeviceNotifications _notifications;
    private readonly Timer _healthTimer;
    private AudioSettings _settings = new();
    private EngineStatus? _lastStatus;
    private int _reconcileQueued;
    private bool _disposed;

    /// <summary>Raised on a worker thread whenever the set of playing sounds changes.</summary>
    public event Action? PlayingChanged;

    /// <summary>Raised on a worker thread whenever device/bus status changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised on a worker thread after Windows reports a device was added/removed/changed.</summary>
    public event Action? DevicesChanged;

    public AudioEngine()
    {
        _monitor.Faulted += _ => QueueReconcile();
        _virtual.Faulted += _ => QueueReconcile();
        _mic.Faulted += _ => QueueReconcile();

        _notificationEnumerator = new MMDeviceEnumerator();
        _notifications = new DeviceNotifications(QueueReconcile);
        _notificationEnumerator.RegisterEndpointNotificationCallback(_notifications);

        // Safety net for drivers that don't send notifications reliably.
        _healthTimer = new Timer(_ => { if (NeedsRecovery()) QueueReconcile(); }, null, 3000, 3000);
    }

    public void ApplySettings(AudioSettings settings)
    {
        lock (_gate)
        {
            _settings = Clone(settings);
            ReconcileLocked();
        }
        PublishStatusIfChanged();
    }

    public AudioDeviceList GetDevices()
    {
        using var e = new MMDeviceEnumerator();
        return new AudioDeviceList(List(e, DataFlow.Render), List(e, DataFlow.Capture));

        static List<AudioDeviceInfo> List(MMDeviceEnumerator e, DataFlow flow)
        {
            string? defaultId = null;
            if (e.HasDefaultAudioEndpoint(flow, Role.Multimedia))
                defaultId = e.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID;
            return e.EnumerateAudioEndPoints(flow, DeviceState.Active)
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, SafeAdapter(d), d.ID == defaultId))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Decodes into the cache ahead of time; also validates imported files.</summary>
    public Task<CachedSound> PreloadAsync(string path) => _cache.GetAsync(path);

    public void Evict(string path) => _cache.Evict(path);

    public void SetSoundVolume(string soundId, float volume) => _volumes[soundId] = volume;

    public async Task<TriggerResult> TriggerAsync(PlayRequest request)
    {
        _volumes[request.SoundId] = request.Volume;

        if (request.Mode == PlayMode.Toggle && IsPlaying(request.SoundId))
        {
            Stop(request.SoundId);
            return TriggerResult.Stopped;
        }

        var sound = await _cache.GetAsync(request.Path).ConfigureAwait(false);

        lock (_gate)
        {
            if (request.Mode == PlayMode.Restart) StopLocked(request.SoundId);

            var (start, end) = sound.Range(request.TrimStartMs, request.TrimEndMs);
            var instance = new Instance(request.SoundId, CachedSound.ToTime(end - start), request.Loop);
            Func<float> gain = () => _volumes.TryGetValue(request.SoundId, out var v) ? v : 1f;
            foreach (var bus in ActiveBusesLocked())
            {
                // Meter only the first copy so the soundboard level isn't counted per bus.
                var voice = new Voice(sound, start, end, request.Loop, gain, instance.Voices.Count == 0 ? _soundsMeter : null);
                voice.Finished += v => ThreadPool.QueueUserWorkItem(_ => OnVoiceFinished(instance, v));
                if (bus.TryAddVoice(voice)) instance.Voices.Add(voice);
            }

            if (instance.Voices.Count == 0)
                throw new InvalidOperationException(L.T("engine.noOutput"));

            _instances[instance.Id] = instance;
        }

        PlayingChanged?.Invoke();
        return TriggerResult.Started;
    }

    public void Stop(string soundId)
    {
        lock (_gate) StopLocked(soundId);
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var instance in _instances.Values)
                foreach (var v in instance.Voices) v.RequestStop();
        }
    }

    public bool IsPlaying(string soundId)
    {
        lock (_gate) return _instances.Values.Any(i => i.SoundId == soundId && !i.Stopping);
    }

    public IReadOnlyList<PlayingInfo> GetPlaying()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            return _instances.Values
                .Where(i => !i.Stopping)
                .Select(i => new PlayingInfo(i.Id, i.SoundId, (now - i.StartedUtc).TotalMilliseconds, i.Duration.TotalMilliseconds, i.Loop))
                .ToList();
        }
    }

    /// <summary>True when nothing is playing and no microphone is being captured.</summary>
    public bool IsIdle
    {
        get { lock (_gate) return _instances.Count == 0 && !_mic.IsOpen; }
    }

    /// <summary>Resource counters, used by tests to prove finished sounds release everything.</summary>
    internal (int Instances, int MonitorVoices, int VirtualVoices, int MonitorMixerInputs, int VirtualMixerInputs) Counters()
    {
        lock (_gate)
            return (_instances.Count, _monitor.VoiceCount, _virtual.VoiceCount, _monitor.MixerInputCount, _virtual.MixerInputCount);
    }

    /// <summary>Jitter-buffer counters for the mic path (underruns = gaps, drops = trimmed backlog).</summary>
    public MicStats GetMicStats() => _mic.Stats;

    /// <summary>Whether the mic is currently attached to the virtual bus mixer.</summary>
    public bool MicFlowing
    {
        get { lock (_gate) return _mic.IsOpen && _virtual.PersistentInputAttached; }
    }

    /// <summary>
    /// Sends a sine tone into the virtual output ONLY (never the headphones), through the same
    /// soundboard gain stage as real sounds. Used by the routing self-test.
    /// </summary>
    public bool PlayTestTone(double frequency, TimeSpan duration, float amplitude)
    {
        var frames = (int)(duration.TotalSeconds * SampleRate);
        var data = new float[frames * Channels];
        var fade = SampleRate / 200; // 5 ms ramps, no clicks
        for (var i = 0; i < frames; i++)
        {
            var env = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (double)fade);
            var v = (float)(amplitude * env * Math.Sin(2 * Math.PI * frequency * i / SampleRate));
            data[i * 2] = v;
            data[i * 2 + 1] = v;
        }
        lock (_gate)
        {
            if (!_virtual.IsOpen) return false;
            return _virtual.TryAddVoice(new Voice(new CachedSound(data), loop: false, () => 1f));
        }
    }

    /// <summary>Drains the peak meters. Call from a single poller (the dashboard pusher).</summary>
    public LevelSnapshot TakeLevels() =>
        new(_mic.Meter.Take(), _soundsMeter.Take(), _monitor.Meter.Take(), _virtual.Meter.Take());

    public EngineStatus GetStatus()
    {
        lock (_gate)
        {
            return new EngineStatus(
                new BusStatus(_settings.MonitorEnabled, _monitor.IsOpen, _monitor.DeviceName, _monitor.Error),
                new BusStatus(_settings.VirtualDeviceId is not null, _virtual.IsOpen, _virtual.DeviceName, _virtual.Error),
                new BusStatus(_settings.MicEnabled && _settings.MicDeviceId is not null, _mic.IsOpen, _mic.DeviceName, _mic.Error));
        }
    }

    private void StopLocked(string soundId)
    {
        foreach (var instance in _instances.Values.Where(i => i.SoundId == soundId))
        {
            instance.Stopping = true;
            foreach (var v in instance.Voices) v.RequestStop();
        }
    }

    private IEnumerable<OutputBus> ActiveBusesLocked()
    {
        if (_settings.MonitorEnabled && _monitor.IsOpen) yield return _monitor;
        if (_virtual.IsOpen) yield return _virtual;
    }

    private void OnVoiceFinished(Instance instance, Voice voice)
    {
        bool removed;
        lock (_gate)
        {
            if (!instance.Voices.All(v => v.IsFinished)) return;
            removed = _instances.Remove(instance.Id);
        }
        if (removed) PlayingChanged?.Invoke();
    }

    private bool NeedsRecovery()
    {
        lock (_gate)
        {
            return (_settings.MonitorEnabled && !_monitor.IsOpen)
                || (_settings.VirtualDeviceId is not null && !_virtual.IsOpen)
                || (_settings.MicEnabled && _settings.MicDeviceId is not null && _virtual.IsOpen && !_mic.IsOpen);
        }
    }

    private void QueueReconcile()
    {
        if (Interlocked.Exchange(ref _reconcileQueued, 1) == 1) return;
        // Debounce: unplugging a USB headset fires several notifications in a burst.
        Task.Delay(400).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _reconcileQueued, 0);
            if (_disposed) return;
            try
            {
                lock (_gate) ReconcileLocked();
                PublishStatusIfChanged();
                DevicesChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("Reconcile failed", ex);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Brings buses in line with settings and currently connected devices.</summary>
    private void ReconcileLocked()
    {
        using var e = new MMDeviceEnumerator();
        var s = _settings;
        // Each volume scales exactly one signal; bus masters stay at unity.
        //   MonitorVolume -> sounds in headphones, VirtualVolume -> sounds into the call,
        //   MicVolume -> your voice into the call.
        _monitor.Volume = 1f;
        _monitor.SoundGain = s.MonitorVolume;
        _virtual.Volume = 1f;
        _virtual.SoundGain = s.VirtualVolume;
        _mic.Volume = s.MicVolume;

        var virtualDevice = s.VirtualDeviceId is null ? null : Resolve(e, s.VirtualDeviceId, DataFlow.Render, fallbackToDefault: false);

        // Headphone monitor
        if (!s.MonitorEnabled)
        {
            if (_monitor.DeviceId is not null || _monitor.Error is not null) _monitor.Close();
        }
        else
        {
            var device = Resolve(e, s.MonitorDeviceId, DataFlow.Render, fallbackToDefault: true);
            if (device is null)
                _monitor.Close(s.MonitorDeviceId is null
                    ? L.T("engine.noDefaultOutput")
                    : DescribeUnavailable(e, s.MonitorDeviceId, "engine.role.headphones"));
            else if (virtualDevice is not null && device.ID == virtualDevice.ID)
                // Sounds would reach the call twice and you would hear nothing.
                _monitor.Close(s.MonitorDeviceId is null
                    ? L.T("engine.defaultIsCable", device.FriendlyName)
                    : L.T("engine.headphonesAreCable"));
            else if (!_monitor.IsOpen || _monitor.DeviceId != device.ID)
                _monitor.Open(device, s.LatencyMs);
        }

        // Virtual output
        if (s.VirtualDeviceId is null)
        {
            if (_virtual.DeviceId is not null || _virtual.Error is not null) _virtual.Close();
        }
        else if (virtualDevice is null)
        {
            _virtual.Close(DescribeUnavailable(e, s.VirtualDeviceId, "engine.role.virtual"));
        }
        else if (!_virtual.IsOpen || _virtual.DeviceId != virtualDevice.ID)
        {
            _virtual.Open(virtualDevice, s.LatencyMs);
        }

        // Microphone (into the virtual bus only)
        if (!s.MicEnabled || s.MicDeviceId is null)
        {
            _virtual.SetPersistentInput(null);
            if (_mic.DeviceId is not null || _mic.Error is not null) _mic.Close();
        }
        else if (!_virtual.IsOpen)
        {
            _virtual.SetPersistentInput(null);
            _mic.Close(L.T("engine.micNeedsVirtual"));
        }
        else
        {
            var device = Resolve(e, s.MicDeviceId, DataFlow.Capture, fallbackToDefault: false);
            if (device is null)
            {
                _virtual.SetPersistentInput(null);
                _mic.Close(DescribeUnavailable(e, s.MicDeviceId, "engine.role.mic"));
            }
            else if (virtualDevice is not null && SharesAdapter(device, virtualDevice))
            {
                _virtual.SetPersistentInput(null);
                _mic.Close(L.T("engine.feedback", device.FriendlyName, virtualDevice.FriendlyName));
            }
            else
            {
                if (!_mic.IsOpen || _mic.DeviceId != device.ID) _mic.Open(device, s.LatencyMs);
                _virtual.SetPersistentInput(_mic.IsOpen ? _mic.Output : null);
            }
        }
    }

    /// <summary>Why a selected endpoint can't be used, phrased as something the user can fix.</summary>
    private static string DescribeUnavailable(MMDeviceEnumerator e, string id, string roleKey)
    {
        var role = L.T(roleKey);
        try
        {
            var d = e.GetDevice(id);
            var name = d.FriendlyName; // device names are never translated
            return d.State switch
            {
                DeviceState.Disabled => L.T("engine.disabled", role, name),
                DeviceState.Unplugged => L.T("engine.unplugged", role, name),
                _ => L.T("engine.notPresent", role, name),
            };
        }
        catch
        {
            return L.T("engine.gone", role);
        }
    }

    private void PublishStatusIfChanged()
    {
        var status = GetStatus();
        if (status == _lastStatus) return;
        _lastStatus = status;
        StatusChanged?.Invoke();
    }

    private static MMDevice? Resolve(MMDeviceEnumerator e, string? id, DataFlow flow, bool fallbackToDefault)
    {
        try
        {
            if (id is null)
                return fallbackToDefault && e.HasDefaultAudioEndpoint(flow, Role.Multimedia)
                    ? e.GetDefaultAudioEndpoint(flow, Role.Multimedia)
                    : null;
            var device = e.GetDevice(id);
            return device.State == DeviceState.Active ? device : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Feedback guard: a cable's render ("CABLE Input") and capture ("CABLE Output") endpoints share
    /// one adapter name, so a mic on the same adapter as the virtual output would loop it back into
    /// itself. Physical devices are refused too: mixing a headset's own mic into that headset's
    /// output would put your voice in your ears.
    /// </summary>
    internal static bool SharesAdapter(MMDevice capture, MMDevice render) => SameAdapter(SafeAdapter(capture), SafeAdapter(render));

    public static bool SameAdapter(string captureAdapter, string renderAdapter) =>
        captureAdapter.Length > 0 && captureAdapter.Equals(renderAdapter, StringComparison.OrdinalIgnoreCase);

    internal static bool LooksVirtual(string name) =>
        new[] { "virtual", "cable", "vb-audio", "voicemeeter" }.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static string SafeAdapter(MMDevice d)
    {
        try { return d.DeviceFriendlyName ?? ""; } catch { return ""; }
    }

    private static AudioSettings Clone(AudioSettings s) => new()
    {
        MonitorDeviceId = s.MonitorDeviceId,
        MonitorEnabled = s.MonitorEnabled,
        MonitorVolume = s.MonitorVolume,
        VirtualDeviceId = s.VirtualDeviceId,
        VirtualVolume = s.VirtualVolume,
        MicDeviceId = s.MicDeviceId,
        MicEnabled = s.MicEnabled,
        MicVolume = s.MicVolume,
        LatencyMs = s.LatencyMs,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _healthTimer.Dispose();
        try { _notificationEnumerator.UnregisterEndpointNotificationCallback(_notifications); } catch { }
        _notificationEnumerator.Dispose();
        lock (_gate)
        {
            _virtual.SetPersistentInput(null);
            _mic.Dispose();
            _virtual.Dispose();
            _monitor.Dispose();
        }
    }

    private sealed class Instance(string soundId, TimeSpan duration, bool loop)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string SoundId { get; } = soundId;
        public TimeSpan Duration { get; } = duration;
        public bool Loop { get; } = loop;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public List<Voice> Voices { get; } = new();
        public bool Stopping { get; set; }
    }

    private sealed class DeviceNotifications(Action onChange) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => onChange();
        public void OnDeviceAdded(string pwstrDeviceId) => onChange();
        public void OnDeviceRemoved(string deviceId) => onChange();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia) onChange();
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
