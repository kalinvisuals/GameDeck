using System.Text.Json.Nodes;
using GameDeck.App.Web;
using GameDeck.Core.Config;
using GameDeck.Core.Diagnostics;
using GameDeck.Core.Localization;
using GameDeck.Core.Presence;

namespace GameDeck.App.Presence;

/// <param name="Status">"off", "idle" (on, nothing to show), "waiting" (Discord not running) or "connected".</param>
/// <param name="User">Discord account the activity shows on, while connected.</param>
/// <param name="Error">What Discord refused in the last update, in Discord's own words.</param>
/// <param name="ActivePresetId">The activity chosen for "manual"; null = none.</param>
/// <param name="ActiveMusicPresetId">The song chosen for "manual"; null = none.</param>
/// <param name="AppliedAt">When the activity was applied (Unix ms): its timer counts from here.</param>
/// <param name="MusicAppliedAt">When the song was applied (Unix ms): its progress counts from here.</param>
/// <param name="ActivityMode">"manual" or "auto" (GameDeck shows no activity; Discord shows the game it detects).</param>
/// <param name="MusicMode">"manual" or "auto" (the song playing now).</param>
/// <param name="AutoSong">The song playing now, while <paramref name="MusicMode"/> is "auto".</param>
/// <param name="MusicApps">Programs that can be allowed for automatic music: GameDeck's YouTube, the ones chosen, the ones seen playing.</param>
public sealed record PresenceStateDto(
    bool Enabled, string Status, string? User, string? Error,
    string? ActivePresetId, string? ActiveMusicPresetId, long? AppliedAt, long? MusicAppliedAt,
    IReadOnlyList<PresencePreset> Presets,
    string ActivityMode, string MusicMode,
    AutoMusicSettings AutoMusic, AutoSongDto? AutoSong, IReadOnlyList<MediaApp> MusicApps);

/// <param name="Preset">How the song card looks, ready for the preview.</param>
/// <param name="StartedAt">When the song started (Unix ms).</param>
public sealed record AutoSongDto(string App, string AppName, PresencePreset Preset, long StartedAt);

/// <summary>Options of the Discord screen; null fields stay as they are.</summary>
public sealed record PresenceOptionsPatch(string? ActivityMode = null, string? MusicMode = null, List<string>? MusicApps = null, bool? FindCovers = null);

/// <summary>
/// Keeps the chosen activity and song on the Discord profile. Discord shows one activity per
/// application, so each card has its own application and connection: "GameDeck" for the activity,
/// "GameDeck Music" for the song. Each card is either "manual" (what the person chose) or "auto"
/// (the activity: nothing from GameDeck, so Discord's own game detection shows; the song: what plays
/// now), never a mix. Costs nothing while off; a card's connection runs only while that card has
/// something to show, looks for Discord every 15 s while it isn't running, and otherwise only writes
/// when something changed (Discord counts timers and progress bars itself), never more than Discord's
/// 5 updates per 20 s. Automatic music follows GameDeck's YouTube and Windows' media controls through
/// events. The activities belong to this process, so Discord removes them when GameDeck closes.
/// </summary>
public sealed class PresenceService : IAsyncDisposable
{
    /// <summary>The "GameDeck" application in the Discord Developer Portal. Public, not a secret.</summary>
    public const string ClientId = "1574850603044184146";
    /// <summary>The "GameDeck Music" application, for the song card shown next to the activity.</summary>
    public const string MusicClientId = "1574863869208170496";
    public static readonly string[] Slots = ["activity", "music"];

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinSendGap = TimeSpan.FromSeconds(4);
    /// <summary>How long after GameDeck closed a card's timer still carries on instead of starting from zero.</summary>
    private static readonly TimeSpan ResumeWithin = TimeSpan.FromHours(6);

    private const string YouTubeIcon = "https://www.gstatic.com/youtube/img/branding/favicon/favicon_144x144.png";
    private static string Favicon(string domain) => $"https://t3.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url=https://{domain}&size=128";

    private readonly ConfigStore _store;
    private readonly Dictionary<string, Channel> _channels;
    private readonly MediaSessions _media = new();
    private readonly object _autoGate = new();
    private Func<YouTubeTrack?>? _youtube;
    private NowPlaying? _auto;
    private bool _started;

    public event Action? Changed;

