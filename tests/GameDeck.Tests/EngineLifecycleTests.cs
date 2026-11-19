using System.Diagnostics;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using Xunit;

namespace GameDeck.Tests;

/// <summary>
/// Playback lifecycle on the real WASAPI engine and the default Windows output device.
/// Monitor volume is 0 and the clips are digital silence, so nothing is audible.
/// </summary>
public sealed class EngineLifecycleTests : IAsyncLifetime
{
    private AudioEngine _engine = null!;
    private string _dir = "";

    public async Task InitializeAsync()
    {
        _dir = TestAudio.TempDir();
        _engine = await Task.Run(() => new AudioEngine());
        await Task.Run(() => _engine.ApplySettings(new AudioSettings { MonitorEnabled = true, MonitorVolume = 0f }));
        Assert.True(_engine.GetStatus().Monitor.Ok, "Default output device must open: " + _engine.GetStatus().Monitor);
    }

    public async Task DisposeAsync() => await Task.Run(_engine.Dispose);

    private string Clip(string name, double seconds)
    {
        var path = Path.Combine(_dir, name + ".wav");
        File.WriteAllBytes(path, TestAudio.Wav(seconds, 48000, 2));
        return path;
    }

    private Task<TriggerResult> Play(string id, string path, PlayMode mode = PlayMode.Restart, bool loop = false) =>
        _engine.TriggerAsync(new PlayRequest(id, path, 1f, loop, mode));

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new Xunit.Sdk.XunitException($"Timed out after {timeoutMs} ms waiting for: {what}");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The mixer detaches a finished voice on its next audio callback (â‰¤ one ~40 ms buffer), so allow
    /// a short, bounded settle time; anything still attached after that is a real leak.
    /// </summary>
    private async Task AssertFullyReleased()
    {
        try
        {
            await WaitUntil(() => _engine.Counters() == (0, 0, 0, 0, 0), 250, "all voices detached from the mixer");
        }
        catch (Xunit.Sdk.XunitException) { /* fall through to the precise assertions below */ }
        var c = _engine.Counters();
        Assert.Equal(0, c.Instances);
        Assert.Equal(0, c.MonitorVoices);
        Assert.Equal(0, c.MonitorMixerInputs);
        Assert.Equal(0, c.VirtualVoices);
        Assert.True(_engine.IsIdle);
        Assert.Empty(_engine.GetPlaying());
    }

    [Fact]
    public async Task Sound_stops_by_itself_at_end_of_file_and_releases_everything()
    {
        var path = Clip("short", 0.4);
        await _engine.PreloadAsync(path);
        var changes = 0;
        _engine.PlayingChanged += () => Interlocked.Increment(ref changes);

        var sw = Stopwatch.StartNew();
        Assert.Equal(TriggerResult.Started, await Play("a", path));
        var c = _engine.Counters();
        Assert.Equal((1, 1, 1), (c.Instances, c.MonitorVoices, c.MonitorMixerInputs));
        Assert.False(_engine.IsIdle);

        await WaitUntil(() => _engine.GetPlaying().Count == 0, 3000, "natural end of playback");
        sw.Stop();
        await WaitUntil(() => _engine.Counters().Instances == 0, 500, "instance cleanup");

        Assert.InRange(sw.ElapsedMilliseconds, 350, 1200); // ended at the file's end, not early, not stuck
        await AssertFullyReleased();
        Assert.True(changes >= 2, "start and end must both be broadcast");
    }

    [Fact]
    public async Task Pressing_again_restarts_from_the_beginning_by_default()
    {
        Assert.Equal(PlayMode.Restart, new SoundButton().Mode);
        Assert.False(new SoundButton().Loop);

        var path = Clip("long", 3.0);
        await _engine.PreloadAsync(path);
        await Play("a", path);
        await Task.Delay(600);
        Assert.True(_engine.GetPlaying().Single().ElapsedMs >= 500);

        Assert.Equal(TriggerResult.Started, await Play("a", path));
        var playing = _engine.GetPlaying();
        Assert.Single(playing);                       // replaced, not layered
        Assert.True(playing[0].ElapsedMs < 200, $"restarted copy should be near 0 ms, was {playing[0].ElapsedMs}");
        await WaitUntil(() => _engine.Counters().Instances == 1, 500, "old copy faded out and released");

        _engine.StopAll();
        await WaitUntil(() => _engine.Counters().Instances == 0, 500, "stop all");
        await AssertFullyReleased();
    }

    [Fact]
    public async Task Different_sounds_play_simultaneously()
    {
        var a = Clip("a", 2.0);
        var b = Clip("b", 2.0);
        var c = Clip("c", 2.0);
        await Play("a", a);
        await Play("b", b);
        await Play("c", c);

        Assert.Equal(["a", "b", "c"], _engine.GetPlaying().Select(p => p.SoundId).Order());
        Assert.Equal(3, _engine.Counters().MonitorMixerInputs);

        _engine.StopAll();
        await WaitUntil(() => _engine.Counters().Instances == 0, 500, "stop all");
        await AssertFullyReleased();
    }

    [Fact]
    public async Task Stop_all_stops_everything_immediately()
    {
        var path = Clip("long", 5.0);
        await Play("a", path);
        await Play("b", path, PlayMode.Overlap);
        await Play("b", path, PlayMode.Overlap);
        Assert.Equal(3, _engine.GetPlaying().Count);

        var sw = Stopwatch.StartNew();
        _engine.StopAll();
        await WaitUntil(() => _engine.Counters().Instances == 0, 1000, "everything released after stop all");
        Assert.True(sw.ElapsedMilliseconds < 300, $"stop all took {sw.ElapsedMilliseconds} ms");
        await AssertFullyReleased();
    }

    [Fact]
    public async Task Loop_is_opt_in_and_stops_only_when_asked()
    {
        var path = Clip("blip", 0.2);
        await Play("once", path, loop: false);
        await Play("looped", path, loop: true);

        await Task.Delay(900); // more than four clip lengths
        Assert.Equal(["looped"], _engine.GetPlaying().Select(p => p.SoundId));

        _engine.Stop("looped");
        await WaitUntil(() => _engine.Counters().Instances == 0, 500, "looped sound released after stop");
        await AssertFullyReleased();
    }

    [Fact]
    public async Task Toggle_mode_is_still_available_per_button()
    {
        var path = Clip("long", 3.0);
        Assert.Equal(TriggerResult.Started, await Play("t", path, PlayMode.Toggle));
        Assert.Equal(TriggerResult.Stopped, await Play("t", path, PlayMode.Toggle));
        await WaitUntil(() => _engine.Counters().Instances == 0, 500, "toggled off");
        await AssertFullyReleased();
    }
}
