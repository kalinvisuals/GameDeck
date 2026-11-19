using System.Text.Json;
using GameDeck.Core.Diagnostics;

namespace GameDeck.Core.Config;

/// <summary>
/// Thread-safe owner of <see cref="AppConfig"/>. All reads and writes go through a single lock and
/// every mutation is persisted atomically (write temp file, then replace).
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private AppConfig _config;
    private string? _savedFeatures;

    public string DataDirectory { get; }
    public string SoundsDirectory { get; }
    public string ConfigPath { get; }
    private string FeaturesPath => Path.Combine(DataDirectory, "features.json");

    /// <summary>Raised after any successful mutation (outside the lock).</summary>
    public event Action? Changed;

    public ConfigStore(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        SoundsDirectory = Path.Combine(dataDirectory, "sounds");
        ConfigPath = Path.Combine(dataDirectory, "config.json");
        Directory.CreateDirectory(SoundsDirectory);
        _config = Load();
        Normalize(_config);
        lock (_gate) SaveLocked();
    }

    public static string DefaultDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("GAMEDECK_DATA");
        return !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameDeck");
    }

    public T Read<T>(Func<AppConfig, T> reader)
    {
        lock (_gate) return reader(_config);
    }

    public T Update<T>(Func<AppConfig, T> mutation, bool notify = true)
    {
        T result;
        lock (_gate)
        {
            result = mutation(_config);
            Normalize(_config);
            SaveLocked();
        }
        if (notify) Changed?.Invoke();
        return result;
    }

    public void Update(Action<AppConfig> mutation, bool notify = true) =>
        Update(c => { mutation(c); return true; }, notify);

    public string SoundPath(SoundButton sound) => Path.Combine(SoundsDirectory, sound.FileName);

    private AppConfig Load()
    {
        var config = new AppConfig();
        bool hasDeck = false, hasScenes = false, hasLogo = false;
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    hasDeck = doc.RootElement.TryGetProperty("deck", out _);
                    hasScenes = doc.RootElement.TryGetProperty("scenes", out _);
                    hasLogo = doc.RootElement.TryGetProperty("ui", out var ui) && ui.ValueKind == JsonValueKind.Object
                        && ui.TryGetProperty("logoModel", out _);
                }
            }
            catch (Exception ex)
            {
                // Keep the broken file for inspection instead of silently overwriting the user's layout.
                var backup = ConfigPath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(ConfigPath, backup, overwrite: true);
                Log.Error($"Config could not be read, started fresh. Backup: {backup}", ex);
                config = new AppConfig();
            }
        }
        RestoreFeatures(config, hasDeck, hasScenes, hasLogo);
        return config;
    }

    /// <summary>
    /// An older GameDeck rewrites config.json without the sections it does not know, so those sections
    /// are mirrored in features.json (which it never touches) and brought back from there.
    /// </summary>
    private void RestoreFeatures(AppConfig config, bool hasDeck, bool hasScenes, bool hasLogo)
    {
        if ((hasDeck && hasScenes && hasLogo) || !File.Exists(FeaturesPath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<FeatureData>(File.ReadAllText(FeaturesPath), JsonOptions);
            if (saved is null) return;
            if (!hasDeck && saved.Deck is not null) config.Deck = saved.Deck;
            if (!hasScenes && saved.Scenes is not null)
            {
                config.Scenes = saved.Scenes;
                config.ActiveSceneId = saved.ActiveSceneId;
                config.AutoScenes = saved.AutoScenes;
            }
            if (!hasLogo && saved.LogoModel is not null)
            {
                config.Ui ??= new UiSettings();
                config.Ui.LogoModel = saved.LogoModel;
                config.Ui.LogoFinish = saved.LogoFinish ?? LogoCatalog.DefaultFinish;
            }
            Log.Info("Restored newer settings from features.json");
        }
        catch (Exception ex)
        {
            Log.Error("features.json could not be read", ex);
        }
    }

    private void SaveLocked()
    {
        // The mirror goes first: whatever happens to config.json afterwards, the buttons are on disk.
        var features = JsonSerializer.Serialize(new FeatureData
        {
            Scenes = _config.Scenes,
            ActiveSceneId = _config.ActiveSceneId,
            AutoScenes = _config.AutoScenes,
            Deck = _config.Deck,
            LogoModel = _config.Ui.LogoModel,
            LogoFinish = _config.Ui.LogoFinish,
        }, JsonOptions);
        if (features != _savedFeatures)
        {
            WriteAtomic(FeaturesPath, features);
            _savedFeatures = features;
        }
        WriteAtomic(ConfigPath, JsonSerializer.Serialize(_config, JsonOptions));
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class FeatureData
    {
        public List<AudioScene>? Scenes { get; set; }
        public string? ActiveSceneId { get; set; }
        public bool AutoScenes { get; set; }
        public DeckConfig? Deck { get; set; }
        public string? LogoModel { get; set; }
        public string? LogoFinish { get; set; }
    }

    internal static void Normalize(AppConfig c)
    {
        c.Server ??= new ServerSettings();
        c.Ui ??= new UiSettings();
        c.Ui.Language = Localization.L.Normalize(c.Ui.Language);
        c.Ui.LogoModel = LogoCatalog.NormalizeModel(c.Ui.LogoModel);
        c.Ui.LogoFinish = LogoCatalog.NormalizeFinish(c.Ui.LogoFinish);
        c.Audio ??= new AudioSettings();
        c.Sounds ??= new();
        c.Profiles ??= new();
        c.PairedDevices ??= new();

        if (c.Profiles.Count == 0) c.Profiles.Add(new Profile { Name = "Main" });
        if (c.ActiveProfileId is null || c.Profiles.All(p => p.Id != c.ActiveProfileId))
            c.ActiveProfileId = c.Profiles[0].Id;

        var known = c.Sounds.Select(s => s.Id).ToHashSet();
        foreach (var p in c.Profiles)
        {
            p.Layout ??= new();
            p.Layout = p.Layout.Where(known.Contains).Distinct().ToList();
        }

        c.Audio.MonitorVolume = Math.Clamp(c.Audio.MonitorVolume, 0f, 1.5f);
        c.Audio.VirtualVolume = Math.Clamp(c.Audio.VirtualVolume, 0f, 1.5f);
        c.Audio.MicVolume = Math.Clamp(c.Audio.MicVolume, 0f, 2f);
        c.Audio.LatencyMs = Math.Clamp(c.Audio.LatencyMs, 10, 200);
        foreach (var s in c.Sounds) s.Volume = Math.Clamp(s.Volume, 0f, 1.5f);

        c.Scenes ??= new();
        c.Scenes.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.Id));
        var hotkeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var autoApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in c.Scenes)
        {
            s.Name = string.IsNullOrWhiteSpace(s.Name) ? "Scene" : s.Name;
            s.Icon = string.IsNullOrWhiteSpace(s.Icon) || s.Icon.Length > 40 ? "headphones" : s.Icon;
            s.Apps = (s.Apps ?? new())
                .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Key))
                .GroupBy(a => a.Key.Trim().ToLowerInvariant())
                .Select(g => new SceneApp { Key = g.Key, Name = g.Last().Name ?? g.Key, Volume = Math.Clamp(g.Last().Volume, 0f, 1f), Muted = g.Last().Muted })
                .ToList();
            if (s.Channels is { } ch)
            {
                ch.MicVolume = Math.Clamp(ch.MicVolume, 0f, 2f);
                ch.VirtualVolume = Math.Clamp(ch.VirtualVolume, 0f, 1.5f);
                ch.MonitorVolume = Math.Clamp(ch.MonitorVolume, 0f, 1.5f);
            }
            // At most one scene per shortcut and per trigger application.
            s.Hotkey = Scenes.Hotkey.Normalize(s.Hotkey);
            if (s.Hotkey is not null && !hotkeys.Add(s.Hotkey)) s.Hotkey = null;
            s.AutoApp = string.IsNullOrWhiteSpace(s.AutoApp) ? null : s.AutoApp.Trim().ToLowerInvariant();
            if (s.AutoApp is not null && !autoApps.Add(s.AutoApp)) s.AutoApp = null;
        }
        if (c.ActiveSceneId is not null && c.Scenes.All(s => s.Id != c.ActiveSceneId)) c.ActiveSceneId = null;

        c.Deck ??= new DeckConfig();
        Deck.DeckRules.Normalize(c.Deck);
    }
}
