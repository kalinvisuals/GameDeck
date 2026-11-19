// Real-hardware validation of the GameDeck audio pipeline. Not a unit test: it opens real devices.
//
//   dotnet run --project tools/GameDeck.HardwareCheck -- devices
//   dotnet run --project tools/GameDeck.HardwareCheck -- check --virtual "CABLE Input" --capture "CABLE Output"
//        --mic "Microphone (USB" [--monitor "<inaudible render device>"] [--seconds 30]
//
// --virtual  render endpoint GameDeck sends the call mix to
// --capture  recording endpoint a voice app would read (the other side of the cable)
// --mic      physical microphone
// --monitor  render endpoint used as "headphones" for the isolation check; pick something you
//            can't hear (it's recorded via WASAPI loopback). Omitted = monitor checks skipped.
//
// Microphone audio is analysed in memory and never written to disk.
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using NAudio.CoreAudioApi;

var argv = args.ToList();
string? Arg(string name) { var i = argv.IndexOf(name); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }

if (argv.FirstOrDefault() is "probe")
{
    // probe --virtual X --capture Y : 2 s tone into X, print what Y receives every 100 ms.
    using var en = new MMDeviceEnumerator();
    var v = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).First(d => d.FriendlyName.Contains(Arg("--virtual")!, StringComparison.OrdinalIgnoreCase));
    var c = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).First(d => d.FriendlyName.Contains(Arg("--capture")!, StringComparison.OrdinalIgnoreCase));
    using var eng = await Task.Run(() => new AudioEngine());
    await Task.Run(() => eng.ApplySettings(new AudioSettings { MonitorEnabled = false, VirtualDeviceId = v.ID }));
    using var r = new Recorder(c, loopback: false);
    r.Start();
    await Task.Delay(500);
    var at = r.Elapsed;
    eng.PlayTestTone(1000, TimeSpan.FromSeconds(2), 0.25f);
    await Task.Delay(2800);
    var a = r.Stop();
    Console.WriteLine($"{c.FriendlyName}: {a.SampleRate} Hz, tone sent at {at.TotalMilliseconds:0} ms (amp 0.25 = -12.0 dBFS)");
    foreach (var hz in new[] { 250.0, 500, 1000, 2000, 4000 })
        Console.WriteLine($"  {hz,5} Hz over tone window: {Analysis.Db(Analysis.Goertzel(a.Samples, (int)((at.TotalSeconds + 0.3) * a.SampleRate), a.SampleRate, hz, a.SampleRate)),7:0.0} dBFS");
    // 10 ms blocks as one character each: '#' signal, '.' silence.
    var b10 = a.SampleRate / 100;
    var line = new System.Text.StringBuilder();
    for (var t = 0; t + b10 <= a.Samples.Length; t += b10) line.Append(Analysis.Rms(a.Samples, t, b10) > 0.003 ? '#' : '.');
    Console.WriteLine("  10 ms blocks: " + line);
    Console.WriteLine($"  dominant frequency in signal: {Analysis.DominantFrequency(a.Samples, (int)((at.TotalSeconds + 0.6) * a.SampleRate), a.SampleRate / 2, a.SampleRate, 300, 3000):0.0} Hz");
    return 0;
}

if (argv.FirstOrDefault() is not "check")
{
    using var en = new MMDeviceEnumerator();
    foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
    {
        Console.WriteLine(flow == DataFlow.Render ? "RENDER (outputs)" : "CAPTURE (inputs)");
        foreach (var d in RoutingAdvisor.List(en, flow))
        {
            var format = "";
            if (d.State == "Active")
            {
                try { var f = en.GetDevice(d.Id).AudioClient.MixFormat; format = $"  {f.SampleRate} Hz {f.Channels} ch {f.BitsPerSample}-bit"; }
                catch { /* format unavailable */ }
            }
            Console.WriteLine($"  [{d.State,-9}] {d.Name}   (adapter: {d.Adapter}){format}");
        }
    }
    return 0;
}

var results = new List<(string Name, bool? Pass, string Detail)>();
void Report(string name, bool? pass, string detail)
{
    results.Add((name, pass, detail));
    Console.WriteLine($"{(pass is null ? "INFO" : pass.Value ? "PASS" : "FAIL"),-5} {name}: {detail}");
}

