using GameDeck.Core.Localization;
using System.Reflection;
using GameDeck.App.Web;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Diagnostics;
using GameDeck.Core.Security;

namespace GameDeck.App;

/// <summary>
/// Application layer: the only place that knows about both the persisted config and the audio engine.
/// The web API and the tray UI both call through here.
/// </summary>
public sealed partial class GameDeckService
{
    public const long MaxUploadBytes = 50 * 1024 * 1024;
    private static readonly string[] AllowedExtensions = [".mp3", ".wav"];
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(5);

    private readonly object _devicesGate = new();
    private AudioDeviceList _devices = new([], []);
    // Every endpoint, including disabled and unplugged ones, for the routing report. Read with the device
    // list (start, Windows device notifications, rescan), not on every state push: it is slow to enumerate.
    private (IReadOnlyList<EndpointInfo> Renders, IReadOnlyList<EndpointInfo> Captures)? _endpoints;

    public ConfigStore Store { get; }
    public AudioEngine Engine { get; }
    public PairingService Pairing { get; }
    public int Port => Store.Read(c => c.Server.Port);
    public static DateTime StartedUtc { get; } = DateTime.UtcNow;

    public static string Version { get; } =
        typeof(GameDeckService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Anything in <see cref="StateDto"/> other than the playing list changed.</summary>
    public event Action? StateChanged;
    public event Action? PlayingChanged;
    /// <summary>A paired device was revoked; its open connections should be closed.</summary>
    public event Action<string>? DeviceRevoked;

    public GameDeckService(ConfigStore store, AudioEngine engine, PairingService pairing)
    {
        Store = store;
        Engine = engine;
        Pairing = pairing;
        store.Changed += () => StateChanged?.Invoke();
        engine.StatusChanged += () => StateChanged?.Invoke();
        engine.PlayingChanged += () => PlayingChanged?.Invoke();
        engine.DevicesChanged += () => { RefreshDevices(); StateChanged?.Invoke(); };
        Macros = new Core.Deck.MacroRunner(ExecuteAsync);
        Macros.Changed += () => StateChanged?.Invoke();
        Updates = new Updates.UpdateService(() => Language, Version);
        Presence = new Presence.PresenceService(store);
    }

    /// <summary>New versions and announcements from GitHub. Started by the app only, never by tests.</summary>
    public Updates.UpdateService Updates { get; }

    /// <summary>Discord Rich Presence. Started by the app only, never by tests.</summary>
    public Presence.PresenceService Presence { get; }

    public void Start()
    {
        PrettifyDefaultLabels();
        RefreshDevices();
        Engine.ApplySettings(Store.Read(c => Effective(c.Audio)));
        RefreshApps();
        var paths = Store.Read(c => c.Sounds.Select(Store.SoundPath).ToList());
        _ = Task.Run(async () =>
        {
            foreach (var p in paths)
            {
                try { await Engine.PreloadAsync(p); }
                catch (Exception ex) { Log.Warn($"Preload failed: {p}", ex); }
            }
        });
    }

    // ---------- Playback ----------

    public async Task<TriggerResult> TriggerAsync(string soundId)
    {
        var request = Store.Read(c =>
        {
            var s = c.Sounds.FirstOrDefault(x => x.Id == soundId) ?? throw new ApiException(404, L.T("api.soundNotFound"));
            return new PlayRequest(s.Id, Store.SoundPath(s), s.Volume, s.Loop, s.Mode, s.TrimStartMs, s.TrimEndMs);
        });
        try
        {
            return await Engine.TriggerAsync(request);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FileNotFoundException)
        {
            throw new ApiException(409, ex.Message);
        }
    }

    public void Stop(string soundId) => Engine.Stop(soundId);

    public void StopAll() => Engine.StopAll();

    // ---------- Sounds ----------

    public async Task<SoundDto> ImportAsync(Stream content, string fileName, long length, string? profileId)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) throw new ApiException(400, L.T("api.onlyMp3Wav", fileName));
        if (length > MaxUploadBytes) throw new ApiException(413, L.T("api.tooLarge", fileName));

        var sound = new SoundButton
        {
            Label = LabelFromFileName(fileName),
            OriginalFileName = Path.GetFileName(fileName),
            Color = Palette[Random.Shared.Next(Palette.Length)],
        };
        sound.FileName = sound.Id + ext;
        var path = Store.SoundPath(sound);

