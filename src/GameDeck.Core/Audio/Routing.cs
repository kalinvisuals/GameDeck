using GameDeck.Core.Localization;
using System.Diagnostics;
using GameDeck.Core.Config;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameDeck.Core.Audio;

public sealed record EndpointInfo(string Id, string Name, string Adapter, string State);

/// <summary>A render endpoint plus the capture endpoint on the same adapter (where Discord listens).</summary>
public sealed record CablePair(EndpointInfo Render, EndpointInfo? Capture);

/// <summary>Level is "error" (routing broken), "warning" (works, but probably not as intended) or "info".</summary>
public sealed record RoutingIssue(string Level, string Message, string? Fix);

public sealed record RoutingReport(
    IReadOnlyList<CablePair> Cables,
    CablePair? Selected,
    IReadOnlyList<RoutingIssue> Issues);

/// <summary>Finds virtual cables and turns the current routing into actionable messages.</summary>
public static class RoutingAdvisor
{
    public static RoutingReport Build(AudioSettings s, EngineStatus status, MicStats mic)
    {
        using var e = new MMDeviceEnumerator();
        return Build(s, status, mic, List(e, DataFlow.Render), List(e, DataFlow.Capture));
    }

    /// <summary>
    /// Same report from endpoint lists read earlier (see <see cref="List"/>). Enumerating every endpoint
    /// with its properties takes most of a second on PCs with many devices, so the service reads them
    /// only when Windows reports a device change instead of on every state push.
    /// </summary>
    public static RoutingReport Build(AudioSettings s, EngineStatus status, MicStats mic, IReadOnlyList<EndpointInfo> renders, IReadOnlyList<EndpointInfo> captures)
    {
        CablePair Pair(EndpointInfo r) => new(r, captures.FirstOrDefault(c => AudioEngine.SameAdapter(c.Adapter, r.Adapter)));
        var cables = renders
            .Where(r => AudioEngine.LooksVirtual(r.Adapter) || AudioEngine.LooksVirtual(r.Name))
            .Select(Pair)
            .Where(p => p.Capture is not null)
            .ToList();

        var issues = new List<RoutingIssue>();
        CablePair? selected = null;

        if (s.VirtualDeviceId is null)
        {
            if (cables.Count == 0)
                issues.Add(new("error", L.T("route.noCable"), L.T("route.noCable.fix")));
            else
                issues.Add(new("warning", L.T("route.virtualOff"), L.T("route.virtualOff.fix", cables[0].Render.Name)));
        }
        else
        {
            var render = renders.FirstOrDefault(r => r.Id == s.VirtualDeviceId);
            if (render is not null)
            {
                selected = Pair(render);
                if (selected.Capture is null)
                    issues.Add(new("warning", L.T("route.notCable", render.Name),
                        cables.Count > 0 ? L.T("route.notCable.fixOther", cables[0].Render.Name) : L.T("route.notCable.fixInstall")));
                else if (selected.Capture.State != "Active")
                    issues.Add(new("error", L.T("route.captureState", selected.Capture.Name, StateText(selected.Capture.State)),
                        L.T("route.captureState.fix", selected.Capture.Name)));
                else if (!AudioEngine.LooksVirtual(render.Adapter) && !AudioEngine.LooksVirtual(render.Name))
                    issues.Add(new("warning", L.T("route.unknownCable", render.Name, selected.Capture.Name),
                        cables.Count > 0 ? L.T("route.notCable.fixOther", cables[0].Render.Name) : L.T("route.notCable.fixInstall")));
            }
            if (status.Virtual.Error is not null) issues.Add(new("error", status.Virtual.Error, null));
        }

        if (status.Monitor.Enabled && status.Monitor.Error is not null) issues.Add(new("error", status.Monitor.Error, null));

        if (s.MicEnabled && s.MicDeviceId is null)
            issues.Add(new("warning", L.T("route.micNoDevice"), L.T("route.micNoDevice.fix")));
        else if (status.Mic.Enabled && status.Mic.Error is not null)
            issues.Add(new("error", status.Mic.Error, null));
        else if (!s.MicEnabled && s.VirtualDeviceId is not null)
            issues.Add(new("info", L.T("route.voiceNotMixed"), L.T("route.voiceNotMixed.fix")));

        if (mic.Underruns > 0 || mic.Drops > 0)
            issues.Add(new("info", L.T("route.micBuffer", mic.Underruns, mic.Drops), mic.Underruns > 10 ? L.T("route.micBuffer.fix") : null));

        if (selected?.Capture is { State: "Active" } cap)
            issues.Add(new("info", L.T("route.howTo", cap.Name), null));

        return new RoutingReport(cables, selected, issues);
    }

