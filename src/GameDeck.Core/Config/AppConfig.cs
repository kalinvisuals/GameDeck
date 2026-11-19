using System.Text.Json.Serialization;

namespace GameDeck.Core.Config;

/// <summary>Root of the persisted configuration (config.json).</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public ServerSettings Server { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();

    /// <summary>Sound library. Profiles reference these by id.</summary>
    public List<SoundButton> Sounds { get; set; } = new();
    public List<Profile> Profiles { get; set; } = new();
    public string? ActiveProfileId { get; set; }
    public List<PairedDevice> PairedDevices { get; set; } = new();

    /// <summary>Smart Audio Scenes: saved mixer and application volumes, applied on demand.</summary>
    public List<AudioScene> Scenes { get; set; } = new();
    /// <summary>The scene applied last, shown as active. Cleared when it is deleted.</summary>
    public string? ActiveSceneId { get; set; }
    /// <summary>Global switch for applying scenes when their application comes to the front. Off by default.</summary>
    public bool AutoScenes { get; set; }

    /// <summary>Smart Deck: button layouts and macros. Separate from the soundboard profiles above.</summary>
    public DeckConfig Deck { get; set; } = new();
}

public sealed class DeckConfig
{
    public List<DeckProfile> Profiles { get; set; } = new();
    public string? ActiveProfileId { get; set; }
    public List<DeckMacro> Macros { get; set; } = new();
    /// <summary>
    /// Two-state buttons currently in their second state (the next press runs ToggleAction). Kept apart
    /// from the layout so saving a profile from one device never resets a toggle flipped on another.
    /// </summary>
    public List<string> Toggled { get; set; } = new();
}

/// <summary>One Smart Deck layout ("Gaming", "Streaming"): a grid size and its pages.</summary>
public sealed class DeckProfile
{
    public string Id { get; set; } = SoundButton.NewId();
    public string Name { get; set; } = "Deck";
    public int Rows { get; set; } = 3;
    public int Columns { get; set; } = 5;
    public List<DeckPage> Pages { get; set; } = new();
}

public sealed class DeckPage
{
    public string Id { get; set; } = SoundButton.NewId();
    public string Name { get; set; } = "";
    public List<DeckButton> Buttons { get; set; } = new();
}

public sealed class DeckButton
{
    public string Id { get; set; } = SoundButton.NewId();
    /// <summary>Position in the grid, row by row: row * Columns + column. Slots without a button are empty.</summary>
    public int Slot { get; set; }
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "zap";
    public string Color { get; set; } = "#a9d6ff";
    /// <summary>File name inside the deck-images directory (still or animated); null = icon only.</summary>
    public string? Image { get; set; }
    public DeckAction Action { get; set; } = new();

    // Appearance while the button's live state is active (mic muted, scene active, sound playing,
    // macro running, app muted). Null = same as the normal appearance.
    public string? ActiveImage { get; set; }
    public string? ActiveIcon { get; set; }
    public string? ActiveColor { get; set; }

    /// <summary>"loop", "active" (animate only while active) or "off" (first frame only). See DeckRules.AnimationModes.</summary>
    public string Animation { get; set; } = "loop";

    /// <summary>Sensor buttons: the value turns amber from WarnAt and red from CriticalAt (in the sensor's unit).</summary>
    public float? WarnAt { get; set; }
    public float? CriticalAt { get; set; }

    /// <summary>Runs instead of <see cref="Action"/> when the button is held down (long press). Null = none.</summary>
    public DeckAction? HoldAction { get; set; }

    /// <summary>
    /// Makes a two-state button: presses alternate between <see cref="Action"/> and this one. Which one
    /// runs next is kept in <see cref="DeckConfig.Toggled"/>. Null = an ordinary button.
    /// </summary>
    public DeckAction? ToggleAction { get; set; }
}

/// <summary>
/// What a button (or one macro step) does. Only the fields its <see cref="Type"/> uses are kept;
/// see DeckRules for the types and their validation. There is deliberately no "run a command" type.
/// </summary>
public sealed class DeckAction
{
    public string Type { get; set; } = "none";
    /// <summary>Path, URL, sound id, scene id, application key or macro id, depending on the type.</summary>
    public string? Target { get; set; }
    /// <summary>Keyboard combination for "keys", e.g. "Ctrl+Shift+S".</summary>
    public string? Keys { get; set; }
    /// <summary>"mic", "virtual" or "monitor" for "mute" and "volume".</summary>
    public string? Channel { get; set; }
    public string? Mode { get; set; }
    public float? Value { get; set; }
    /// <summary>Sensor id for a "sensor" widget, e.g. "cpu.usage".</summary>
    public string? Sensor { get; set; }
    public string? Unit { get; set; }
    /// <summary>"wait" macro step, in milliseconds.</summary>
    public int? DelayMs { get; set; }
}

public sealed class DeckMacro
{
    public string Id { get; set; } = SoundButton.NewId();
    public string Name { get; set; } = "Macro";
    public List<DeckAction> Steps { get; set; } = new();
}

