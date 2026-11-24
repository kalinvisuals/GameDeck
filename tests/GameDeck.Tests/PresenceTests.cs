using GameDeck.Core.Config;
using GameDeck.Core.Presence;
using Xunit;

namespace GameDeck.Tests;

public class PresenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Music_presets_are_always_listening_with_the_song_progress()
    {
        var p = new PresencePreset { Kind = "music", Type = "playing", Name = "YouTube", Details = "Song", State = "Artist", LargeText = "Album", DurationSec = 200, Timer = "track" };
        PresenceRules.Clean(p);
        var (a, next) = PresenceRules.Build(p, T0, T0.AddSeconds(10));

        Assert.Equal(2, (int)a["type"]!);
        Assert.Equal("YouTube", (string)a["name"]!);
        Assert.Equal(T0.ToUnixTimeMilliseconds(), (long)a["timestamps"]!["start"]!);
        Assert.Equal(T0.AddSeconds(200).ToUnixTimeMilliseconds(), (long)a["timestamps"]!["end"]!);
        Assert.Equal(T0.AddSeconds(200), next);
    }

    [Fact]
    public void A_song_repeats_after_it_ends()
    {
        var p = new PresencePreset { Kind = "music", Details = "Song", DurationSec = 100, PositionSec = 30, Timer = "track" };
        PresenceRules.Clean(p);
        // Applied at 0:30 into the song; 250 s later it is the third round.
        var (a, next) = PresenceRules.Build(p, T0, T0.AddSeconds(250));
        var first = T0.AddSeconds(-30);
        Assert.Equal(first.AddSeconds(200).ToUnixTimeMilliseconds(), (long)a["timestamps"]!["start"]!);
        Assert.Equal(first.AddSeconds(300), next);
    }

    [Fact]
    public void A_countdown_disappears_when_it_reaches_zero()
    {
        var p = new PresencePreset { Details = "Back soon", Timer = "countdown", CountdownMinutes = 15 };
        PresenceRules.Clean(p);
        var (running, next) = PresenceRules.Build(p, T0, T0.AddMinutes(5));
        Assert.Equal(T0.AddMinutes(15).ToUnixTimeMilliseconds(), (long)running["timestamps"]!["end"]!);
        Assert.Equal(T0.AddMinutes(15), next);

        var (ended, after) = PresenceRules.Build(p, T0, T0.AddMinutes(16));
        Assert.False(ended.ContainsKey("timestamps"));
        Assert.Null(after);
    }

    [Fact]
    public void Cleaning_keeps_only_what_Discord_accepts()
    {
        var p = new PresencePreset
        {
            Type = "streaming",
            Details = new string('x', 300),
            LargeImage = "javascript:alert(1)",
            SmallImage = "https://example.com/a.png",
            Buttons =
            [
                new() { Label = "One", Url = "https://a.example" },
                new() { Label = "No link", Url = "" },
                new() { Label = new string('b', 50), Url = "https://b.example" },
                new() { Label = "Three", Url = "https://c.example" },
            ],
            Timer = "track", // no song length: falls back to elapsed
            StatusDisplay = "everything",
        };
        PresenceRules.Clean(p);

        Assert.Equal("playing", p.Type);
        Assert.Equal(PresenceRules.MaxText, p.Details.Length);
        Assert.Equal("", p.LargeImage);
        Assert.Equal("https://example.com/a.png", p.SmallImage);
        Assert.Equal(2, p.Buttons.Count);
        Assert.Equal(PresenceRules.MaxButtonLabel, p.Buttons[1].Label.Length);
        Assert.Equal("elapsed", p.Timer);
        Assert.Equal("name", p.StatusDisplay);
    }

    [Fact]
    public void One_letter_texts_are_padded_and_member_list_falls_back_to_the_name()
    {
        var p = new PresencePreset { Details = "A", StatusDisplay = "state", Timer = "none" };
        PresenceRules.Clean(p);
        var (a, _) = PresenceRules.Build(p, T0, T0);
        Assert.Equal(2, ((string)a["details"]!).Length);
        Assert.Equal(0, (int)a["status_display_type"]!); // no second line to show
        Assert.False(a.ContainsKey("timestamps"));
        Assert.False(a.ContainsKey("name")); // Discord then shows the application name, GameDeck
    }

    [Fact]
    public void A_song_and_an_activity_have_their_own_cards()
    {
        var c = new PresenceConfig
        {
            Presets =
            [
                new PresencePreset { Id = "game", Kind = "activity", Details = "In a match" },
                new PresencePreset { Id = "song", Kind = "music", Details = "Song", DurationSec = 100 },
            ],
            // A song in the activity slot (saved before songs had their own card) moves to the song card.
            ActivePresetId = "song",
        };
        PresenceRules.Normalize(c);
        Assert.Null(c.ActivePresetId);
        Assert.Equal("song", c.ActiveMusicPresetId);

        c.ActivePresetId = "game";
        c.ActiveMusicPresetId = "game"; // an activity is never the song
        PresenceRules.Normalize(c);
        Assert.Equal("game", c.ActivePresetId);
        Assert.Null(c.ActiveMusicPresetId);
    }

    [Fact]
    public void Presence_is_off_by_default_and_survives_a_reload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gd-presence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(dir);
            Assert.False(store.Read(c => c.Presence.Enabled));
            store.Update(c =>
            {
                c.Presence.Presets.Add(new PresencePreset { Id = "p1", Kind = "music", Details = "Song", DurationSec = 120 });
                c.Presence.ActiveMusicPresetId = "p1";
                c.Presence.Enabled = true;
            });

            var reloaded = new ConfigStore(dir);
            Assert.True(reloaded.Read(c => c.Presence.Enabled));
            Assert.Equal("p1", reloaded.Read(c => c.Presence.ActiveMusicPresetId));
            Assert.Equal("listening", reloaded.Read(c => c.Presence.Presets[0].Type));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

