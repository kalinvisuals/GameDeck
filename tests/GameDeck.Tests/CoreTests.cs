using System.Net;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Security;
using Xunit;

namespace GameDeck.Tests;

public class NetworkGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.10.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Only_local_network_addresses_are_allowed(string ip, bool expected) =>
        Assert.Equal(expected, NetworkGuard.IsLocalNetworkAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("localhost", true)]
    [InlineData("[::1]", true)]
    [InlineData("evil.example.com", false)]
    [InlineData("", false)]
    public void Host_header_rejects_rebinding_hostnames(string host, bool expected) =>
        Assert.Equal(expected, NetworkGuard.IsAllowedHost(host));
}

public class PairingServiceTests
{
    [Fact]
    public void Correct_code_succeeds_once_and_rotates()
    {
        var pairing = new PairingService();
        var (code, _) = pairing.GetCurrentCode();

        var first = pairing.TryPair(code);
        Assert.Equal(PairOutcome.Success, first.Outcome);
        Assert.False(string.IsNullOrEmpty(first.Token));
        Assert.Equal(PairOutcome.Invalid, pairing.TryPair(code).Outcome);
        Assert.NotEqual(code, pairing.GetCurrentCode().Code);
    }

    [Fact]
    public void Code_with_spaces_is_accepted()
    {
        var pairing = new PairingService();
        var code = pairing.GetCurrentCode().Code;
        Assert.Equal(PairOutcome.Success, pairing.TryPair($"{code[..3]} {code[3..]}").Outcome);
    }

    [Fact]
    public void Expired_code_is_rejected()
    {
        var now = DateTime.UtcNow;
        var pairing = new PairingService(() => now);
        var code = pairing.GetCurrentCode().Code;
        now += PairingService.CodeLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(PairOutcome.Invalid, pairing.TryPair(code).Outcome);
    }

    [Fact]
    public void Repeated_failures_lock_out_even_the_right_code()
    {
        var now = DateTime.UtcNow;
        var pairing = new PairingService(() => now);
        for (var i = 0; i < PairingService.MaxFailures - 1; i++)
            Assert.Equal(PairOutcome.Invalid, pairing.TryPair("not-it").Outcome);
        Assert.Equal(PairOutcome.LockedOut, pairing.TryPair("not-it").Outcome);

        var code = pairing.GetCurrentCode().Code;
        Assert.Equal(PairOutcome.LockedOut, pairing.TryPair(code).Outcome);

        now += PairingService.LockoutDuration + TimeSpan.FromSeconds(1);
        Assert.Equal(PairOutcome.Success, pairing.TryPair(pairing.GetCurrentCode().Code).Outcome);
    }

    [Fact]
    public void Token_hash_round_trips()
    {
        var token = PairingService.NewToken();
        var hash = PairingService.HashToken(token);
        Assert.True(PairingService.HashMatches(token, hash));
        Assert.False(PairingService.HashMatches(PairingService.NewToken(), hash));
    }
}

public class ConfigStoreTests
{
    [Fact]
    public void Fresh_store_has_a_profile_and_persists_changes()
    {
        var dir = TestAudio.TempDir();
        var store = new ConfigStore(dir);
        var profileId = store.Read(c => c.ActiveProfileId);
        Assert.NotNull(profileId);

        store.Update(c => c.Sounds.Add(new SoundButton { Id = "abc", Label = "Airhorn", FileName = "abc.wav" }));
        store.Update(c => c.Profiles[0].Layout.AddRange(["abc", "missing", "abc"]));

        var reloaded = new ConfigStore(dir);
        Assert.Equal(["abc"], reloaded.Read(c => c.Profiles[0].Layout));
        Assert.Equal("Airhorn", reloaded.Read(c => c.Sounds.Single().Label));
    }