/// <summary>
/// A saved set of volumes. GameDeck's own channels are optional (null leaves them alone); applications
/// are matched by executable name. Applying a scene never changes devices or routing.
/// </summary>
public sealed class AudioScene
{
    public string Id { get; set; } = SoundButton.NewId();
    public string Name { get; set; } = "Scene";
    public string Icon { get; set; } = "headphones";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public SceneChannels? Channels { get; set; }
    public List<SceneApp> Apps { get; set; } = new();
    /// <summary>Global shortcut in canonical form ("Ctrl+Alt+1"); null = none.</summary>
    public string? Hotkey { get; set; }
    /// <summary>Executable ("game.exe") whose window coming to the front applies this scene, when <see cref="AppConfig.AutoScenes"/> is on.</summary>
    public string? AutoApp { get; set; }
}

/// <summary>GameDeck's mixer levels and mutes, in the same units as <see cref="AudioSettings"/>.</summary>
public sealed class SceneChannels
{
    public float MicVolume { get; set; } = 1f;
    public bool MicMuted { get; set; }
    public float VirtualVolume { get; set; } = 1f;
    public bool VirtualMuted { get; set; }
    public float MonitorVolume { get; set; } = 0.8f;
    public bool MonitorMuted { get; set; }
}

public sealed class SceneApp
{
    /// <summary>Executable name in lower case, e.g. "discord.exe" (see AppSessions).</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>0..1, as in the Windows volume mixer.</summary>
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
}

/// <summary>Interface preferences shared by the PC windows and every paired browser.</summary>
public sealed class UiSettings
{
    /// <summary>"ro" (default for new installs) or "en".</summary>
    public string Language { get; set; } = Localization.L.English;
    /// <summary>Chosen logo design and finish, see <see cref="LogoCatalog"/>.</summary>
    public string LogoModel { get; set; } = LogoCatalog.DefaultModel;
    public string LogoFinish { get; set; } = LogoCatalog.DefaultFinish;
}

public sealed class ServerSettings
{
    public int Port { get; set; } = 5280;
}

public sealed class AudioSettings
{
    /// <summary>Headphones. Null means "Windows default output".</summary>
    public string? MonitorDeviceId { get; set; }
    public bool MonitorEnabled { get; set; } = true;
    public float MonitorVolume { get; set; } = 0.8f;

    /// <summary>Render side of a virtual cable (e.g. "CABLE Input"). Null means off.</summary>
    public string? VirtualDeviceId { get; set; }
    public float VirtualVolume { get; set; } = 1.0f;

    /// <summary>Physical microphone mixed into the virtual output. Null means off.</summary>
    public string? MicDeviceId { get; set; }
    public bool MicEnabled { get; set; }
    public float MicVolume { get; set; } = 1.0f;

    public int LatencyMs { get; set; } = 40;

    // Mutes keep devices open and the volume remembered; the app sends 0 gain while muted.
    public bool MicMuted { get; set; }
    public bool VirtualMuted { get; set; }
    public bool MonitorMuted { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PlayMode>))]
public enum PlayMode
{
    /// <summary>Press starts, press again stops.</summary>
    Toggle,
    /// <summary>Press always restarts from the beginning. The default.</summary>
    Restart,
    /// <summary>Every press starts another overlapping copy.</summary>
    Overlap,
}

public sealed class SoundButton
{
    public string Id { get; set; } = NewId();
    public string Label { get; set; } = "Sound";
    /// <summary>File name inside the sounds directory.</summary>
    public string FileName { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    /// <summary>Identity tag shown as a small marker on the button, not a fill.</summary>
    public string Color { get; set; } = "#a9d6ff";
    public string Icon { get; set; } = "music";
    public float Volume { get; set; } = 1.0f;
    /// <summary>Off by default: a sound stops by itself at the end of its file.</summary>
    public bool Loop { get; set; }
    /// <summary>Pressing a playing button starts it again from the beginning.</summary>
    public PlayMode Mode { get; set; } = PlayMode.Restart;
    public long DurationMs { get; set; }
    /// <summary>Only this part of the file plays: from TrimStartMs to TrimEndMs (0 = the end of the file).</summary>
    public long TrimStartMs { get; set; }
    public long TrimEndMs { get; set; }
    public bool Favorite { get; set; }
    /// <summary>Free-text group shown as a filter chip ("Memes", "Hype"). Empty = uncategorised.</summary>
    public string Category { get; set; } = "";

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public sealed class Profile
{
    public string Id { get; set; } = SoundButton.NewId();
    public string Name { get; set; } = "Main";
    /// <summary>Ordered sound ids shown on this profile's grid.</summary>
    public List<string> Layout { get; set; } = new();
}

public sealed class PairedDevice
{
    public string Id { get; set; } = SoundButton.NewId();
    public string Name { get; set; } = "Device";
    /// <summary>SHA-256 of the session token, hex. The token itself is never stored.</summary>
    public string TokenHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}