using var e = new MMDeviceEnumerator();
MMDevice Find(DataFlow flow, string? part) =>
    e.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault(d => part is not null && d.FriendlyName.Contains(part, StringComparison.OrdinalIgnoreCase))
    ?? throw new ArgumentException($"No active {flow} device matching '{part}'. Run with 'devices' to list them.");

var virtualDev = Find(DataFlow.Render, Arg("--virtual"));
// "--capture loopback" records the virtual render endpoint itself: everything GameDeck produces,
// minus the cable driver. Use it when no loopback cable is installed.
var useLoopback = Arg("--capture") == "loopback";
var captureDev = useLoopback ? virtualDev : Find(DataFlow.Capture, Arg("--capture"));
Recorder CallRecorder() => new(captureDev, loopback: useLoopback);
// For the feedback-guard check: a recording endpoint on the virtual output's own adapter.
var guardCapture = useLoopback
    ? e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(c => AudioEngine.SameAdapter(AudioEngine.SafeAdapter(c), AudioEngine.SafeAdapter(virtualDev)))
    : captureDev;
var micDev = Find(DataFlow.Capture, Arg("--mic"));
var monitorDev = Arg("--monitor") is { } m ? Find(DataFlow.Render, m) : null;
var seconds = int.Parse(Arg("--seconds") ?? "30");
Console.WriteLine($"virtual : {virtualDev.FriendlyName}\ncapture : {captureDev.FriendlyName}\nmic     : {micDev.FriendlyName}\nmonitor : {monitorDev?.FriendlyName ?? "(skipped)"}\n");

var settings = new AudioSettings
{
    MonitorEnabled = false, // enabled only in the isolation step, on an inaudible device
    VirtualDeviceId = virtualDev.ID,
    VirtualVolume = 1f,
    MicDeviceId = micDev.ID,
    MicEnabled = false,
    MicVolume = 1f,
    LatencyMs = 40,
};
using var engine = await Task.Run(() => new AudioEngine());
void Apply() => engine.ApplySettings(settings);
await Task.Run(Apply);

// ---------------------------------------------------------------- 1. the cable carries our output
var own = await RouteSelfTest.RunOnRenderAsync(engine, virtualDev.ID);
Report("GameDeck output present on the virtual render endpoint (WASAPI loopback)", own.Detected, own.Message);
var route = useLoopback ? own : await RouteSelfTest.RunAsync(engine, captureDev.ID);
if (useLoopback) Report("Route through a cable driver", null, "not tested: recording the virtual render endpoint directly (--capture loopback)");
else
{
    Report("Route: virtual output -> capture endpoint", route.Detected, route.Message);
    if (route.Detected) Report("Pitch preserved through the cable", route.PitchOk, $"1000 Hz sent, {route.FrequencyHz:0.0} Hz received");
}
// Measure the soundboard tone at the frequency that actually arrives.
var toneHz = route.FrequencyHz > 0 ? route.FrequencyHz : 1000;
if (!route.Detected)
{
    Console.WriteLine("\nThe capture endpoint does not receive the virtual output; remaining checks would be meaningless.");
    return 1;
}

// ---------------------------------------------------------------- 2. mic only, continuity
settings.MicEnabled = true;
await Task.Run(Apply);
var st = engine.GetStatus();
Report("Mic opens and attaches to the virtual bus", st.Mic.Ok && engine.MicFlowing, st.Mic.Error ?? st.Mic.DeviceName ?? "");
await Task.Delay(500);
var micStart = engine.GetMicStats();

var (cable, direct) = await RecordBoth(CallRecorder(), new Recorder(micDev, loopback: false), TimeSpan.FromSeconds(seconds));
var micStats = engine.GetMicStats();
var cableBlocks = Blocks(cable);
var directBlocks = Blocks(direct);
var directDb = Analysis.Db(Median(directBlocks));
var cableDb = Analysis.Db(Median(cableBlocks.Skip(50)));
var micIsSilent = directBlocks.All(v => v == 0);
Report("Physical mic delivers signal", micIsSilent ? false : true,
    micIsSilent ? "Mic returned digital silence (muted?). Unmute it and rerun." : $"median {directDb:0.0} dBFS (room/voice)");