    /// <summary>Windows endpoint state in words ("disabled", "dezactivată").</summary>
    public static string StateText(string state) => L.T("route.state." + state);

    public static IReadOnlyList<EndpointInfo> List(MMDeviceEnumerator e, DataFlow flow) =>
        e.EnumerateAudioEndPoints(flow, DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged)
            .Select(d => new EndpointInfo(d.ID, d.FriendlyName, AudioEngine.SafeAdapter(d), d.State.ToString()))
            .ToList();
}

/// <param name="FrequencyHz">Frequency actually received. Anything but ~1000 Hz means the cable's two
/// sides run at different sample rates without conversion (voices arrive pitch-shifted).</param>
public sealed record RouteTestResult(
    bool Detected, double LatencyMs, double ToneDbfs, double NoiseDbfs, string CaptureDevice, string Message,
    double FrequencyHz = 0, bool PitchOk = false);

/// <summary>
/// End-to-end check of the virtual path: plays a short 1 kHz tone into the virtual output only and
/// listens on the cable's recording endpoint, the same endpoint Discord reads.
/// </summary>
public static class RouteSelfTest
{
    public const double ToneHz = 1000;

    public static Task<RouteTestResult> RunAsync(AudioEngine engine, string captureDeviceId, CancellationToken ct = default) =>
        RunCoreAsync(engine, captureDeviceId, loopback: false, ct);

    /// <summary>Records the virtual render endpoint itself (WASAPI loopback): proves GameDeck's own output, independent of any cable.</summary>
    public static Task<RouteTestResult> RunOnRenderAsync(AudioEngine engine, string renderDeviceId, CancellationToken ct = default) =>
        RunCoreAsync(engine, renderDeviceId, loopback: true, ct);

    private static async Task<RouteTestResult> RunCoreAsync(AudioEngine engine, string deviceId, bool loopback, CancellationToken ct)
    {
        using var e = new MMDeviceEnumerator();
        MMDevice device;
        try { device = e.GetDevice(deviceId); }
        catch { return new(false, 0, -120, -120, "?", L.T("test.captureMissing")); }

        using var rec = new Recorder(device, loopback);
        rec.Start();
        await Task.Delay(300, ct); // let capture settle, measures the noise floor
        var toneAt = rec.Elapsed;
        // 1 s tone, listen 2.5 s: some virtual drivers add several hundred ms of latency.
        if (!engine.PlayTestTone(ToneHz, TimeSpan.FromSeconds(1), 0.25f))
            return new(false, 0, -120, -120, device.FriendlyName, L.T("test.virtualClosed"));
        await Task.Delay(2500, ct);
        var audio = rec.Stop();

        // Detect by energy, not by an exact frequency: a cable with mismatched sample rates delivers
        // the tone pitch-shifted, and that is a finding to report, not "not connected".
        var rate = audio.SampleRate;
        var block = rate / 100; // 10 ms
        var pre = Math.Clamp((int)(toneAt.TotalSeconds * rate) - block, 0, audio.Samples.Length);
        var noise = Analysis.Rms(audio.Samples, 0, pre);
        var threshold = Math.Max(noise * 10, 0.003); // ≥ 20 dB over the floor, and above -50 dBFS
        int onset = -1, active = 0, last = -1;
        for (var i = pre; i + block <= audio.Samples.Length; i += block)
        {
            if (Analysis.Rms(audio.Samples, i, block) <= threshold) continue;
            if (onset < 0) onset = i;
            last = i;
            active++;
        }
        // Gaps inside the tone body = the driver (or clock drift) dropping audio.
        var gaps = onset < 0 ? 0 : (last - onset) / block + 1 - active;

        var noiseDb = Analysis.Db(noise);
        if (onset < 0 || active < 30) // at least 300 ms of the 1 s tone
            return new(false, 0, -180, noiseDb, device.FriendlyName,
                L.T("test.noTone", device.FriendlyName));

        var window = Math.Min(rate / 4, audio.Samples.Length - (onset + block * 5));
        var start = onset + block * 5;
        var toneDb = Analysis.Db(Analysis.Rms(audio.Samples, start, window) * Math.Sqrt(2));
        var freq = Analysis.DominantFrequency(audio.Samples, start, window, rate, 300, 3000);
        var pitchOk = Math.Abs(freq / ToneHz - 1) < 0.01;
        var latency = Math.Max(0, (onset / (double)rate - toneAt.TotalSeconds) * 1000);

        if (!pitchOk)
            return new(true, latency, toneDb, noiseDb, device.FriendlyName,
                L.T("test.pitch", device.FriendlyName, ToneHz.ToString("0"), freq.ToString("0"), rate), freq, false);

        var gapNote = gaps > 0 ? L.T("test.gaps", gaps) : "";
        return new(true, latency, toneDb, noiseDb, device.FriendlyName,
            L.T("test.ok", device.FriendlyName, latency.ToString("0"), toneDb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + gapNote,
            freq, gaps == 0);
    }
}

public sealed record CapturedAudio(float[] Samples, int SampleRate);

/// <summary>Records an endpoint to mono float in memory (never to disk).</summary>
public sealed class Recorder : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly MemoryStream _bytes = new();
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private TimeSpan? _firstData;

