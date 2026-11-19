using GameDeck.Core.Audio;
using NAudio.Wave;
using Xunit;

namespace GameDeck.Tests;

public class JitterBufferTests
{
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
    private static byte[] Ms(int ms, float value = 0.5f)
    {
        var samples = 48 * ms;
        var bytes = new byte[samples * 4];
        for (var i = 0; i < samples; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), value);
        return bytes;
    }

    private static MicInput.JitterBuffer Make() => new(Format, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(150));

    [Fact]
    public void Outputs_silence_until_the_target_cushion_is_filled_then_plays_continuously()
    {
        var jb = Make();
        var out10 = new byte[48 * 10 * 4];

        jb.Write(Ms(20), Ms(20).Length);
        jb.Read(out10, 0, out10.Length);
        Assert.All(out10, b => Assert.Equal(0, b)); // still priming

        jb.Write(Ms(20), Ms(20).Length);               // 40 ms buffered >= 30 ms target
        jb.Read(out10, 0, out10.Length);
        Assert.Equal(0.5f, BitConverter.ToSingle(out10, 0));
        Assert.Equal(0.5f, BitConverter.ToSingle(out10, out10.Length - 4));
        Assert.Equal(0, jb.Stats.Underruns);
    }

    [Fact]
    public void Underrun_is_counted_once_and_reprimes_instead_of_chopping()
    {
        var jb = Make();
        jb.Write(Ms(40), Ms(40).Length);
        var out50 = new byte[48 * 50 * 4];
        jb.Read(out50, 0, out50.Length);                // asks for more than buffered
        Assert.Equal(1, jb.Stats.Underruns);

        jb.Write(Ms(10), Ms(10).Length);                // below target: must keep waiting, not trickle
        var out5 = new byte[48 * 5 * 4];
        jb.Read(out5, 0, out5.Length);
        Assert.All(out5, b => Assert.Equal(0, b));
        Assert.Equal(1, jb.Stats.Underruns);
    }

    [Fact]
    public void Backlog_over_the_maximum_is_trimmed_back_to_the_target()
    {
        var jb = Make();
        for (var i = 0; i < 20; i++) jb.Write(Ms(10), Ms(10).Length); // 200 ms > 150 ms max
        Assert.True(jb.Stats.Drops >= 1);
        Assert.InRange(jb.Stats.BufferedMs, 25, 160);
    }
}

public class FeedbackGuardTests
{
    [Theory]
    [InlineData("VB-Audio Virtual Cable", "VB-Audio Virtual Cable", true)]
    [InlineData("Steam Streaming Microphone", "Steam Streaming Microphone", true)]
    [InlineData("USB Mic", "USB Mic", true)]   // headset's own mic into its own output
    [InlineData("USB Mic", "VB-Audio Virtual Cable", false)]
    [InlineData("", "", false)]
    public void Mic_on_the_same_adapter_as_the_virtual_output_is_refused(string mic, string output, bool refused) =>
        Assert.Equal(refused, AudioEngine.SameAdapter(mic, output));
}
