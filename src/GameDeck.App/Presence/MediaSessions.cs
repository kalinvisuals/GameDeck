using GameDeck.Core.Diagnostics;
using Windows.Media.Control;

namespace GameDeck.App.Presence;

/// <summary>A song playing right now. <paramref name="Started"/> is when it would have started at normal speed.</summary>
/// <param name="App">Key of the program that plays it ("spotify", "chrome", "gamedeck-youtube").</param>
/// <param name="Link">Address of the song when known (GameDeck's YouTube), otherwise empty.</param>
/// <param name="Cover">Public cover address when known, otherwise empty (looked up later).</param>
public sealed record NowPlaying(string App, string Title, string Artist, string Album, int DurationSec, DateTimeOffset Started, string Link, string Cover);

/// <summary>A program that showed up in Windows' media controls, for the list of sources to allow.</summary>
public sealed record MediaApp(string Key, string Name);

/// <summary>
/// What plays in other programs, from Windows' media controls (the same list as the volume flyout).
/// Event driven: Windows tells us when a song, its position or its state changes, so nothing polls.
/// Runs only while automatic music is on; <see cref="Stop"/> releases everything.
/// </summary>
public sealed class MediaSessions : IDisposable
{
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly List<GlobalSystemMediaTransportControlsSession> _watched = [];
    private Dictionary<string, NowPlaying> _playing = new();
    private readonly Dictionary<string, string> _seen = new();
    private int _refreshQueued;

    /// <summary>Something started, stopped, changed song or jumped.</summary>
    public event Action? Changed;

    public bool Running { get { lock (_gate) return _manager is not null; } }