    public PresenceService(ConfigStore store)
    {
        _store = store;
        _channels = new()
        {
            ["activity"] = new Channel(this, "activity", ClientId),
            ["music"] = new Channel(this, "music", MusicClientId),
        };
        _media.Changed += OnMusicChanged;
    }

    /// <summary>Called once by the app (never by tests): connects if presence was left on.</summary>
    public void Start()
    {
        // Timers carry on after a restart (an update, a reboot); after a long break they start again.
        var (activityAt, musicAt) = _store.Read(c => (c.Presence.ActivityAppliedAt, c.Presence.MusicAppliedAt));
        foreach (var (slot, at) in new[] { ("activity", activityAt), ("music", musicAt) })
            if (at is { } when && DateTimeOffset.UtcNow - when < ResumeWithin && when <= DateTimeOffset.UtcNow) _channels[slot].Resume(when);
        _started = true;
        SyncSources();
        SyncChannels();
    }

    /// <summary>GameDeck's own YouTube section, the first source of automatic music.</summary>
    public void UseYouTube(YouTubeBridge bridge)
    {
        _youtube = () => bridge.Snapshot().Track;
        bridge.Changed += OnMusicChanged;
    }

    // ---------------- dashboard actions ----------------

    public PresenceStateDto Snapshot()
    {
        var (enabled, activeId, musicId, presets, activityMode, musicMode, auto) = _store.Read(c =>
            (c.Presence.Enabled, c.Presence.ActivePresetId, c.Presence.ActiveMusicPresetId, c.Presence.Presets.Select(Copy).ToList(),
             c.Presence.ActivityMode, c.Presence.MusicMode,
             new AutoMusicSettings { Apps = c.Presence.AutoMusic.Apps.ToList(), FindCovers = c.Presence.AutoMusic.FindCovers }));
        var activity = _channels["activity"].Read();
        var music = _channels["music"].Read();
        NowPlaying? song;
        lock (_autoGate) song = _auto;
        var anything = Shown("activity").Preset is not null || Shown("music").Preset is not null;
        var status = !enabled ? "off" : !anything ? "idle" : activity.Connected || music.Connected ? "connected" : "waiting";

        var apps = new Dictionary<string, string> { [PresenceRules.GameDeckYouTube] = "GameDeck YouTube" };
        foreach (var key in auto.Apps) apps.TryAdd(key, MediaSessions.AppName(key, key));
        foreach (var seen in _media.Seen) apps.TryAdd(seen.Key, seen.Name);

        return new(enabled, status, activity.User ?? music.User, enabled ? activity.Error ?? music.Error : null,
            activeId, musicId,
            activeId is null ? null : activity.AppliedAt.ToUnixTimeMilliseconds(),
            musicId is null ? null : music.AppliedAt.ToUnixTimeMilliseconds(),
            presets, activityMode, musicMode, auto,
            song is null || musicMode != "auto" ? null : new AutoSongDto(song.App, SourceName(song.App), AutoPreset(song), song.Started.ToUnixTimeMilliseconds()),
            apps.Select(a => new MediaApp(a.Key, a.Value)).ToList());
    }

    public void SetOptions(PresenceOptionsPatch patch)
    {
        static string? Mode(string? m) => m is null ? null : PresenceRules.Modes.Contains(m) ? m : throw new ApiException(400, L.T("presence.notFound"));
        var (activityMode, musicMode) = (Mode(patch.ActivityMode), Mode(patch.MusicMode));
        _store.Update(c =>
        {
            var p = c.Presence;
            if (activityMode is not null) p.ActivityMode = activityMode;
            if (musicMode is not null) p.MusicMode = musicMode;
            if (patch.MusicApps is not null) p.AutoMusic.Apps = patch.MusicApps;
            if (patch.FindCovers is { } covers) p.AutoMusic.FindCovers = covers;
            // Choosing what a card shows means wanting it on the profile.
            if (activityMode is not null || musicMode is not null) p.Enabled = true;
        }, notify: false);
        if (activityMode is not null || musicMode is not null) Log.Info($"Discord cards: activity {activityMode ?? "same"}, music {musicMode ?? "same"}");
        SyncSources();
        OnMusicChanged();
        Refresh();
    }

