using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDeck.Core.Diagnostics;
using GameDeck.Core.Localization;

namespace GameDeck.App.Web;

public sealed record YouTubeTrack(string Id, string Title, string Channel, string Art, double Duration, double Position, bool Paused, int Volume, bool IsAd);
public sealed record YouTubeItem(string Id, string Title, string Channel, string Duration, string Art);
public sealed record YouTubePlaylist(string Id, string Title);
public sealed record YouTubeStateDto(
    bool Connected, YouTubeTrack? Track, string Query, bool Searching,
    IReadOnlyList<YouTubeItem> Results, IReadOnlyList<YouTubeItem> UpNext, string UpNextKind,
    IReadOnlyList<string> Queue, IReadOnlyList<YouTubePlaylist> Playlists, string? Notice);
public sealed record YouTubeAction(string? Action, string? Text, string? Id, int? Index, double? Value);

/// <summary>What a line typed in the YouTube section asks for.</summary>
public sealed record YouTubeCommand(string? Control, double? Value, IReadOnlyList<string> Searches)
{
    public const int MaxLines = 200;
    private const int MaxLineLength = 200;

    public static YouTubeCommand Parse(string? text)
    {
        var lines = (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Length > MaxLineLength ? l[..MaxLineLength] : l).Take(MaxLines).ToList();
        if (lines.Count != 1) return new(null, null, lines);

        var plain = RemoveDiacritics(lines[0]).ToLowerInvariant();
        // The command words of every interface language, compared without accents (й loses its breve too).
        var control = plain switch
        {
            "pause" or "stop" or "pauza" or "pausa" or "pausar" or "пауза" => "pause",
            "play" or "resume" or "continua" or "porneste" or "continuar" or "reanudar" or "tocar" or "weiter" or "abspielen"
                or "reprendre" or "lecture" or "wznow" or "graj" or "продолжить" or "играть" => "play",
            "next" or "skip" or "urmatoarea" or "urmatorul" or "siguiente" or "proxima" or "proximo" or "nachstes" or "naechstes"
                or "suivant" or "suivante" or "nastepny" or "nastepna" or "следующая" or "следующии" or "дальше" => "next",
            "previous" or "back" or "precedenta" or "precedentul" or "inapoi" or "anterior" or "zuruck" or "zurueck" or "vorheriges"
                or "precedent" or "precedente" or "poprzedni" or "poprzednia" or "предыдущая" or "предыдущии" or "назад" => "previous",
            _ => null,
        };
        if (control is not null) return new(control, null, []);

        var volume = Regex.Match(plain, @"^(?:volum|volume|volumen|lautstarke|lautstaerke|g[lł]osnosc|громкость)\s+(\d{1,3})$");
        if (volume.Success) return new("volume", Math.Min(100, int.Parse(volume.Groups[1].Value, CultureInfo.InvariantCulture)), []);
        return new(null, null, lines);
    }