    /// <param name="loopback">true records what a render endpoint is playing (WASAPI loopback).</param>
    public Recorder(MMDevice device, bool loopback)
    {
        _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 20);
        _capture.DataAvailable += (_, a) =>
        {
            lock (_gate)
            {
                _firstData ??= _clock.Elapsed - TimeSpan.FromSeconds(a.BytesRecorded / (double)_capture.WaveFormat.AverageBytesPerSecond);
                _bytes.Write(a.Buffer, 0, a.BytesRecorded);
            }
        };
    }

    /// <summary>Time since the first recorded sample (aligns events with sample positions).</summary>
    public TimeSpan Elapsed
    {
        get { lock (_gate) return _clock.Elapsed - (_firstData ?? TimeSpan.Zero); }
    }

    public void Start()
    {
        _clock.Start();
        _capture.StartRecording();
    }

    public CapturedAudio Stop()
    {
        try { _capture.StopRecording(); } catch { }
        byte[] raw;
        lock (_gate) raw = _bytes.ToArray();
        var fmt = _capture.WaveFormat;
        var provider = new RawSourceWaveStream(new MemoryStream(raw), fmt).ToSampleProvider();
        var interleaved = new List<float>(raw.Length / Math.Max(1, fmt.BitsPerSample / 8));
        var buf = new float[fmt.SampleRate * fmt.Channels / 10];
        int n;
        while ((n = provider.Read(buf, 0, buf.Length)) > 0) interleaved.AddRange(buf.AsSpan(0, n).ToArray());
        var ch = fmt.Channels;
        var mono = new float[interleaved.Count / ch];
        for (var i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (var c = 0; c < ch; c++) sum += interleaved[i * ch + c];
            mono[i] = sum / ch;
        }
        return new CapturedAudio(mono, fmt.SampleRate);
    }

    public void Dispose() => _capture.Dispose();
}

public static class Analysis
{
    /// <summary>Amplitude of one frequency over a window (Goertzel), as a linear peak value.</summary>
    public static double Goertzel(float[] x, int start, int length, double freq, int rate)
    {
        if (length <= 0) return 0;
        var k = 2 * Math.Cos(2 * Math.PI * freq / rate);
        double s1 = 0, s2 = 0;
        for (var i = start; i < start + length && i < x.Length; i++)
        {
            var s = x[i] + k * s1 - s2;
            s2 = s1;
            s1 = s;
        }
        var power = s1 * s1 + s2 * s2 - k * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(0, power)) / length;
    }

    public static double Rms(float[] x, int start, int length)
    {
        double sum = 0;
        var end = Math.Min(x.Length, start + length);
        for (var i = start; i < end; i++) sum += x[i] * (double)x[i];
        return end > start ? Math.Sqrt(sum / (end - start)) : 0;
    }

    public static double Db(double linear) => linear <= 1e-9 ? -180 : 20 * Math.Log10(linear);

    /// <summary>Strongest frequency in [min, max] Hz: coarse 5 Hz scan, then 0.25 Hz refinement.</summary>
    public static double DominantFrequency(float[] x, int start, int length, int rate, double min, double max)
    {
        double best = min, bestAmp = -1;
        for (var f = min; f <= max; f += 5)
        {
            var a = Goertzel(x, start, length, f, rate);
            if (a > bestAmp) { bestAmp = a; best = f; }
        }
        var center = best;
        for (var f = center - 5; f <= center + 5; f += 0.25)
        {
            var a = Goertzel(x, start, length, f, rate);
            if (a > bestAmp) { bestAmp = a; best = f; }
        }
        return best;
    }
}