    public async Task StartAsync()
    {
        lock (_gate) if (_manager is not null) return;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_gate) _manager = manager;
            manager.SessionsChanged += OnSessionsChanged;
            Rewatch();
            Log.Info("Watching Windows media sessions for Discord");
        }
        catch (Exception ex)
        {
            Log.Warn("Windows media sessions are not available", ex);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
            foreach (var s in _watched) Unhook(s);
            _watched.Clear();
            _manager = null;
            _playing = new();
        }
    }

    /// <summary>Songs playing now, one per program.</summary>
    public IReadOnlyDictionary<string, NowPlaying> Playing { get { lock (_gate) return new Dictionary<string, NowPlaying>(_playing); } }

    /// <summary>Programs seen playing since GameDeck started.</summary>
    public IReadOnlyList<MediaApp> Seen { get { lock (_gate) return _seen.Select(p => new MediaApp(p.Key, p.Value)).OrderBy(a => a.Name).ToList(); } }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Rewatch();

    private void Rewatch()
    {
        lock (_gate)
        {
            if (_manager is null) return;
            foreach (var s in _watched) Unhook(s);
            _watched.Clear();
            foreach (var s in _manager.GetSessions())
            {
                s.MediaPropertiesChanged += OnMediaChanged;
                s.PlaybackInfoChanged += OnPlaybackChanged;
                s.TimelinePropertiesChanged += OnTimelineChanged;
                _watched.Add(s);
            }
        }
        QueueRefresh();
    }

    private void Unhook(GlobalSystemMediaTransportControlsSession s)
    {
        s.MediaPropertiesChanged -= OnMediaChanged;
        s.PlaybackInfoChanged -= OnPlaybackChanged;
        s.TimelinePropertiesChanged -= OnTimelineChanged;
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => QueueRefresh();
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => QueueRefresh();
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => QueueRefresh();

    /// <summary>Programs report several changes at once (song, state, position); they are read together once.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ = Task.Delay(300).ContinueWith(async _ =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            await RefreshAsync();
        }, TaskScheduler.Default);
    }

    private async Task RefreshAsync()
    {
        List<GlobalSystemMediaTransportControlsSession> sessions;
        lock (_gate) sessions = _watched.ToList();
        var now = DateTimeOffset.UtcNow;
        var playing = new Dictionary<string, NowPlaying>();
        foreach (var s in sessions)
        {
            try
            {
                var key = AppKey(s.SourceAppUserModelId);
                if (key.Length == 0) continue;
                lock (_gate) _seen.TryAdd(key, AppName(key, s.SourceAppUserModelId));
                if (s.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                var media = await s.TryGetMediaPropertiesAsync();
                if (media is null || string.IsNullOrWhiteSpace(media.Title)) continue;
                var timeline = s.GetTimelineProperties();
                var length = timeline.EndTime - timeline.StartTime;
                // Position is as of LastUpdatedTime; the song kept playing since.
                var position = timeline.Position - timeline.StartTime;
                if (timeline.LastUpdatedTime.Year > 2000) position += now - timeline.LastUpdatedTime;
                var duration = length > TimeSpan.Zero && length < TimeSpan.FromHours(24) ? (int)length.TotalSeconds : 0;
                if (duration > 0 && position > length) position = length;
                var artist = (string.IsNullOrWhiteSpace(media.Artist) ? media.AlbumArtist ?? "" : media.Artist).Trim();
                var title = media.Title.Trim();
                // A browser reports a video: "Artist - Song (Official Video)" by "ArtistVEVO" or "Artist - Topic".
                if (IsBrowser(key)) (artist, title) = SongLookup.SplitVideoTitle(title, artist);
                playing[key] = new NowPlaying(key, title, artist, (media.AlbumTitle ?? "").Trim(), duration,
                    now - (position > TimeSpan.Zero ? position : TimeSpan.Zero), "", "");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A program that closes while being read: skip it this time.
            }
        }

        bool changed;
        lock (_gate)
        {
            changed = !Same(_playing, playing);
            _playing = playing;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Equal songs from the same programs, allowing a few seconds of clock difference.</summary>
    private static bool Same(Dictionary<string, NowPlaying> a, Dictionary<string, NowPlaying> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var o)
            && o with { Started = p.Value.Started } == p.Value
            && (o.Started - p.Value.Started).Duration() < TimeSpan.FromSeconds(3));

    /// <summary>"Spotify.exe" → "spotify", "Chrome" → "chrome", "AppleInc.AppleMusicWin_…!App" → "applemusic".</summary>
    internal static string AppKey(string? aumid)
    {
        var id = (aumid ?? "").Trim().ToLowerInvariant();
        if (id.Length == 0) return "";
        if (id.Contains("applemusic")) return "applemusic";
        if (id.Contains("zunemusic") || id.Contains("windowsmediaplayer")) return "mediaplayer";
        if (id.Contains("spotify")) return "spotify";
        if (id.Contains("deezer")) return "deezer";
        if (id.Contains("tidal")) return "tidal";
        if (id.Contains("msedge")) return "msedge";
        id = id.Split('!')[0].Split('_')[0];
        id = Path.GetFileNameWithoutExtension(id.Replace('\\', '/').Split('/').Last());
        return new string(id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray());
    }

    internal static bool IsBrowser(string key) => key is "chrome" or "msedge" or "firefox" or "opera" or "brave" or "vivaldi";

    internal static string AppName(string key, string? aumid) => key switch
    {
        "spotify" => "Spotify",
        "applemusic" => "Apple Music",
        "deezer" => "Deezer",
        "tidal" => "TIDAL",
        "mediaplayer" => "Media Player",
        "chrome" => "Google Chrome",
        "msedge" => "Microsoft Edge",
        "firefox" => "Firefox",
        "opera" => "Opera",
        "brave" => "Brave",
        "vlc" => "VLC",
        "foobar2000" => "foobar2000",
        "itunes" => "iTunes",
        "amazonmusic" => "Amazon Music",
        _ => key.Length > 0 ? char.ToUpperInvariant(key[0]) + key[1..] : aumid ?? "",
    };

    public void Dispose() => Stop();
}