public class SongLookupTests
{
    [Theory]
    [InlineData("Never Gonna Give You Up (Official Video) (4K Remaster)", "Never Gonna Give You Up")]
    [InlineData("Song [Official Music Video]", "Song")]
    [InlineData("Song (Lyrics)", "Song")]
    [InlineData("Song (feat. Someone)", "Song (feat. Someone)")]
    public void Video_labels_are_dropped_from_titles(string title, string expected) =>
        Assert.Equal(expected, GameDeck.App.Presence.SongLookup.CleanTitle(title));

    [Theory]
    [InlineData("https://example.com/song")]
    [InlineData("http://open.spotify.com/track/4PTG3Z6ehGkBFwjybzWkR8")]
    [InlineData("https://open.spotify.com.evil.example/track/4PTG3Z6ehGkBFwjybzWkR8")]
    [InlineData("not a link")]
    public async Task Only_https_links_of_the_known_services_are_looked_up(string link)
    {
        var ex = await Assert.ThrowsAsync<GameDeck.App.Web.ApiException>(() => GameDeck.App.Presence.SongLookup.LookupAsync(link));
        Assert.Equal(400, ex.Status);
    }
}

public class PresenceStepTwoTests
{
    [Theory]
    [InlineData("Spotify.exe", "spotify")]
    [InlineData("Chrome", "chrome")]
    [InlineData("MSEdge", "msedge")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "applemusic")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "mediaplayer")]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", "vlc")]
    [InlineData("", "")]
    public void Media_programs_get_stable_keys(string aumid, string key) =>
        Assert.Equal(key, GameDeck.App.Presence.MediaSessions.AppKey(aumid));

    [Fact]
    public void Both_cards_start_manual_and_music_apps_default_to_music_players()
    {
        var c = new GameDeck.Core.Presence.PresenceConfig();
        Assert.Equal("manual", c.ActivityMode);
        Assert.Equal("manual", c.MusicMode);
        Assert.Contains("gamedeck-youtube", c.AutoMusic.Apps);
        Assert.DoesNotContain("chrome", c.AutoMusic.Apps); // browsers only when chosen

        c.ActivityMode = "something";
        c.MusicMode = "auto";
        c.AutoMusic.Apps = ["Spotify", "spotify", " ", "Chrome"];
        GameDeck.Core.Presence.PresenceRules.Normalize(c);
        Assert.Equal("manual", c.ActivityMode);
        Assert.Equal("auto", c.MusicMode);
        Assert.Equal(["spotify", "chrome"], c.AutoMusic.Apps);
    }

    [Theory]
    [InlineData("activityMode", null, true)]
    [InlineData("musicMode", null, true)]
    [InlineData("show", "abc123def456", true)]
    [InlineData("show", null, false)]
    [InlineData("hideActivity", null, true)]
    [InlineData("hideSong", null, true)]
    [InlineData("toggle", null, true)]
    [InlineData("explode", null, false)]
    public void Discord_deck_buttons_are_validated(string mode, string? target, bool ok)
    {
        var action = new GameDeck.Core.Config.DeckAction { Type = "discord", Mode = mode, Target = target };
        if (ok)
        {
            var clean = GameDeck.Core.Deck.DeckRules.CleanAction(action, inMacro: false, "");
            Assert.Equal(mode, clean.Mode);
            Assert.Equal(mode == "show" ? target : null, clean.Target);
        }
        else Assert.Throws<GameDeck.Core.Deck.DeckException>(() => GameDeck.Core.Deck.DeckRules.CleanAction(action, inMacro: false, ""));
    }
}

public class VideoTitleTests
{
    [Theory]
    [InlineData("Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)", "Rick Astley", "Rick Astley", "Never Gonna Give You Up")]
    [InlineData("Never Gonna Give You Up", "RickAstleyVEVO", "RickAstley", "Never Gonna Give You Up")]
    [InlineData("Flowers", "Miley Cyrus - Topic", "Miley Cyrus", "Flowers")]
    public void Browser_videos_become_artist_and_song(string title, string channel, string artist, string song) =>
        Assert.Equal((artist, song), GameDeck.App.Presence.SongLookup.SplitVideoTitle(title, channel));
}

public class PresenceRestartTests
{
    [Fact]
    public void A_card_timer_carries_on_after_GameDeck_restarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gd-presence-timer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(dir);
            // Presence stays off, so nothing connects to Discord during the test.
            store.Update(c =>
            {
                c.Presence.Presets.Add(new PresencePreset { Id = "p1", Details = "Back soon", Timer = "countdown" });
                c.Presence.ActivePresetId = "p1";
            });
            var first = new GameDeck.App.Presence.PresenceService(store);
            first.RestartTimer("activity");
            var started = first.Snapshot().AppliedAt;
            Assert.NotNull(started);

            Thread.Sleep(30);
            var afterRestart = new GameDeck.App.Presence.PresenceService(new ConfigStore(dir));
            afterRestart.Start();
            Assert.Equal(started, afterRestart.Snapshot().AppliedAt);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
