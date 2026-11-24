namespace GameDeck.Core.Presence;

/// <summary>Discord Rich Presence: off until the person turns it on. Kept in config.json under "presence".</summary>
public sealed class PresenceConfig
{
    public bool Enabled { get; set; }
    /// <summary>The activity (an "activity" preset) shown while <see cref="Enabled"/> is on. Null = none.</summary>
    public string? ActivePresetId { get; set; }
    /// <summary>
    /// The song (a "music" preset) shown at the same time, as a second card. Discord shows one activity
    /// per application, so songs go through a second application.
    /// </summary>
    public string? ActiveMusicPresetId { get; set; }
    public List<PresencePreset> Presets { get; set; } = new();

    /// <summary>
    /// "manual": the activity card shows the activity chosen here. "auto": GameDeck shows no activity, so
    /// the only one is the game Discord detects by itself (with its own icon). Never both at once.
    /// </summary>
    public string ActivityMode { get; set; } = "manual";

    /// <summary>
    /// "manual": the song card shows the song chosen here. "auto": it shows what plays, in GameDeck's
    /// YouTube or the programs allowed in <see cref="AutoMusic"/>, and nothing when nothing plays.
    /// </summary>
    public string MusicMode { get; set; } = "manual";

    public AutoMusicSettings AutoMusic { get; set; } = new();

    /// <summary>When each card's timer started, so it carries on after GameDeck restarts.</summary>
    public DateTimeOffset? ActivityAppliedAt { get; set; }
    public DateTimeOffset? MusicAppliedAt { get; set; }
}

/// <summary>Where the song comes from when <see cref="PresenceConfig.MusicMode"/> is "auto".</summary>
public sealed class AutoMusicSettings
{
    /// <summary>
    /// Programs whose music may show: "gamedeck-youtube" (the YouTube section of GameDeck) and keys of
    /// Windows media programs ("spotify", "applemusic", "chrome"). Music apps by default; browsers only if chosen.
    /// </summary>
    public List<string> Apps { get; set; } = [PresenceRules.GameDeckYouTube, "spotify", "applemusic", "deezer", "tidal"];
    /// <summary>Look the cover up online (Apple's public music search) when the program doesn't give one.</summary>
    public bool FindCovers { get; set; } = true;
}

/// <summary>
/// One saved activity. A "music" preset uses the same fields as an activity, shown with music
/// labels in the dashboard: Details = song, State = artist, LargeText = album, LargeImage = cover,
/// DetailsUrl = link to the song, Name = where it plays ("YouTube").
/// </summary>
public sealed class PresencePreset
{
    public string Id { get; set; } = Config.SoundButton.NewId();
    /// <summary>Name of the preset in the dashboard list (not shown on Discord).</summary>
    public string Label { get; set; } = "";
    /// <summary>"activity" or "music". See <see cref="PresenceRules.Kinds"/>.</summary>
    public string Kind { get; set; } = "activity";
    /// <summary>"playing", "listening", "watching" or "competing": the only types Discord allows from an application.</summary>
    public string Type { get; set; } = "playing";
    /// <summary>Activity name on the card ("Playing <Name>"). Empty = the application's name, GameDeck.</summary>
    public string Name { get; set; } = "";
    public string Details { get; set; } = "";
    public string DetailsUrl { get; set; } = "";
    public string State { get; set; } = "";
    public string StateUrl { get; set; } = "";
    /// <summary>Public https image address (also GIF), or empty.</summary>
    public string LargeImage { get; set; } = "";
    public string LargeText { get; set; } = "";
    public string SmallImage { get; set; } = "";
    public string SmallText { get; set; } = "";
    /// <summary>At most two; everyone except the person themself sees them.</summary>
    public List<PresenceButton> Buttons { get; set; } = new();
    /// <summary>"none", "elapsed" (since applied), "countdown" or "track" (song progress). See <see cref="PresenceRules.Timers"/>.</summary>
    public string Timer { get; set; } = "elapsed";
    public int CountdownMinutes { get; set; } = 15;
    /// <summary>Song length for the "track" timer; the song repeats when it ends.</summary>
    public int DurationSec { get; set; }
    /// <summary>Where the song starts when the preset is applied.</summary>
    public int PositionSec { get; set; }
    /// <summary>Which text the member list shows next to the person: "name", "details" or "state".</summary>
    public string StatusDisplay { get; set; } = "name";
}

public sealed class PresenceButton
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}
