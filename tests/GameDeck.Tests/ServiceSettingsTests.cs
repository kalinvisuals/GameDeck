using GameDeck.App;
using GameDeck.Core.Config;
using Xunit;

namespace GameDeck.Tests;

[Collection("Localization")] // uses the process-wide language
public class ServiceSettingsTests
{
    [Theory]
    [InlineData("am-trei-randuri-de-creier_riGvZqJ.mp3", "Am trei randuri de creier")]
    [InlineData("police-airhorn.mp3", "Police airhorn")]
    [InlineData("ping-2.mp3", "Ping 2")]
    [InlineData("ataca-un-hacker.mp3", "Ataca un hacker")]
    [InlineData("my_sound_final.wav", "My sound final")]     // all-lowercase tail is a word, kept
    [InlineData("Victory.wav", "Victory")]
    [InlineData("___.wav", null)] // nothing readable → the localized "Untitled"
    public void Labels_from_file_names_are_readable(string file, string? expected) =>
        Assert.Equal(expected ?? GameDeck.Core.Localization.L.T("sound.untitled"), GameDeckService.LabelFromFileName(file));

    [Fact]
    public void Mutes_send_zero_gain_but_keep_the_stored_volumes_and_devices()
    {
        var stored = new AudioSettings
        {
            MicVolume = 0.7f, VirtualVolume = 0.9f, MonitorVolume = 0.5f,
            MicMuted = true, VirtualMuted = false, MonitorMuted = true,
            MicDeviceId = "mic", VirtualDeviceId = "cable", MicEnabled = true,
        };

        var e = GameDeckService.Effective(stored);

        Assert.Equal((0f, 0.9f, 0f), (e.MicVolume, e.VirtualVolume, e.MonitorVolume));
        Assert.Equal(("mic", "cable", true), (e.MicDeviceId, e.VirtualDeviceId, e.MicEnabled)); // nothing closes
        Assert.Equal((0.7f, 0.5f), (stored.MicVolume, stored.MonitorVolume));                  // restored on unmute
    }
}
