namespace GameDeck.Core.Audio;

/// <summary>
/// Lock-free peak hold, written from audio threads and drained by the UI pusher.
/// <see cref="Take"/> returns the highest absolute sample since the previous call.
/// </summary>
public sealed class PeakMeter
{
    private int _bits;

    public void Report(float peak)
    {
        if (peak <= 0) return;
        while (true)
        {
            var current = Volatile.Read(ref _bits);
            if (BitConverter.Int32BitsToSingle(current) >= peak) return;
            if (Interlocked.CompareExchange(ref _bits, BitConverter.SingleToInt32Bits(peak), current) == current) return;
        }
    }

    public void Report(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        Report(peak);
    }

    public float Take() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _bits, 0));
}