var zeroRuns = CountZeroBlocks(cable, skipMs: 500);
Report($"Mic continuous on the cable for {seconds}s with no soundboard audio",
    !micIsSilent && zeroRuns == 0 && micStats.Underruns - micStart.Underruns == 0,
    $"median {cableDb:0.0} dBFS on '{captureDev.FriendlyName}', {zeroRuns} silent 10 ms blocks, " +
    $"{micStats.Underruns - micStart.Underruns} underruns, {micStats.Drops - micStart.Drops} drops, buffer {micStats.BufferedMs:0} ms");

var (corr, lagMs) = BestEnvelopeCorrelation(directBlocks, cableBlocks, maxLagBlocks: 30);
Report("Cable signal is the mic (envelope correlation)", corr > 0.6 ? true : corr > 0.3 ? null : false,
    $"r = {corr:0.00} at {lagMs:0} ms lag{(corr <= 0.6 ? " (quiet rooms give low r; speak during the test for a strong result)" : "")}");

// ---------------------------------------------------------------- 3+4. mic + soundboard, independent volumes
var tonePath = Path.Combine(Path.GetTempPath(), "gamedeck-hwcheck-tone.wav");
File.WriteAllBytes(tonePath, Wav.Sine(14, 1000, 0.3));
var phases = new (string Name, Action Set)[]
{
    ("mic only (baseline)", () => { }),
    ("mic + sound", () => { }),
    ("mic gain 50%", () => settings.MicVolume = 0.5f),
    ("mic 100%, sound->call 50%", () => { settings.MicVolume = 1f; settings.VirtualVolume = 0.5f; }),
    ("sound->call 100%, headphone volume 20%", () => { settings.VirtualVolume = 1f; settings.MonitorVolume = 0.2f; }),
};
var phaseLen = TimeSpan.FromSeconds(2.5);
var rec = CallRecorder();
var micRec = new Recorder(micDev, loopback: false);
rec.Start();
micRec.Start();
var marks = new List<TimeSpan>();
for (var p = 0; p < phases.Length; p++)
{
    phases[p].Set();
    await Task.Run(Apply);
    if (p == 1) await engine.TriggerAsync(new PlayRequest("tone", tonePath, 1f, false, PlayMode.Restart)); // the real soundboard path
    marks.Add(rec.Elapsed);
    await Task.Delay(phaseLen);
}
marks.Add(rec.Elapsed);
var mix = rec.Stop();
var micMix = micRec.Stop();
rec.Dispose();
micRec.Dispose();
engine.StopAll();

var rows = new List<(string Name, double ToneDb, double ResidualDb, double MicDb)>();
for (var p = 0; p < phases.Length; p++)
{
    // middle 1.5 s of each phase, away from gain ramps and onsets
    var from = (int)((marks[p].TotalSeconds + 0.6) * mix.SampleRate);
    var len = (int)(1.5 * mix.SampleRate);
    var tone = Analysis.Goertzel(mix.Samples, from, len, toneHz, mix.SampleRate);
    var total = Analysis.Rms(mix.Samples, from, len);
    var residual = Math.Sqrt(Math.Max(0, total * total - tone * tone / 2));
    var mf = (int)((marks[p].TotalSeconds + 0.6) * micMix.SampleRate);
    var micLevel = Analysis.Rms(micMix.Samples, mf, (int)(1.5 * micMix.SampleRate));
    rows.Add((phases[p].Name, Analysis.Db(tone), Analysis.Db(residual), Analysis.Db(micLevel)));
}
Console.WriteLine($"\n  phase                                    sound@{toneHz:0}Hz  mic on cable   mic direct   (dBFS)");
foreach (var r in rows) Console.WriteLine($"  {r.Name,-40} {r.ToneDb,9:0.0} {r.ResidualDb,13:0.0} {r.MicDb,12:0.0}");
Console.WriteLine();

// Mic on cable relative to the mic itself cancels out room-level changes between phases.
double MicRel(int i) => rows[i].ResidualDb - rows[i].MicDb;
// Separating "tone" from "mic" relies on the tone staying a clean sine. A cable that corrupts the
// format smears it into other frequencies, so those numbers are not valid measurements.
var separable = useLoopback || route.PitchOk;
void Mix(string name, bool pass, string detail) =>
    Report(name, separable ? pass : null, separable ? detail : $"not measurable through this cable (it corrupts the signal); measure with --capture loopback. Raw: {detail}");