    public void SetEnabled(bool enabled)
    {
        _store.Update(c => c.Presence.Enabled = enabled, notify: false);
        if (enabled) foreach (var ch in _channels.Values) ch.Restart();
        Log.Info($"Discord presence {(enabled ? "on" : "off")}");
        SyncSources();
        OnMusicChanged();
        Refresh();
    }

    /// <summary>For the Smart Deck: on → off, off → on.</summary>
    public void Toggle() => SetEnabled(!_store.Read(c => c.Presence.Enabled));

    /// <summary>For the Smart Deck: a card's mode, manual → auto → manual.</summary>
    public void ToggleMode(string slot)
    {
        var current = _store.Read(c => slot == "music" ? c.Presence.MusicMode : c.Presence.ActivityMode);
        var next = current == "auto" ? "manual" : "auto";
        SetOptions(slot == "music" ? new PresenceOptionsPatch(MusicMode: next) : new PresenceOptionsPatch(ActivityMode: next));
    }

    public PresencePreset CreatePreset(PresencePreset body)
    {
        var preset = Copy(body);
        preset.Id = SoundButton.NewId();
        PresenceRules.Clean(preset);
        _store.Update(c =>
        {
            if (c.Presence.Presets.Count >= PresenceRules.MaxPresets) throw new ApiException(400, L.T("presence.tooMany", PresenceRules.MaxPresets));
            c.Presence.Presets.Add(preset);
        }, notify: false);
        Refresh();
        return Copy(preset);
    }

    /// <summary>Saves a preset. A shown one changes on Discord right away; its timer restarts when the song or timer changed.</summary>
    public PresencePreset SavePreset(string id, PresencePreset body)
    {
        var preset = Copy(body);
        preset.Id = id;
        var (restart, slot) = _store.Update(c =>
        {
            var index = c.Presence.Presets.FindIndex(p => p.Id == id);
            if (index < 0) throw new ApiException(404, L.T("presence.notFound"));
            var old = c.Presence.Presets[index];
            preset.Kind = old.Kind; // a preset stays on its card
            PresenceRules.Clean(preset);
            c.Presence.Presets[index] = preset;
            var shown = c.Presence.ActivePresetId == id || c.Presence.ActiveMusicPresetId == id;
            return (shown && (old.Details != preset.Details || old.Timer != preset.Timer || old.DurationSec != preset.DurationSec
                || old.PositionSec != preset.PositionSec || old.CountdownMinutes != preset.CountdownMinutes), PresenceRules.SlotOf(preset));
        }, notify: false);
        if (restart) _channels[slot].Restart();
        Refresh();
        return Copy(preset);
    }

    public void DeletePreset(string id)
    {
        _store.Update(c =>
        {
            if (c.Presence.Presets.RemoveAll(p => p.Id == id) == 0) throw new ApiException(404, L.T("presence.notFound"));
            if (c.Presence.ActivePresetId == id) c.Presence.ActivePresetId = null;
            if (c.Presence.ActiveMusicPresetId == id) c.Presence.ActiveMusicPresetId = null;
        }, notify: false);
        Refresh();
    }

    /// <summary>Shows the preset on its card: that card turns to "manual", presence turns on, the timer starts from now.</summary>
    public void Activate(string id)
    {
        var slot = _store.Update(c =>
        {
            var p = c.Presence.Presets.FirstOrDefault(x => x.Id == id) ?? throw new ApiException(404, L.T("presence.notFound"));
            var s = PresenceRules.SlotOf(p);
            if (s == "music")
            {
                c.Presence.ActiveMusicPresetId = id;
                c.Presence.MusicMode = "manual";
            }
            else
            {
                c.Presence.ActivePresetId = id;
                c.Presence.ActivityMode = "manual";
            }
            c.Presence.Enabled = true;
            return s;
        }, notify: false);
        _channels[slot].Restart();
        SyncSources();
        OnMusicChanged();
        Refresh();
    }

    /// <summary>Takes one card off the profile (back to "manual" with nothing chosen); the other stays.</summary>
    public void Hide(string slot)
    {
        if (!Slots.Contains(slot)) throw new ApiException(400, L.T("presence.notFound"));
        _store.Update(c =>
        {
            if (slot == "music")
            {
                c.Presence.ActiveMusicPresetId = null;
                c.Presence.MusicMode = "manual";
            }
            else
            {
                c.Presence.ActivePresetId = null;
                c.Presence.ActivityMode = "manual";
            }
        }, notify: false);
        SyncSources();
        OnMusicChanged();
        Refresh();
    }