    private static string RemoveDiacritics(string value) =>
        new(value.Normalize(FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    private const NormalizationForm FormD = NormalizationForm.FormD;
}

/// <summary>
/// Links the dashboard with the browser extension in the open YouTube tab. It only reacts to messages
/// from either side, so it costs nothing while nobody uses it.
/// </summary>
public sealed partial class YouTubeBridge(Func<string> language)
{
    /// <summary>Fixed by the public key in extension/manifest.json; the same in Chrome and Edge.</summary>
    public const string ExtensionOrigin = "chrome-extension://mgmhhoehghnaomboibjdhpkkomhaegck";
    private const int MaxMessageBytes = 256 * 1024;
    private const int MaxListItems = 30;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<string> _queue = [];
    private WebSocket? _socket;
    private YouTubeTrack? _track;
    private string _query = "";
    private bool _searching;
    private bool _playFirstResult;
    private int _requestId;
    private List<YouTubeItem> _results = [];
    private List<YouTubeItem> _upNext = [];
    private string _upNextKind = "related";
    private List<YouTubePlaylist> _playlists = [];
    private string? _notice;

    public event Action? Changed;

    public YouTubeStateDto Snapshot()
    {
        lock (_gate)
            return new(_socket is not null, _track, _query, _searching, _results.ToList(), _upNext.ToList(), _upNextKind, _queue.ToList(), _playlists.ToList(), _notice);
    }

    public async Task ApplyAsync(YouTubeAction a)
    {
        switch (a.Action)
        {
            case "command":
                await RunCommandAsync(YouTubeCommand.Parse(a.Text));
                break;
            case "control" when a.Text is "play" or "pause" or "toggle" or "next" or "previous" or "volume" or "seek":
                await SendRequiredAsync(new { type = "control", action = a.Text, value = a.Value ?? 0 });
                break;
            case "open" when IsVideoId(a.Id):
                await SendRequiredAsync(new { type = "openVideo", id = a.Id });
                break;
            case "playlist" when IsPlaylistId(a.Id):
                await SendRequiredAsync(new { type = "openPlaylist", id = a.Id });
                break;
            case "refreshPlaylists":
                await SendRequiredAsync(new { type = "refreshPlaylists" });
                break;
            case "clearQueue":
                lock (_gate) _queue.Clear();
                Changed?.Invoke();
                break;
            case "removeQueued" when a.Index is { } index:
                lock (_gate)
                {
                    if (index >= 0 && index < _queue.Count) _queue.RemoveAt(index);
                }
                Changed?.Invoke();
                break;
            default:
                throw new ApiException(400, L.T("youtube.badRequest"));
        }
    }

    private async Task RunCommandAsync(YouTubeCommand command)
    {
        if (command.Control is not null)
        {
            await SendRequiredAsync(new { type = "control", action = command.Control, value = command.Value ?? 0 });
            return;
        }
        if (command.Searches.Count == 0) throw new ApiException(400, L.T("youtube.badRequest"));
        EnsureConnected();
        lock (_gate)
        {
            _queue.AddRange(command.Searches.Skip(1));
            if (_queue.Count > YouTubeCommand.MaxLines) _queue.RemoveRange(YouTubeCommand.MaxLines, _queue.Count - YouTubeCommand.MaxLines);
        }
        // One song is the person's choice from the results; a written list plays by itself.
        await SearchAsync(command.Searches[0], command.Searches.Count > 1);
    }

    private async Task SearchAsync(string query, bool playFirstResult)
    {
        int id;
        lock (_gate)
        {
            id = ++_requestId;
            _query = query;
            _searching = true;
            _playFirstResult = playFirstResult;
            _results = [];
            _notice = null;
        }
        Changed?.Invoke();
        await SendAsync(new { type = "search", requestId = id, query, autoplay = playFirstResult });
    }

    private async Task PlayNextQueuedAsync()
    {
        string? next = null;
        lock (_gate)
        {
            if (_queue.Count > 0)
            {
                next = _queue[0];
                _queue.RemoveAt(0);
            }
        }
        if (next is not null) await SearchAsync(next, playFirstResult: true);
        else Changed?.Invoke();
    }

    public async Task HandleExtensionAsync(WebSocket socket, CancellationToken ct)
    {
        lock (_gate) _socket = socket;
        Log.Info("YouTube extension connected");
        Changed?.Invoke();
        await SendAsync(new { type = "config", language = language() });
        try
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes) break;
                if (!result.EndOfMessage) continue;
                if (result.MessageType == WebSocketMessageType.Text) await HandleMessageAsync(message.ToArray());
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // the tab or browser went away
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    _track = null;
                    _searching = false;
                    _upNext = [];
                }
            }
            Log.Info("YouTube extension disconnected");
            Changed?.Invoke();
        }
    }

    internal async Task HandleMessageAsync(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            switch (Text(root, "type", 20))
            {
                case "track":
                    lock (_gate) _track = ReadTrack(root);
                    Changed?.Invoke();
                    break;
                case "results":
                    await HandleResultsAsync(root);
                    break;
                case "upNext":
                    lock (_gate)
                    {
                        _upNext = ReadList(root, ReadItem, MaxListItems);
                        _upNextKind = Text(root, "kind", 20) == "playlist" ? "playlist" : "related";
                    }
                    Changed?.Invoke();
                    break;
                case "playlists":
                    lock (_gate) _playlists = ReadList(root, ReadPlaylist, 100);
                    Changed?.Invoke();
                    break;
                case "ended":
                    await PlayNextQueuedAsync();
                    break;
                case "notice":
                    lock (_gate) _notice = NoticeCode(Text(root, "code", 30));
                    Changed?.Invoke();
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            Log.Warn("Ignored a malformed message from the YouTube extension", ex);
        }
    }

    private async Task HandleResultsAsync(JsonElement root)
    {
        var requestId = root.TryGetProperty("requestId", out var r) && r.TryGetInt32(out var value) ? value : -1;
        var items = ReadList(root, ReadItem, MaxListItems);
        var query = Text(root, "query", 200);
        bool playNext;
        lock (_gate)
        {
            // 0 is the page's own list changing, negative is a search from the extension panel.
            if (requestId > 0 ? requestId != _requestId : requestId == 0 && _searching) return;
            if (query.Length > 0) _query = query;
            _results = items;
            playNext = requestId > 0 && _playFirstResult && items.Count == 0;
            _searching = false;
            _notice = items.Count > 0 ? null : NoticeCode(Text(root, "error", 30)) ?? "notFound";
        }
        if (requestId != 0)
        {
            var how = Text(root, "how", 30);
            Log.Info($"YouTube search finished with {items.Count} results ({(SearchSource().IsMatch(how) ? how : "unknown")})");
        }
        if (playNext) await PlayNextQueuedAsync();
        else Changed?.Invoke();
    }

    private static string? NoticeCode(string code) => code is "notFound" or "searchBoxMissing" or "noVideo" or "needsClick" ? code : null;

    private static YouTubeTrack? ReadTrack(JsonElement root)
    {
        if (!root.TryGetProperty("track", out var t) || t.ValueKind != JsonValueKind.Object) return null;
        var id = Text(t, "id", 20);
        if (!IsVideoId(id)) return null;
        return new(id, Text(t, "title", 200), Text(t, "channel", 120), Thumbnail(id), Number(t, "duration"), Number(t, "position"),
            t.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True, (int)Math.Clamp(Number(t, "volume"), 0, 100),
            t.TryGetProperty("isAd", out var ad) && ad.ValueKind == JsonValueKind.True);
    }

    private static YouTubeItem? ReadItem(JsonElement e)
    {
        var id = Text(e, "id", 20);
        var title = Text(e, "title", 200);
        return IsVideoId(id) && title.Length > 0 ? new(id, title, Text(e, "channel", 120), Text(e, "duration", 12), Thumbnail(id)) : null;
    }

    private static YouTubePlaylist? ReadPlaylist(JsonElement e)
    {
        var id = Text(e, "id", 80);
        var title = Text(e, "title", 120);
        return IsPlaylistId(id) && title.Length > 0 ? new(id, title) : null;
    }

    private static List<T> ReadList<T>(JsonElement root, Func<JsonElement, T?> read, int max) where T : class =>
        root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(read).OfType<T>().Take(max).ToList()
            : [];

    private static string Text(JsonElement e, string name, int max)
    {
        var value = e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        return value.Length > max ? value[..max] : value;
    }

    private static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.TryGetDouble(out var value) && double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static string Thumbnail(string id) => $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";
    private static bool IsVideoId(string? id) => id is not null && VideoId().IsMatch(id);
    private static bool IsPlaylistId(string? id) => id is not null && PlaylistId().IsMatch(id);

    [GeneratedRegex(@"^[\w-]{6,20}$")] private static partial Regex VideoId();
    [GeneratedRegex(@"^[\w-]{2,80}$")] private static partial Regex PlaylistId();
    [GeneratedRegex(@"^[a-z0-9-]{1,30}$")] private static partial Regex SearchSource();

    private void EnsureConnected()
    {
        lock (_gate)
        {
            if (_socket is null) throw new ApiException(409, L.T("youtube.notConnected"));
        }
    }

    private async Task SendRequiredAsync(object message)
    {
        EnsureConnected();
        await SendAsync(message);
    }

    private async Task SendAsync(object message)
    {
        WebSocket? socket;
        lock (_gate) socket = _socket;
        if (socket?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _sendLock.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            Log.Warn("Could not reach the YouTube extension", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
