using GameDeck.Core.Audio;
using GameDeck.Core.Config;

namespace GameDeck.App.Web;

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record SoundDto(
    string Id, string Label, string Color, string Icon, float Volume, bool Loop,
    PlayMode Mode, long DurationMs, string OriginalFileName, bool Favorite, string Category,
    long TrimStartMs, long TrimEndMs);

public sealed record DiagnosticsDto(
    MicStats Mic, DateTime StartedUtc, string DataDirectory, string Os, string Runtime);

public sealed record ProfileDto(string Id, string Name, IReadOnlyList<string> Layout);

public sealed record PairedDeviceDto(string Id, string Name, DateTime CreatedUtc, DateTime LastSeenUtc);

public sealed record ServerInfoDto(int Port, IReadOnlyList<string> Urls, string Version);

public sealed record StateDto(
    IReadOnlyList<SoundDto> Sounds,
    IReadOnlyList<ProfileDto> Profiles,
    string ActiveProfileId,
    AudioSettings Audio,
    AudioDeviceList Devices,
    EngineStatus Status,
    IReadOnlyList<PlayingInfo> Playing,
    IReadOnlyList<PairedDeviceDto> PairedDevices,
    ServerInfoDto Server,
    RoutingReport Routing,
    DiagnosticsDto Diagnostics,
    UiDto Ui,
    IReadOnlyList<AppAudioInfo> Apps,
    ScenesDto Scenes,
    DeckStateDto Deck);

/// <param name="HotkeyError">Why the shortcut is not working right now (e.g. another program owns it); null when fine.</param>
public sealed record SceneDto(
    string Id, string Name, string Icon, DateTime CreatedUtc, DateTime ModifiedUtc,
    SceneChannels? Channels, IReadOnlyList<SceneApp> Apps, string? Hotkey, string? HotkeyError, string? AutoApp);

/// <param name="ActiveId">The scene applied last.</param>
/// <param name="AutoSwitch">Global switch for automatic activation.</param>
public sealed record ScenesDto(IReadOnlyList<SceneDto> Items, string? ActiveId, bool AutoSwitch);

public sealed record SceneCreate(string? Name, string? Icon);

/// <summary>
/// Hotkey and AutoApp use "" to clear. Apps and Channels replace the stored ones; ClearChannels
/// makes the scene leave GameDeck's own channels alone.
/// </summary>
public sealed record ScenePatch(
    string? Name = null, string? Icon = null, string? Hotkey = null, string? AutoApp = null,
    List<SceneApp>? Apps = null, SceneChannels? Channels = null, bool? ClearChannels = null);

/// <param name="Applied">Applications that were running and got their saved volume.</param>
/// <param name="Skipped">Applications in the scene that are not running now (left for next time).</param>
public sealed record SceneApplyResult(string SceneId, IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped);

public sealed record SceneOptionsPatch(bool? AutoSwitch);

public sealed record AppVolumePatch(float? Volume, bool? Muted);

public sealed record UiDto(string Language, string LogoModel, string LogoFinish);

public sealed record UiPatch(string? Language);

public sealed record LogoPatch(string? Model, string? Finish);

public sealed record AutoStartDto(bool Enabled);

public sealed record PresenceEnabled(bool Enabled);

/// <param name="Local">The browser runs on the GameDeck PC itself; only it may add launch, link and key actions.</param>
public sealed record SessionDto(bool Authenticated, string? DeviceId, string? DeviceName, bool Local = false);

public sealed record PairRequest(string? Code, string? DeviceName);

public sealed record SoundPatch(
    string? Label, string? Color, string? Icon, float? Volume, bool? Loop, PlayMode? Mode,
    bool? Favorite = null, string? Category = null,
    // The part of the file that plays. TrimEndMs 0 (or the full length) = to the end of the file.
    long? TrimStartMs = null, long? TrimEndMs = null);

public sealed record ProfileCreate(string? Name);

public sealed record ProfilePatch(string? Name, List<string>? Layout);

public sealed record LayoutAdd(string SoundId);

/// <summary>Device ids use "" to mean "default" (monitor) or "off" (virtual/mic) so JSON can express it.</summary>
public sealed record AudioPatch(
    string? MonitorDeviceId, bool? MonitorEnabled, float? MonitorVolume,
    string? VirtualDeviceId, float? VirtualVolume,
    string? MicDeviceId, bool? MicEnabled, float? MicVolume,
    int? LatencyMs,
    bool? MicMuted = null, bool? VirtualMuted = null, bool? MonitorMuted = null);

/// <summary>Seconds to release scene shortcuts while a dashboard records one; 0 resumes.</summary>
public sealed record HotkeySuspend(double Seconds);

/// <param name="RunningMacros">Ids of macros running right now.</param>
/// <param name="MacroErrors">Macro id → why its last run stopped early.</param>
/// <param name="Toggled">Two-state buttons currently in their second state.</param>
public sealed record DeckStateDto(
    IReadOnlyList<DeckProfile> Profiles, string ActiveProfileId, IReadOnlyList<DeckMacro> Macros,
    IReadOnlyList<string> RunningMacros, IReadOnlyDictionary<string, string> MacroErrors, IReadOnlyList<string> Toggled);

/// <param name="CopyFrom">Profile to duplicate; null creates an empty one.</param>
public sealed record DeckProfileCreate(string? Name, string? CopyFrom = null);

/// <param name="Ignored">The press came too soon after the previous one on the same button (double tap) and did nothing.</param>
public sealed record DeckPressResult(bool Ok, bool Ignored = false);

/// <summary>A profile as a portable file: its macros and button images travel with it.</summary>
/// <param name="Images">Image file name → base64 content.</param>
public sealed record DeckExport(string? Format, int Version, DeckProfile? Profile, List<DeckMacro>? Macros, Dictionary<string, string>? Images);

/// <param name="Folder">True opens a folder picker instead of a file picker.</param>
/// <param name="Current">The path already in the field, so the dialog opens next to it.</param>
public sealed record DeckBrowse(bool Folder, string? Current);

/// <summary>A program, shortcut, file or folder whose Windows icon becomes a button image.</summary>
public sealed record DeckIconRequest(string? Path);

/// <param name="Name">File name to put on a button.</param>
/// <param name="Animated">GIF, animated WebP or animated PNG with more than one frame.</param>
public sealed record DeckImageUpload(string Name, bool Animated, int Width, int Height, int Frames);