    /// <summary>The card's timer or song starts again from now.</summary>
    public void RestartTimer(string slot)
    {
        if (!_channels.TryGetValue(slot, out var ch)) throw new ApiException(400, L.T("presence.notFound"));
        ch.Restart();
        Refresh();
    }

    // ---------------- automatic music ----------------

    /// <summary>Watches Windows' media controls only while the song card is automatic and may use a program other than GameDeck.</summary>
    private void SyncSources()
    {
        if (!_started) return;
        var watch = _store.Read(c => c.Presence.Enabled && c.Presence.MusicMode == "auto"
            && c.Presence.AutoMusic.Apps.Any(a => a != PresenceRules.GameDeckYouTube));
        if (watch) _ = _media.StartAsync();
        else if (_media.Running) _media.Stop();
    }

    /// <summary>Picks the song to show from the allowed programs; GameDeck's YouTube first, then in the order chosen.</summary>
    private void OnMusicChanged()
    {
        var (on, apps, covers) = _store.Read(c => (c.Presence.Enabled && c.Presence.MusicMode == "auto", c.Presence.AutoMusic.Apps.ToList(), c.Presence.AutoMusic.FindCovers));
        NowPlaying? pick = null;
        if (on)
        {
            if (apps.Contains(PresenceRules.GameDeckYouTube) && _youtube?.Invoke() is { Paused: false, IsAd: false } t && t.Title.Length > 0)
            {
                var (artist, song) = SongLookup.SplitVideoTitle(t.Title, t.Channel);
                pick = new NowPlaying(PresenceRules.GameDeckYouTube, song, artist, "", (int)t.Duration,
                    DateTimeOffset.UtcNow - TimeSpan.FromSeconds(t.Position), $"https://www.youtube.com/watch?v={t.Id}", $"https://i.ytimg.com/vi/{t.Id}/hqdefault.jpg");
            }
            else
            {
                var playing = _media.Playing;
                pick = apps.Select(a => playing.GetValueOrDefault(a)).FirstOrDefault(p => p is not null);
            }
        }

        bool changed;
        lock (_autoGate)
        {
            // Same song from the same program, about the same position: nothing to tell Discord.
            var same = pick is not null && _auto is not null && pick.App == _auto.App && pick.Title == _auto.Title && pick.Artist == _auto.Artist
                && (pick.Started - _auto.Started).Duration() < TimeSpan.FromSeconds(3);
            changed = !same && !(pick is null && _auto is null);
            if (changed) _auto = pick;
        }
        if (!changed) return;
        if (pick is { Cover.Length: 0 } && covers) _ = FindCoverAsync(pick);
        Refresh();
    }

    private async Task FindCoverAsync(NowPlaying song)
    {
        var (cover, album) = await SongLookup.FindCoverAsync(song.Artist, song.Title);
        if (cover.Length == 0) return;
        lock (_autoGate)
        {
            if (_auto is null || _auto.App != song.App || _auto.Title != song.Title) return;
            _auto = _auto with { Cover = cover, Album = _auto.Album.Length > 0 ? _auto.Album : album };
        }
        Refresh();
    }

    private static string SourceName(string app) => app == PresenceRules.GameDeckYouTube ? "YouTube" : MediaSessions.AppName(app, app);

    /// <summary>The song card for a song playing now, in the same shape as a hand-made song.</summary>
    private static PresencePreset AutoPreset(NowPlaying n)
    {
        var name = SourceName(n.App);
        var icon = n.App switch
        {
            PresenceRules.GameDeckYouTube => YouTubeIcon,
            "spotify" => Favicon("open.spotify.com"),
            "applemusic" => Favicon("music.apple.com"),
            "deezer" => Favicon("deezer.com"),
            "tidal" => Favicon("tidal.com"),
            _ => "",
        };
        var p = new PresencePreset
        {
            Id = "auto", Kind = "music", Name = name, Details = n.Title, State = n.Artist, LargeText = n.Album, LargeImage = n.Cover,
            SmallImage = icon, SmallText = icon.Length > 0 ? name : "", DetailsUrl = n.Link,
            Buttons = n.Link.Length > 0 ? [new PresenceButton { Label = L.T("presence.listenOn", name), Url = n.Link }] : [],
            Timer = n.DurationSec > 0 ? "track" : "elapsed", DurationSec = n.DurationSec, StatusDisplay = "details",
        };
        PresenceRules.Clean(p);
        return p;
    }