        try
        {
            await using (var file = File.Create(path))
            {
                await content.CopyToAsync(file);
            }
            var decoded = await Engine.PreloadAsync(path);
            sound.DurationMs = (long)decoded.Duration.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is not ApiException)
        {
            Engine.Evict(path);
            TryDelete(path);
            Log.Warn($"Import failed for {fileName}", ex);
            throw new ApiException(400, L.T("api.notAudio", fileName, ex.Message));
        }

        Store.Update(c =>
        {
            c.Sounds.Add(sound);
            var profile = c.Profiles.FirstOrDefault(p => p.Id == (profileId ?? c.ActiveProfileId)) ?? c.Profiles[0];
            profile.Layout.Add(sound.Id);
        });
        Log.Info($"Imported '{fileName}' as {sound.Id} ({sound.DurationMs} ms)");
        return ToDto(sound);
    }

    public SoundDto UpdateSound(string id, SoundPatch patch)
    {
        var dto = Store.Update(c =>
        {
            var s = c.Sounds.FirstOrDefault(x => x.Id == id) ?? throw new ApiException(404, L.T("api.soundNotFound"));
            if (patch.Label is not null) s.Label = CleanLabel(patch.Label);
            if (patch.Color is not null) s.Color = IsHexColor(patch.Color) ? patch.Color : throw new ApiException(400, L.T("api.badColor"));
            if (patch.Icon is not null) s.Icon = patch.Icon.Length <= 40 ? patch.Icon : throw new ApiException(400, L.T("api.iconTooLong"));
            if (patch.Volume is not null) s.Volume = Math.Clamp(patch.Volume.Value, 0f, 1.5f);
            if (patch.Loop is not null) s.Loop = patch.Loop.Value;
            if (patch.Mode is not null) s.Mode = patch.Mode.Value;
            if (patch.Favorite is not null) s.Favorite = patch.Favorite.Value;
            if (patch.Category is not null) s.Category = CleanCategory(patch.Category);
            if (patch.TrimStartMs is not null || patch.TrimEndMs is not null)
                (s.TrimStartMs, s.TrimEndMs) = CleanTrim(patch.TrimStartMs ?? s.TrimStartMs, patch.TrimEndMs ?? s.TrimEndMs, s.DurationMs);
            return ToDto(s);
        });
        Engine.SetSoundVolume(id, dto.Volume);
        return dto;
    }

    public void DeleteSound(string id)
    {
        var path = Store.Update(c =>
        {
            var s = c.Sounds.FirstOrDefault(x => x.Id == id) ?? throw new ApiException(404, L.T("api.soundNotFound"));
            c.Sounds.Remove(s);
            return Store.SoundPath(s);
        });
        Engine.Stop(id);
        Engine.Evict(path);
        TryDelete(path);
    }

    // ---------- Profiles ----------

    public ProfileDto CreateProfile(string? name) => Store.Update(c =>
    {
        var p = new Profile { Name = CleanLabel(name ?? "New profile") };
        c.Profiles.Add(p);
        c.ActiveProfileId = p.Id;
        return ToDto(p);
    });

    public ProfileDto UpdateProfile(string id, ProfilePatch patch) => Store.Update(c =>
    {
        var p = c.Profiles.FirstOrDefault(x => x.Id == id) ?? throw new ApiException(404, L.T("api.profileNotFound"));
        if (patch.Name is not null) p.Name = CleanLabel(patch.Name);
        if (patch.Layout is not null) p.Layout = patch.Layout.ToList(); // unknown ids are dropped by Normalize
        return ToDto(p);
    });

    public void AddToProfile(string id, string soundId) => Store.Update(c =>
    {
        var p = c.Profiles.FirstOrDefault(x => x.Id == id) ?? throw new ApiException(404, L.T("api.profileNotFound"));
        if (c.Sounds.All(s => s.Id != soundId)) throw new ApiException(404, L.T("api.soundNotFound"));
        if (!p.Layout.Contains(soundId)) p.Layout.Add(soundId);
    });

    public void DeleteProfile(string id) => Store.Update(c =>
    {
        if (c.Profiles.Count <= 1) throw new ApiException(400, L.T("api.lastProfile"));
        var removed = c.Profiles.RemoveAll(p => p.Id == id);
        if (removed == 0) throw new ApiException(404, L.T("api.profileNotFound"));
    });

    public void ActivateProfile(string id) => Store.Update(c =>
    {
        if (c.Profiles.All(p => p.Id != id)) throw new ApiException(404, L.T("api.profileNotFound"));
        c.ActiveProfileId = id;
    });

    // ---------- Audio ----------

    public void UpdateAudio(AudioPatch patch)
    {
        var settings = Store.Update(c =>
        {
            var a = c.Audio;
            if (patch.MonitorDeviceId is not null) a.MonitorDeviceId = NullIfEmpty(patch.MonitorDeviceId);
            if (patch.MonitorEnabled is not null) a.MonitorEnabled = patch.MonitorEnabled.Value;
            if (patch.MonitorVolume is not null) a.MonitorVolume = patch.MonitorVolume.Value;
            if (patch.VirtualDeviceId is not null) a.VirtualDeviceId = NullIfEmpty(patch.VirtualDeviceId);
            if (patch.VirtualVolume is not null) a.VirtualVolume = patch.VirtualVolume.Value;
            if (patch.MicDeviceId is not null) a.MicDeviceId = NullIfEmpty(patch.MicDeviceId);
            if (patch.MicEnabled is not null) a.MicEnabled = patch.MicEnabled.Value;
            if (patch.MicVolume is not null) a.MicVolume = patch.MicVolume.Value;
            if (patch.LatencyMs is not null) a.LatencyMs = patch.LatencyMs.Value;
            if (patch.MicMuted is not null) a.MicMuted = patch.MicMuted.Value;
            if (patch.VirtualMuted is not null) a.VirtualMuted = patch.VirtualMuted.Value;
            if (patch.MonitorMuted is not null) a.MonitorMuted = patch.MonitorMuted.Value;
            return a;
        });
        Engine.ApplySettings(Store.Read(_ => Effective(settings)));
    }

    /// <summary>
    /// What the engine should run: the stored settings with muted channels at zero gain. Devices stay
    /// open while muted (unmuting is instant) and the stored volume is kept for when you unmute.
    /// </summary>
    internal static AudioSettings Effective(AudioSettings a) => new()
    {
        MonitorDeviceId = a.MonitorDeviceId,
        MonitorEnabled = a.MonitorEnabled,
        MonitorVolume = a.MonitorMuted ? 0f : a.MonitorVolume,
        VirtualDeviceId = a.VirtualDeviceId,
        VirtualVolume = a.VirtualMuted ? 0f : a.VirtualVolume,
        MicDeviceId = a.MicDeviceId,
        MicEnabled = a.MicEnabled,
        MicVolume = a.MicMuted ? 0f : a.MicVolume,
        LatencyMs = a.LatencyMs,
    };

    // ---------- Applications (Windows volume mixer) ----------

    private readonly object _appsGate = new();
    private IReadOnlyList<AppAudioInfo> _apps = [];

    /// <summary>Re-reads application volumes; pushes state only if something changed. Not polled: called on demand.</summary>
    public IReadOnlyList<AppAudioInfo> RefreshApps()
    {
        IReadOnlyList<AppAudioInfo> list;
        try
        {
            list = AppSessions.List();
        }
        catch (Exception ex)
        {
            Log.Error("Application sessions could not be read", ex);
            return _apps;
        }
        bool changed;
        lock (_appsGate)
        {
            changed = !_apps.SequenceEqual(list);
            _apps = list;
        }
        if (changed) StateChanged?.Invoke();
        return list;
    }

    public void SetAppVolume(string key, AppVolumePatch patch)
    {
        var target = new AppAudioTarget(key.ToLowerInvariant(),
            patch.Volume is { } v && float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : null, patch.Muted);
        if (AppSessions.Apply([target]).Count == 0) throw new ApiException(404, L.T("apps.notRunning"));
        // Update the cached entry instead of re-reading every session: a slider sends several of these a second.
        lock (_appsGate)
            _apps = _apps.Select(a => a.Key == target.Key ? a with { Volume = target.Volume ?? a.Volume, Muted = target.Muted ?? a.Muted } : a).ToList();
        StateChanged?.Invoke();
    }

    // ---------- Smart Audio Scenes ----------

    private readonly object _hotkeyGate = new();
    private IReadOnlyDictionary<string, string> _hotkeyProblems = new Dictionary<string, string>();

    public SceneDto CreateScene(SceneCreate body)
    {
        var (channels, apps) = CaptureCurrent();
        var dto = Store.Update(c =>
        {
            var s = new AudioScene
            {
                Name = CleanLabel(string.IsNullOrWhiteSpace(body.Name) ? L.T("scene.defaultName", c.Scenes.Count + 1) : body.Name),
                Icon = CleanIcon(body.Icon) ?? "headphones",
                Channels = channels,
                Apps = apps,
            };
            c.Scenes.Add(s);
            return ToDto(s);
        });
        Log.Info($"Scene '{dto.Name}' created ({dto.Apps.Count} apps)");
        return dto;
    }

    public SceneDto UpdateScene(string id, ScenePatch patch)
    {
        string? hotkey = null;
        if (patch.Hotkey is { Length: > 0 })
        {
            if (!Core.Scenes.Hotkey.TryParse(patch.Hotkey, out var parsed, out var error)) throw new ApiException(400, L.T(error!));
            hotkey = parsed!.ToString();
        }
        var autoApp = patch.AutoApp is { Length: > 0 } a ? a.Trim().ToLowerInvariant() : null;

        return Store.Update(c =>
        {
            var s = FindScene(c, id);
            if (patch.Name is not null) s.Name = CleanLabel(patch.Name);
            if (patch.Icon is not null) s.Icon = CleanIcon(patch.Icon) ?? throw new ApiException(400, L.T("api.iconTooLong"));
            if (patch.Hotkey is not null)
            {
                var owner = hotkey is null ? null : c.Scenes.FirstOrDefault(x => x.Id != id && string.Equals(x.Hotkey, hotkey, StringComparison.OrdinalIgnoreCase));
                if (owner is not null) throw new ApiException(409, L.T("scene.hotkeyTaken", hotkey!, owner.Name));
                s.Hotkey = hotkey;
            }
            if (patch.AutoApp is not null)
            {
                var owner = autoApp is null ? null : c.Scenes.FirstOrDefault(x => x.Id != id && x.AutoApp == autoApp);
                if (owner is not null) throw new ApiException(409, L.T("scene.autoAppTaken", autoApp!, owner.Name));
                s.AutoApp = autoApp;
            }
            if (patch.Apps is not null) s.Apps = patch.Apps; // cleaned by Normalize
            if (patch.ClearChannels == true) s.Channels = null;
            else if (patch.Channels is not null) s.Channels = patch.Channels;
            s.ModifiedUtc = DateTime.UtcNow;
            return ToDto(s);
        });
    }

    /// <summary>
    /// Stores the current mixer: GameDeck channels (if the scene includes them) and every running
    /// application. Applications in the scene that are not running now keep their saved values.
    /// </summary>
    public SceneDto CaptureScene(string id)
    {
        var (channels, apps) = CaptureCurrent();
        return Store.Update(c =>
        {
            var s = FindScene(c, id);
            if (s.Channels is not null) s.Channels = channels;
            var running = apps.Select(x => x.Key).ToHashSet();
            s.Apps = apps.Concat(s.Apps.Where(x => !running.Contains(x.Key))).ToList();
            s.ModifiedUtc = DateTime.UtcNow;
            return ToDto(s);
        });
    }

    public SceneDto DuplicateScene(string id) => Store.Update(c =>
    {
        var src = FindScene(c, id);
        var copy = new AudioScene
        {
            Name = CleanLabel(L.T("scene.copyName", src.Name)),
            Icon = src.Icon,
            Channels = src.Channels is null ? null : Clone(src.Channels),
            Apps = src.Apps.Select(a => new SceneApp { Key = a.Key, Name = a.Name, Volume = a.Volume, Muted = a.Muted }).ToList(),
            // Shortcut and trigger application stay with the original: each can belong to one scene only.
        };
        c.Scenes.Insert(c.Scenes.IndexOf(src) + 1, copy);
        return ToDto(copy);
    });

    public void DeleteScene(string id) => Store.Update(c =>
    {
        if (c.Scenes.RemoveAll(s => s.Id == id) == 0) throw new ApiException(404, L.T("scene.notFound"));
    });

    /// <summary>
    /// Applies saved volumes: GameDeck channels through the normal mixer path (devices and routing
    /// untouched), then each running application. Applications that aren't running are skipped quietly;
    /// applications not in the scene are left alone.
    /// </summary>
    public SceneApplyResult ApplyScene(string id, string source = "manual")
    {
        var (name, channels, apps) = Store.Read(c =>
        {
            var s = FindScene(c, id);
            return (s.Name, s.Channels is null ? null : Clone(s.Channels), s.Apps.Select(a => (a.Key, a.Name, a.Volume, a.Muted)).ToList());
        });

        if (channels is not null)
            UpdateAudio(new AudioPatch(null, null, channels.MonitorVolume, null, channels.VirtualVolume, null, null, channels.MicVolume, null,
                MicMuted: channels.MicMuted, VirtualMuted: channels.VirtualMuted, MonitorMuted: channels.MonitorMuted));

        var found = AppSessions.Apply(apps.Select(a => new AppAudioTarget(a.Key, a.Volume, a.Muted)));
        Store.Update(c => c.ActiveSceneId = id);
        RefreshApps();
        var applied = apps.Where(a => found.Contains(a.Key)).Select(a => a.Name).ToList();
        var skipped = apps.Where(a => !found.Contains(a.Key)).Select(a => a.Name).ToList();
        Log.Info($"Scene '{name}' applied ({source}): {applied.Count} app(s) set, {skipped.Count} not running");
        return new SceneApplyResult(id, applied, skipped);
    }

    public void SetSceneOptions(SceneOptionsPatch patch) => Store.Update(c =>
    {
        if (patch.AutoSwitch is { } on) c.AutoScenes = on;
    });

    /// <summary>Scene id → canonical shortcut, for the tray's hotkey registration. Empty while suspended.</summary>
    public IReadOnlyList<(string SceneId, string Hotkey)> HotkeyBindings() =>
        DateTime.UtcNow < _hotkeysSuspendedUntil
            ? []
            : Store.Read(c => c.Scenes.Where(s => s.Hotkey is not null).Select(s => (s.Id, s.Hotkey!)).ToList());

    private DateTime _hotkeysSuspendedUntil = DateTime.MinValue;

    /// <summary>
    /// Releases every scene shortcut for a moment while a dashboard records a new one. Otherwise Windows
    /// delivers a registered combination to GameDeck instead of the browser, and the recorder never
    /// sees it. Resumes by itself after <paramref name="seconds"/> (0 = now).
    /// </summary>
    public void SuspendHotkeys(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, 30));
        var until = DateTime.UtcNow + duration;
        _hotkeysSuspendedUntil = until;
        StateChanged?.Invoke();
        if (duration > TimeSpan.Zero)
            _ = Task.Delay(duration + TimeSpan.FromMilliseconds(50)).ContinueWith(_ =>
            {
                if (_hotkeysSuspendedUntil == until) StateChanged?.Invoke(); // re-register
            }, TaskScheduler.Default);
    }

    /// <summary>Scene id → trigger executable; empty while automatic switching is off.</summary>
    public IReadOnlyList<(string SceneId, string AutoApp)> AutoRules() =>
        Store.Read(c => c.AutoScenes ? c.Scenes.Where(s => s.AutoApp is not null).Select(s => (s.Id, s.AutoApp!)).ToList() : []);

    public string? ActiveSceneId => Store.Read(c => c.ActiveSceneId);

    /// <summary>Shortcuts Windows refused (another program owns them), reported by the tray.</summary>
    public void ReportHotkeyProblems(IReadOnlyDictionary<string, string> problems)
    {
        lock (_hotkeyGate)
        {
            if (_hotkeyProblems.Count == problems.Count && _hotkeyProblems.All(p => problems.TryGetValue(p.Key, out var v) && v == p.Value)) return;
            _hotkeyProblems = problems;
        }
        StateChanged?.Invoke();
    }

    private (SceneChannels Channels, List<SceneApp> Apps) CaptureCurrent()
    {
        var channels = Store.Read(c => new SceneChannels
        {
            MicVolume = c.Audio.MicVolume, MicMuted = c.Audio.MicMuted,
            VirtualVolume = c.Audio.VirtualVolume, VirtualMuted = c.Audio.VirtualMuted,
            MonitorVolume = c.Audio.MonitorVolume, MonitorMuted = c.Audio.MonitorMuted,
        });
        var apps = RefreshApps().Select(a => new SceneApp { Key = a.Key, Name = a.Name, Volume = a.Volume, Muted = a.Muted }).ToList();
        return (channels, apps);
    }

    private static AudioScene FindScene(AppConfig c, string id) =>
        c.Scenes.FirstOrDefault(s => s.Id == id) ?? throw new ApiException(404, L.T("scene.notFound"));

    private static SceneChannels Clone(SceneChannels s) => new()
    {
        MicVolume = s.MicVolume, MicMuted = s.MicMuted, VirtualVolume = s.VirtualVolume,
        VirtualMuted = s.VirtualMuted, MonitorVolume = s.MonitorVolume, MonitorMuted = s.MonitorMuted,
    };

    private static string? CleanIcon(string? icon) => icon is { Length: > 0 and <= 40 } ? icon : null;

    private SceneDto ToDto(AudioScene s)
    {
        string? problem;
        lock (_hotkeyGate) problem = _hotkeyProblems.GetValueOrDefault(s.Id);
        return new(s.Id, s.Name, s.Icon, s.CreatedUtc, s.ModifiedUtc, s.Channels is null ? null : Clone(s.Channels),
            s.Apps.Select(a => new SceneApp { Key = a.Key, Name = a.Name, Volume = a.Volume, Muted = a.Muted }).ToList(),
            s.Hotkey, s.Hotkey is null ? null : problem, s.AutoApp);
    }

    // ---------- Language ----------

    /// <summary>Raised after the interface language changed, so PC windows can redraw their text.</summary>
    public event Action? LanguageChanged;

    public string Language => Store.Read(c => c.Ui.Language);

    public void SetLanguage(string? language)
    {
        if (!L.IsSupported(language))
            throw new ApiException(400, L.T("api.badLanguage"));
        var lang = L.Normalize(language);
        Store.Update(c => c.Ui.Language = lang);
        L.Language = lang;
        // Re-run device reconciliation so status messages are regenerated in the new language.
        Engine.ApplySettings(Store.Read(c => Effective(c.Audio)));
        LanguageChanged?.Invoke();
        StateChanged?.Invoke();
        Log.Info($"Interface language set to {lang}");
    }

    // ---------- Logo ----------

    /// <summary>Raised after a new logo was chosen, so the tray icon can follow.</summary>
    public event Action? LogoChanged;

    public (string Model, string Finish) Logo => Store.Read(c => (c.Ui.LogoModel, c.Ui.LogoFinish));

    public void SetLogo(string? model, string? finish)
    {
        if (model is null || finish is null || !LogoCatalog.Models.Contains(model) || !LogoCatalog.Finishes.Contains(finish))
            throw new ApiException(400, L.T("api.badLogo"));
        Store.Update(c =>
        {
            c.Ui.LogoModel = model;
            c.Ui.LogoFinish = finish;
        });
        LogoChanged?.Invoke();
        StateChanged?.Invoke();
        Log.Info($"Logo set to {model} / {finish}");
    }

    // ---------- Start with Windows ----------

    public WindowsAutoStart AutoStart { get; set; } = new();

    /// <summary>A setting of the PC itself, so only a browser on the PC can read or change it.</summary>
    public bool AutoStartEnabled(bool local)
    {
        if (!local) throw new ApiException(403, L.T("api.pcOnly"));
        return AutoStart.Enabled;
    }

    public void SetAutoStart(bool enabled, bool local)
    {
        if (!local) throw new ApiException(403, L.T("api.pcOnly"));
        try
        {
            AutoStart.Set(enabled);
            Log.Info($"Start with Windows: {(enabled ? "on" : "off")}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            Log.Error("Start with Windows could not be changed", ex);
            throw new ApiException(500, L.T("api.autoStartFailed"));
        }
    }

    /// <summary>
    /// "am-trei-randuri-de-creier_riGvZqJ.mp3" → "Am trei randuri de creier": separators become spaces
    /// and the random id that sound sites append (underscore + mixed-case run) is dropped.
    /// </summary>
    internal static string LabelFromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var suffix = System.Text.RegularExpressions.Regex.Match(stem, @"_([A-Za-z0-9]{5,10})$");
        if (suffix.Success && suffix.Groups[1].Value.Any(char.IsUpper) && suffix.Groups[1].Value.Any(char.IsLower) && suffix.Index > 0)
            stem = stem[..suffix.Index];
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"[-_.\s]+", " ").Trim();
        if (stem.Length > 0) stem = char.ToUpperInvariant(stem[0]) + stem[1..];
        return CleanLabel(stem);
    }

    /// <summary>Labels the user never renamed (still the raw file name) get the readable form.</summary>
    private void PrettifyDefaultLabels()
    {
        var renamed = Store.Update(c =>
        {
            var n = 0;
            foreach (var s in c.Sounds)
            {
                if (string.IsNullOrEmpty(s.OriginalFileName)) continue;
                var raw = CleanLabel(Path.GetFileNameWithoutExtension(s.OriginalFileName));
                var pretty = LabelFromFileName(s.OriginalFileName);
                if (s.Label == raw && pretty != raw)
                {
                    s.Label = pretty;
                    n++;
                }
            }
            return n;
        }, notify: false);
        if (renamed > 0) Log.Info($"Tidied {renamed} sound label(s) taken from file names");
    }

    private static string CleanCategory(string value)
    {
        var trimmed = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return trimmed.Length > 24 ? trimmed[..24] : trimmed;
    }

    public void RefreshDevices()
    {
        try
        {
            var list = Engine.GetDevices();
            lock (_devicesGate) _devices = list;
        }
        catch (Exception ex)
        {
            Log.Error("Device enumeration failed", ex);
        }
        try
        {
            using var e = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var endpoints = (RoutingAdvisor.List(e, NAudio.CoreAudioApi.DataFlow.Render), RoutingAdvisor.List(e, NAudio.CoreAudioApi.DataFlow.Capture));
            lock (_devicesGate) _endpoints = endpoints;
        }
        catch (Exception ex)
        {
            Log.Error("Endpoint enumeration failed", ex);
            lock (_devicesGate) _endpoints = null; // the routing report then reads them itself
        }
    }

    // ---------- Pairing / auth ----------

    public (PairAttempt Attempt, PairedDevice? Device) Pair(string? code, string? deviceName)
    {
        var attempt = Pairing.TryPair(code);
        if (attempt.Outcome != PairOutcome.Success) return (attempt, null);

        var device = new PairedDevice
        {
            Name = CleanLabel(string.IsNullOrWhiteSpace(deviceName) ? "Device" : deviceName),
            TokenHash = PairingService.HashToken(attempt.Token!),
        };
        Store.Update(c => c.PairedDevices.Add(device));
        Log.Info($"Paired new device '{device.Name}' ({device.Id})");
        return (attempt, device);
    }

    public PairedDevice? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
        var hash = PairingService.HashToken(token);
        var (device, stale) = Store.Read(c =>
        {
            var d = c.PairedDevices.FirstOrDefault(x => PairingService.HashMatches(token, x.TokenHash));
            return (d, d is not null && DateTime.UtcNow - d.LastSeenUtc > LastSeenResolution);
        });
        if (device is not null && stale)
        {
            Store.Update(c =>
            {
                var d = c.PairedDevices.FirstOrDefault(x => x.TokenHash == hash);
                if (d is not null) d.LastSeenUtc = DateTime.UtcNow;
            }, notify: false);
        }
        return device;
    }

    public void Revoke(string deviceId)
    {
        var removed = Store.Update(c => c.PairedDevices.RemoveAll(d => d.Id == deviceId));
        if (removed == 0) throw new ApiException(404, L.T("api.deviceNotFound"));
        Log.Info($"Revoked device {deviceId}");
        DeviceRevoked?.Invoke(deviceId);
    }

    public void RevokeAll()
    {
        var ids = Store.Update(c =>
        {
            var list = c.PairedDevices.Select(d => d.Id).ToList();
            c.PairedDevices.Clear();
            return list;
        });
        foreach (var id in ids) DeviceRevoked?.Invoke(id);
        Log.Info($"Revoked all {ids.Count} paired devices");
    }

    // ---------- State ----------

    public StateDto BuildState()
    {
        AudioDeviceList devices;
        lock (_devicesGate) devices = _devices;
        var urls = NetworkGuard.GetLanAddresses().Select(a => $"http://{a}:{Port}").ToList();
        var routing = GetRouting();
        IReadOnlyList<AppAudioInfo> apps;
        lock (_appsGate) apps = _apps;
        var windows = WindowsForState();

        return Store.Read(c => new StateDto(
            c.Sounds.Select(ToDto).ToList(),
            c.Profiles.Select(ToDto).ToList(),
            c.ActiveProfileId!,
            c.Audio,
            devices,
            Engine.GetStatus(),
            Engine.GetPlaying(),
            c.PairedDevices.Select(d => new PairedDeviceDto(d.Id, d.Name, d.CreatedUtc, d.LastSeenUtc)).ToList(),
            new ServerInfoDto(c.Server.Port, urls, Version),
            routing,
            new DiagnosticsDto(Engine.GetMicStats(), StartedUtc, Store.DataDirectory,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            new UiDto(c.Ui.Language, c.Ui.LogoModel, c.Ui.LogoFinish),
            apps,
            new ScenesDto(c.Scenes.Select(ToDto).ToList(), c.ActiveSceneId, c.AutoScenes),
            BuildDeck(c, windows)));
    }

    public RoutingReport GetRouting()
    {
        try
        {
            (IReadOnlyList<EndpointInfo> Renders, IReadOnlyList<EndpointInfo> Captures)? endpoints;
            lock (_devicesGate) endpoints = _endpoints;
            var audio = Store.Read(c => c.Audio);
            return endpoints is { } known
                ? RoutingAdvisor.Build(audio, Engine.GetStatus(), Engine.GetMicStats(), known.Renders, known.Captures)
                : RoutingAdvisor.Build(audio, Engine.GetStatus(), Engine.GetMicStats());
        }
        catch (Exception ex)
        {
            Log.Error("Routing report failed", ex);
            return new RoutingReport([], null, [new RoutingIssue("error", L.T("route.readFailed", ex.Message), null)]);
        }
    }

    /// <summary>Tone into the virtual output only, listened for on the cable's recording side.</summary>
    public async Task<RouteTestResult> RunRouteTestAsync()
    {
        var capture = GetRouting().Selected?.Capture;
        if (capture is null)
            return new RouteTestResult(false, 0, -120, -120, "",
                L.T("test.noCapture"));
        if (capture.State != "Active")
            return new RouteTestResult(false, 0, -120, -120, capture.Name, L.T("test.captureNotActive", capture.Name, RoutingAdvisor.StateText(capture.State)));
        var result = await RouteSelfTest.RunAsync(Engine, capture.Id);
        Log.Info($"Route test: {result.Message}");
        return result;
    }

    // ---------- helpers ----------

    private static readonly string[] Palette =
        ["#a9d6ff", "#c9d1dc", "#8fb8c9", "#a9b8a1", "#d6c3a1", "#c9a4a4", "#b3a9d6", "#7d8696"];

    private static SoundDto ToDto(SoundButton s) =>
        new(s.Id, s.Label, s.Color, s.Icon, s.Volume, s.Loop, s.Mode, s.DurationMs, s.OriginalFileName, s.Favorite, s.Category ?? "",
            s.TrimStartMs, s.TrimEndMs);

    /// <summary>Clamps a selection to the file; the full length is stored as (0, 0) so it means "untrimmed".</summary>
    internal static (long Start, long End) CleanTrim(long startMs, long endMs, long durationMs)
    {
        var end = endMs <= 0 || endMs >= durationMs ? durationMs : endMs;
        var start = Math.Clamp(startMs, 0, Math.Max(0, durationMs - CachedSound.MinTrimMs));
        if (start == 0 && end == durationMs) return (0, 0);
        if (end - start < CachedSound.MinTrimMs) throw new ApiException(400, L.T("api.trimTooShort", CachedSound.MinTrimMs));
        return (start, end >= durationMs ? 0 : end);
    }

    /// <summary>The imported file itself, for the editor's waveform and local preview.</summary>
    public (string Path, string ContentType) SoundFile(string id) => Store.Read(c =>
    {
        var s = c.Sounds.FirstOrDefault(x => x.Id == id) ?? throw new ApiException(404, L.T("api.soundNotFound"));
        var path = Store.SoundPath(s);
        if (!File.Exists(path)) throw new ApiException(404, L.T("sound.missing"));
        return (path, Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav" : "audio/mpeg");
    });

    private static ProfileDto ToDto(Profile p) => new(p.Id, p.Name, p.Layout.ToList());

    private static string CleanLabel(string value)
    {
        var trimmed = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (trimmed.Length == 0) trimmed = L.T("sound.untitled");
        return trimmed.Length > 40 ? trimmed[..40] : trimmed;
    }

    private static bool IsHexColor(string v) =>
        v.Length == 7 && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit);

    private static string? NullIfEmpty(string v) => v.Length == 0 ? null : v;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn($"Could not delete {path}", ex); }
    }
}
