using GameDeck.App;
using GameDeck.App.Web;
using GameDeck.Core.Audio;
using Xunit;

namespace GameDeck.Tests;

public class TrimTests
{
    private const int FramesPerMs = AudioEngine.SampleRate / 1000;

    /// <summary>Stereo sound whose sample value is its frame index, so a read shows where it came from.</summary>
    private static CachedSound Ramp(int ms)
    {
        var data = new float[ms * FramesPerMs * AudioEngine.Channels];
        for (var i = 0; i < data.Length; i++) data[i] = i / AudioEngine.Channels;
        return new CachedSound(data);
    }

    [Fact]
    public void Range_is_frame_aligned_and_zero_end_means_whole_file()
    {
        var sound = Ramp(1000);
        Assert.Equal((0, sound.Samples.Length), sound.Range(0, 0));
        Assert.Equal((200 * FramesPerMs * 2, 700 * FramesPerMs * 2), sound.Range(200, 700));
        Assert.Equal((200 * FramesPerMs * 2, sound.Samples.Length), sound.Range(200, 5000));
    }

    [Fact]
    public void Too_short_or_inverted_selection_plays_whole_sound()
    {
        var sound = Ramp(1000);
        Assert.Equal((0, sound.Samples.Length), sound.Range(500, 520));
        Assert.Equal((0, sound.Samples.Length), sound.Range(800, 300));
        Assert.Equal((0, sound.Samples.Length), sound.Range(2000, 0));
    }

    [Fact]
    public void Voice_plays_only_the_selection_with_edge_ramps()
    {
        var sound = Ramp(1000);
        var (start, end) = sound.Range(200, 300);
        var voice = new Voice(sound, start, end, loop: false, () => 1f);
        var buffer = new float[48000];

        var read = voice.Read(buffer, 0, buffer.Length);

        Assert.Equal(end - start, read);
        Assert.True(voice.IsFinished);
        Assert.Equal(0f, buffer[0]); // fade-in starts silent
        var mid = (end - start) / 2;
        Assert.Equal(sound.Samples[start + mid], buffer[mid], 1); // untouched in the middle
        Assert.True(buffer[read - 1] < sound.Samples[end - 1] * 0.01f, "fade-out should end near silence");
    }

    [Fact]
    public void Loop_wraps_to_selection_start()
    {
        var sound = Ramp(1000);
        var (start, end) = sound.Range(100, 200);
        var voice = new Voice(sound, start, end, loop: true, () => 1f);
        var length = end - start;
        var buffer = new float[length * 2];

        Assert.Equal(buffer.Length, voice.Read(buffer, 0, buffer.Length));
        Assert.Equal(buffer[length / 2], buffer[length + length / 2]);
        Assert.False(voice.IsFinished);
    }

    [Fact]
    public void Untrimmed_voice_is_unchanged()
    {
        var sound = Ramp(100);
        var voice = new Voice(sound, loop: false, () => 1f);
        var buffer = new float[sound.Samples.Length];
        voice.Read(buffer, 0, buffer.Length);
        Assert.Equal(sound.Samples, buffer);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 3000, 0, 0)]       // full length is stored as untrimmed
    [InlineData(250, 0, 250, 0)]
    [InlineData(250, 9999, 250, 0)]
    [InlineData(-40, 1200, 0, 1200)]
    [InlineData(500, 2500, 500, 2500)]
    public void CleanTrim_clamps_to_the_file(long start, long end, long expectedStart, long expectedEnd)
    {
        Assert.Equal((expectedStart, expectedEnd), GameDeckService.CleanTrim(start, end, 3000));
    }

    [Fact]
    public void CleanTrim_rejects_a_selection_shorter_than_the_minimum()
    {
        var ex = Assert.Throws<ApiException>(() => GameDeckService.CleanTrim(1000, 1020, 3000));
        Assert.Equal(400, ex.Status);
    }
}