    // ---------------- connections ----------------

    private void Refresh()
    {
        SyncChannels();
        foreach (var ch in _channels.Values) ch.Wake();
        Changed?.Invoke();
    }

    /// <summary>Runs a card's connection exactly while presence is on and the card has something to show.</summary>
    private void SyncChannels()
    {
        if (!_started) return;
        _channels["activity"].SetRunning(Shown("activity").Preset is not null);
        _channels["music"].SetRunning(Shown("music").Preset is not null);
    }

    /// <summary>What a card shows now, and when it started when that isn't the card's own time (a song playing elsewhere).</summary>
    private (PresencePreset? Preset, DateTimeOffset? Started) Shown(string slot)
    {
        NowPlaying? song;
        lock (_autoGate) song = _auto;
        var (enabled, mode, preset) = _store.Read(c =>
        {
            var music = slot == "music";
            var id = music ? c.Presence.ActiveMusicPresetId : c.Presence.ActivePresetId;
            return (c.Presence.Enabled, music ? c.Presence.MusicMode : c.Presence.ActivityMode,
                c.Presence.Presets.FirstOrDefault(p => p.Id == id) is { } p ? Copy(p) : null);
        });
        if (!enabled) return (null, null);
        if (mode == "auto")
            // Automatic activity: nothing from GameDeck, so Discord's own game detection is the only one.
            return slot == "music" && song is not null ? (AutoPreset(song), song.Started) : (null, null);
        return (preset, null);
    }

    private static PresencePreset Copy(PresencePreset p) => new()
    {
        Id = p.Id, Label = p.Label, Kind = p.Kind, Type = p.Type, Name = p.Name,
        Details = p.Details, DetailsUrl = p.DetailsUrl, State = p.State, StateUrl = p.StateUrl,
        LargeImage = p.LargeImage, LargeText = p.LargeText, SmallImage = p.SmallImage, SmallText = p.SmallText,
        Buttons = (p.Buttons ?? new()).Where(b => b is not null).Select(b => new PresenceButton { Label = b.Label, Url = b.Url }).ToList(),
        Timer = p.Timer, CountdownMinutes = p.CountdownMinutes, DurationSec = p.DurationSec, PositionSec = p.PositionSec,
        StatusDisplay = p.StatusDisplay,
    };

    public async ValueTask DisposeAsync()
    {
        _media.Dispose();
        await Task.WhenAll(_channels.Values.Select(ch => ch.StopAsync()));
    }

    /// <summary>One card on the profile: one Discord application, one connection.</summary>
    private sealed class Channel(PresenceService owner, string slot, string clientId)
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _wake = new(0, 1);
        private CancellationTokenSource? _run;
        private Task? _loop;
        private DiscordIpc? _ipc;
        private string? _user;
        private string? _error;
        private DateTimeOffset _appliedAt = DateTimeOffset.UtcNow;

        public (bool Connected, string? User, string? Error, DateTimeOffset AppliedAt) Read()
        {
            lock (_gate) return (_ipc is not null, _ipc is not null ? _user : null, _run is not null ? _error : null, _appliedAt);
        }

        /// <summary>The timer starts from now; the time is saved so it survives GameDeck restarting.</summary>
        public void Restart()
        {
            var now = DateTimeOffset.UtcNow;
            lock (_gate) _appliedAt = now;
            owner._store.Update(c =>
            {
                if (slot == "music") c.Presence.MusicAppliedAt = now;
                else c.Presence.ActivityAppliedAt = now;
            }, notify: false);
        }

        /// <summary>Carries on a timer saved before GameDeck closed.</summary>
        public void Resume(DateTimeOffset appliedAt)
        {
            lock (_gate) _appliedAt = appliedAt;
        }

        public void Wake()
        {
            try { if (_wake.CurrentCount == 0) _wake.Release(); }
            catch (SemaphoreFullException) { /* already awake */ }
        }

        public void SetRunning(bool run)
        {
            lock (_gate)
            {
                if (run && _run is null)
                {
                    _run = new CancellationTokenSource();
                    var token = _run.Token;
                    _loop = Task.Run(() => RunAsync(token));
                }
                else if (!run && _run is not null)
                {
                    _run.Cancel();
                    _run = null;
                    _error = null;
                }
            }
        }