Mix("Soundboard audio reaches the call", rows[1].ToneDb > -20, $"{rows[1].ToneDb:0.0} dBFS (expected about -10.5 dBFS for a 0.3 sine)");
Mix("Mic not muted/replaced while a sound plays", Math.Abs(MicRel(1) - MicRel(0)) < 3, $"mic-on-cable relative level changed {MicRel(1) - MicRel(0):+0.0;-0.0} dB");
Mix("Mic gain 50% lowers only the mic", Math.Abs(MicRel(2) - MicRel(1) + 6) < 2.5 && Math.Abs(rows[2].ToneDb - rows[1].ToneDb) < 1,
    $"mic {MicRel(2) - MicRel(1):+0.0;-0.0} dB (expect -6), sound {rows[2].ToneDb - rows[1].ToneDb:+0.0;-0.0} dB (expect 0)");
Mix("Sound->call 50% lowers only the sound", Math.Abs(rows[3].ToneDb - rows[1].ToneDb + 6) < 1 && Math.Abs(MicRel(3) - MicRel(1)) < 2.5,
    $"sound {rows[3].ToneDb - rows[1].ToneDb:+0.0;-0.0} dB (expect -6), mic {MicRel(3) - MicRel(1):+0.0;-0.0} dB (expect 0)");
Mix("Headphone volume does not touch the call", Math.Abs(rows[4].ToneDb - rows[1].ToneDb) < 1 && Math.Abs(MicRel(4) - MicRel(1)) < 2.5,
    $"sound {rows[4].ToneDb - rows[1].ToneDb:+0.0;-0.0} dB, mic {MicRel(4) - MicRel(1):+0.0;-0.0} dB (expect 0, 0)");

// ---------------------------------------------------------------- 5. headphone monitor isolation
settings.MonitorVolume = 1f;
if (monitorDev is not null)
{
    settings.MonitorEnabled = true;
    settings.MonitorDeviceId = monitorDev.ID;
    await Task.Run(Apply);
    await Task.Delay(300);
    var monRec = new Recorder(monitorDev, loopback: true);
    monRec.Start();
    await Task.Delay(1500);                                              // mic live, no sounds
    var soundAt = monRec.Elapsed;
    await engine.TriggerAsync(new PlayRequest("tone", tonePath, 1f, false, PlayMode.Restart));
    await Task.Delay(1500);
    var mon = monRec.Stop();
    monRec.Dispose();
    engine.StopAll();
    var quietEnd = (int)(soundAt.TotalSeconds * mon.SampleRate);
    var quiet = Analysis.Rms(mon.Samples, mon.SampleRate / 5, Math.Max(0, quietEnd - mon.SampleRate / 5));
    var from = quietEnd + mon.SampleRate / 2;
    var mtone = Analysis.Goertzel(mon.Samples, from, mon.SampleRate / 2, 1000, mon.SampleRate);
    var mtotal = Analysis.Rms(mon.Samples, from, mon.SampleRate / 2);
    var mres = Math.Sqrt(Math.Max(0, mtotal * mtotal - mtone * mtone / 2));
    Report("Mic is never in the headphones", Analysis.Db(quiet) < -90, $"headphone bus with mic live and no sounds: {Analysis.Db(quiet):0.0} dBFS");
    Report("Sounds are in the headphones exactly once", Analysis.Db(mtone) > -20 && Analysis.Db(mres) < -60,
        $"tone {Analysis.Db(mtone):0.0} dBFS, everything else {Analysis.Db(mres):0.0} dBFS");

    // monitor == virtual guard
    settings.MonitorDeviceId = virtualDev.ID;
    await Task.Run(Apply);
    var g = engine.GetStatus();
    Report("Headphones = virtual cable is refused", !g.Monitor.Ok && g.Virtual.Ok, g.Monitor.Error ?? "(no error)");
    settings.MonitorEnabled = false;
    settings.MonitorDeviceId = null;
}
else
{
    Report("Headphone isolation", null, "skipped (no --monitor device given)");
}