    [Fact]
    public void Corrupt_config_is_backed_up_not_lost()
    {
        var dir = TestAudio.TempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"), "{ not json");
        var store = new ConfigStore(dir);
        Assert.Single(store.Read(c => c.Profiles));
        Assert.Contains(Directory.GetFiles(dir), f => f.Contains("config.json.broken-"));
    }

    [Fact]
    public void Deck_and_scenes_survive_an_older_version_rewriting_the_config()
    {
        var dir = TestAudio.TempDir();
        var store = new ConfigStore(dir);
        store.Update(c =>
        {
            c.Deck.Profiles[0].Name = "Gaming";
            c.Scenes.Add(new AudioScene { Id = "s1", Name = "Night" });
            c.AutoScenes = true;
        });

        // What an older build leaves behind: only the sections it knows about.
        var path = Path.Combine(dir, "config.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var key in new[] { "deck", "scenes", "activeSceneId", "autoScenes" }) json.Remove(key);
        File.WriteAllText(path, json.ToJsonString());

        var reloaded = new ConfigStore(dir);
        Assert.Equal("Gaming", reloaded.Read(c => c.Deck.Profiles[0].Name));
        Assert.Equal("Night", reloaded.Read(c => c.Scenes.Single().Name));
        Assert.True(reloaded.Read(c => c.AutoScenes));
    }

    [Fact]
    public void Deck_saved_in_the_config_wins_over_the_mirror()
    {
        var dir = TestAudio.TempDir();
        new ConfigStore(dir).Update(c => c.Deck.Profiles[0].Name = "Old");

        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Old\"", "\"New\""));

        Assert.Equal("New", new ConfigStore(dir).Read(c => c.Deck.Profiles[0].Name));
    }
}

public class AudioDecodeTests
{
    [Fact]
    public void Mono_44k_wav_is_decoded_to_48k_stereo()
    {
        var path = Path.Combine(TestAudio.TempDir(), "tone.wav");
        File.WriteAllBytes(path, TestAudio.Wav(1.0, 44100, 1, amplitude: 0.5));

        var sound = CachedSound.Decode(path);

        Assert.InRange(sound.Duration.TotalMilliseconds, 990, 1010);
        Assert.Equal(0, sound.Samples.Length % 2);
        Assert.True(sound.Samples.Max() > 0.4f);
        // Stereo duplication: left == right for a mono source.
        Assert.Equal(sound.Samples[2000], sound.Samples[2001]);
    }

    [Fact]
    public void Garbage_file_is_rejected()
    {
        var path = Path.Combine(TestAudio.TempDir(), "fake.wav");
        File.WriteAllBytes(path, new byte[1000]);
        Assert.ThrowsAny<Exception>(() => CachedSound.Decode(path));
    }
}

public class PeakMeterTests
{
    [Fact]
    public void Holds_the_max_until_taken_then_resets()
    {
        var meter = new PeakMeter();
        meter.Report([0.1f, -0.7f, 0.3f]);
        meter.Report(0.2f);
        Assert.Equal(0.7f, meter.Take(), 5);
        Assert.Equal(0f, meter.Take());
    }

    [Fact]
    public void Voice_reports_post_volume_level()
    {
        var meter = new PeakMeter();
        var voice = new Voice(new CachedSound(Enumerable.Repeat(1f, 4800).ToArray()), false, () => 0.5f, meter);
        voice.Read(new float[960], 0, 960);
        Assert.Equal(0.5f, meter.Take(), 3);
    }
}

public class VoiceTests
{
    private static CachedSound Ones(int samples) => new(Enumerable.Repeat(1f, samples).ToArray());

    [Fact]
    public void Plays_to_end_then_finishes_once()
    {
        var voice = new Voice(Ones(1000), loop: false, () => 1f);
        var finished = 0;
        voice.Finished += _ => finished++;
        var buffer = new float[960];

        Assert.Equal(960, voice.Read(buffer, 0, 960));
        Assert.Equal(40, voice.Read(buffer, 0, 960));
        Assert.Equal(0, voice.Read(buffer, 0, 960));
        Assert.Equal(1, finished);
    }

    [Fact]
    public void Loop_keeps_filling_until_stopped_with_fade()
    {
        var voice = new Voice(Ones(100), loop: true, () => 1f);
        var buffer = new float[4800];
        Assert.Equal(4800, voice.Read(buffer, 0, 4800));
        Assert.All(buffer, s => Assert.Equal(1f, s, 3));

        voice.RequestStop();
        var read = voice.Read(buffer, 0, 4800);
        Assert.True(read > 0 && read < 4800);
        Assert.True(buffer[read - 1] < 0.01f, "fade should end near silence");
        Assert.True(voice.IsFinished);
    }
}