        public async Task StopAsync()
        {
            Task? loop;
            lock (_gate)
            {
                _run?.Cancel();
                _run = null;
                loop = _loop;
            }
            if (loop is not null)
            {
                try { await loop.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { /* exiting anyway */ }
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                DiscordIpc? ipc = null;
                try
                {
                    ipc = await DiscordIpc.TryConnectAsync(clientId, ct);
                    if (ipc is null)
                    {
                        await WaitAsync(RetryDelay, ct);
                        continue;
                    }
                    lock (_gate)
                    {
                        _ipc = ipc;
                        _user = ipc.User;
                        _error = null;
                    }
                    Log.Info($"Connected to Discord for the {slot} card");
                    owner.Changed?.Invoke();

                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var listen = ipc.ListenAsync(OnReply, linked.Token);
                    var send = SendLoopAsync(ipc, linked.Token);
                    await Task.WhenAny(listen, send);
                    linked.Cancel();
                    try { await Task.WhenAll(listen, send); } catch { /* the reason is logged below */ }
                    if (listen.IsFaulted && !ct.IsCancellationRequested) Log.Info($"Discord connection ({slot}) ended: {listen.Exception?.GetBaseException().Message}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // card hidden or presence turned off
                }
                catch (Exception ex)
                {
                    Log.Warn($"Discord presence connection ({slot}) failed", ex);
                }
                finally
                {
                    if (ipc is not null)
                    {
                        if (ct.IsCancellationRequested) await ClearAsync(ipc);
                        await ipc.DisposeAsync();
                    }
                    lock (_gate)
                    {
                        if (ReferenceEquals(_ipc, ipc)) _ipc = null;
                    }
                    owner.Changed?.Invoke();
                }
                if (!ct.IsCancellationRequested)
                {
                    try { await WaitAsync(RetryDelay, ct); } catch (OperationCanceledException) { }
                }
            }
        }

        /// <summary>Sends the wanted activity whenever it differs from what Discord shows, spaced by <see cref="MinSendGap"/>.</summary>
        private async Task SendLoopAsync(DiscordIpc ipc, CancellationToken ct)
        {
            string? sent = null;
            var lastSend = DateTimeOffset.MinValue;
            while (!ct.IsCancellationRequested)
            {
                var (activity, next) = Wanted(DateTimeOffset.UtcNow);
                var json = activity?.ToJsonString() ?? "null";
                if (json != sent)
                {
                    var gap = lastSend + MinSendGap - DateTimeOffset.UtcNow;
                    if (gap > TimeSpan.Zero)
                    {
                        await Task.Delay(gap, ct);
                        continue; // read the newest wish after waiting
                    }
                    await ipc.SetActivityAsync(activity, Environment.ProcessId, ct);
                    sent = json;
                    lastSend = DateTimeOffset.UtcNow;
                }
                var wait = next is { } at ? at - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250) : Timeout.InfiniteTimeSpan;
                await WaitAsync(wait < TimeSpan.Zero && wait != Timeout.InfiniteTimeSpan ? TimeSpan.Zero : wait, ct);
            }
        }

        private (JsonObject? Activity, DateTimeOffset? Next) Wanted(DateTimeOffset now)
        {
            var (preset, started) = owner.Shown(slot);
            if (preset is null) return (null, null);
            DateTimeOffset appliedAt;
            lock (_gate) appliedAt = _appliedAt;
            return PresenceRules.Build(preset, started ?? appliedAt, now);
        }

        private void OnReply(string? error)
        {
            bool changed;
            lock (_gate)
            {
                changed = _error != error;
                _error = error;
            }
            if (error is not null) Log.Warn($"Discord refused the {slot} card: {error}");
            if (changed) owner.Changed?.Invoke();
        }

        /// <summary>Waits for <paramref name="delay"/> or until something changes.</summary>
        private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
        {
            try { await _wake.WaitAsync(delay, ct); }
            catch (ArgumentOutOfRangeException) { await _wake.WaitAsync(ct); }
        }

        private static async Task ClearAsync(DiscordIpc ipc)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ipc.SetActivityAsync(null, Environment.ProcessId, cts.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Discord drops the activity anyway once the connection closes.
            }
        }
    }
}