// ---------------------------------------------------------------- 6. feedback guard + missing device
if (guardCapture is not null)
{
    settings.MicDeviceId = guardCapture.ID;
    await Task.Run(Apply);
    var fb = engine.GetStatus();
    Report($"'{guardCapture.FriendlyName}' (same device as the output) refused as mic (feedback guard)", !fb.Mic.Ok && !engine.MicFlowing, fb.Mic.Error ?? "(no error)");
    settings.MicDeviceId = micDev.ID;
}
else
{
    Report("Feedback guard", null, "skipped (no recording endpoint shares the virtual output's adapter)");
}

settings.VirtualDeviceId = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}";
await Task.Run(Apply);
var missing = engine.GetStatus();
Report("Missing virtual device gives an actionable error", !missing.Virtual.Ok && missing.Virtual.Error is not null, missing.Virtual.Error ?? "(none)");
settings.VirtualDeviceId = virtualDev.ID;
await Task.Run(Apply);

Console.WriteLine("\nRouting advice:");
foreach (var i in RoutingAdvisor.Build(settings, engine.GetStatus(), engine.GetMicStats()).Issues) Console.WriteLine($"  [{i.Level}] {i.Message}{(i.Fix is null ? "" : "  â†’ " + i.Fix)}");

File.Delete(tonePath);
var failed = results.Count(r => r.Pass == false);
Console.WriteLine($"\n{results.Count(r => r.Pass == true)} passed, {failed} failed, {results.Count(r => r.Pass is null)} info");
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------- helpers
static async Task<(CapturedAudio Cable, CapturedAudio Mic)> RecordBoth(Recorder cable, Recorder mic, TimeSpan duration)
{
    using var a = cable;
    using var b = mic;
    a.Start();
    b.Start();
    await Task.Delay(duration);
    return (a.Stop(), b.Stop());
}

static List<double> Blocks(CapturedAudio a)
{
    var n = a.SampleRate / 100;
    var list = new List<double>();
    for (var i = 0; i + n <= a.Samples.Length; i += n) list.Add(Analysis.Rms(a.Samples, i, n));
    return list;
}

static double Median(IEnumerable<double> v)
{
    var s = v.OrderBy(x => x).ToList();
    return s.Count == 0 ? 0 : s[s.Count / 2];
}

static int CountZeroBlocks(CapturedAudio a, int skipMs)
{
    var n = a.SampleRate / 100;
    var count = 0;
    for (var i = a.SampleRate * skipMs / 1000; i + n <= a.Samples.Length; i += n)
    {
        var allZero = true;
        for (var j = i; j < i + n && allZero; j++) allZero = a.Samples[j] == 0;
        if (allZero) count++;
    }
    return count;
}

static (double R, double LagMs) BestEnvelopeCorrelation(List<double> reference, List<double> signal, int maxLagBlocks)
{
    double best = -1;
    var bestLag = 0;
    var r = reference.Select(x => Analysis.Db(x)).ToArray();
    var s = signal.Select(x => Analysis.Db(x)).ToArray();
    for (var lag = 0; lag <= maxLagBlocks; lag++)
    {
        var n = Math.Min(r.Length, s.Length - lag) - 50;
        if (n < 100) break;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        for (var i = 50; i < 50 + n; i++)
        {
            double x = r[i], y = s[i + lag];
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
        }
        var cov = sxy / n - sx / n * (sy / n);
        var den = Math.Sqrt(Math.Max(1e-12, (sxx / n - sx * sx / n / n) * (syy / n - sy * sy / n / n)));
        var c = cov / den;
        if (c > best) { best = c; bestLag = lag; }
    }
    return (best, bestLag * 10);
}

static class Wav
{
    public static byte[] Sine(double seconds, double freq, double amp)
    {
        const int rate = 48000;
        var frames = (int)(seconds * rate);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var data = frames * 4;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(data);
        for (var i = 0; i < frames; i++)
        {
            var v = (short)(amp * short.MaxValue * Math.Sin(2 * Math.PI * freq * i / rate));
            w.Write(v); w.Write(v);
        }
        w.Flush();
        return ms.ToArray();
    }
}
