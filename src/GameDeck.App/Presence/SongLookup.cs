using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using GameDeck.App.Web;
using GameDeck.Core.Diagnostics;
using GameDeck.Core.Localization;

namespace GameDeck.App.Presence;

/// <param name="Source">"YouTube", "Spotify", "SoundCloud", "Apple Music" or "Deezer".</param>
/// <param name="DurationSec">0 when the service doesn't say.</param>
public sealed record SongInfo(string Source, string Title, string Artist, string Album, string Cover, int DurationSec, string Link);

public sealed record SongLookupRequest(string? Url);

/// <summary>
/// Fills in a song from a link the person pasted: title, artist, album, cover and length, read from
/// the service's own public pages and APIs (no keys, no accounts). Runs only when asked, and only
/// ever contacts the five known services: a link elsewhere, or a short link leading elsewhere, is refused.
/// </summary>
public static partial class SongLookup
{
    private const int MaxRedirects = 5;
    private const int MaxBytes = 3 * 1024 * 1024;

    private static readonly string[] KnownHosts =
        ["youtube.com", "youtu.be", "spotify.com", "spotify.link", "soundcloud.com", "music.apple.com", "itunes.apple.com", "deezer.com", "deezer.page.link"];

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return client;
    }

    public static async Task<SongInfo> LookupAsync(string? link, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !IsKnown(uri))
            throw new ApiException(400, L.T("presence.lookupUnknown"));
        try
        {
            // Short links (spotify.link, on.soundcloud.com, deezer.page.link) lead to the song's address.
            if (IsHost(uri, "spotify.link") || IsHost(uri, "deezer.page.link") || IsHost(uri, "link.deezer.com") || IsHost(uri, "on.soundcloud.com"))
                uri = await ResolveAsync(uri, ct);

            var song = uri switch
            {
                _ when IsHost(uri, "youtube.com") || IsHost(uri, "youtu.be") => await YouTubeAsync(uri, ct),
                _ when IsHost(uri, "spotify.com") => await SpotifyAsync(uri, ct),
                _ when IsHost(uri, "soundcloud.com") => await SoundCloudAsync(uri, ct),
                _ when IsHost(uri, "music.apple.com") => await AppleAsync(uri, ct),
                _ when IsHost(uri, "deezer.com") => await DeezerAsync(uri, ct),
                _ => null,
            };
            if (song is null || song.Title.Length == 0) throw new ApiException(422, L.T("presence.lookupNotSong"));
            return song with { Link = link!.Trim() };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            Log.Info($"Song lookup failed for {uri.Host}: {ex.GetType().Name} {ex.Message}");
            throw new ApiException(502, L.T("presence.lookupFailed"));
        }
    }

    // ---------------- services ----------------

    private static async Task<SongInfo?> YouTubeAsync(Uri uri, CancellationToken ct)
    {
        var id = IsHost(uri, "youtu.be") ? uri.AbsolutePath.Trim('/') : HttpUtility.ParseQueryString(uri.Query)["v"] ?? ShortsId().Match(uri.AbsolutePath).Groups[1].Value;
        if (!VideoId().IsMatch(id ?? "")) return null;
        var watch = $"https://www.youtube.com/watch?v={id}";
        using var o = await JsonAsync($"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString(watch)}", ct);
        var (artist, song) = SplitVideoTitle(Str(o.RootElement, "title"), Str(o.RootElement, "author_name"));

        var duration = 0;
        try
        {
            var page = await TextAsync(watch, ct, ("Cookie", "CONSENT=YES+1"));
            var m = LengthSeconds().Match(page);
            if (m.Success) duration = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // the length stays empty; everything else is known
        }
        return new("YouTube", song, artist, "", $"https://i.ytimg.com/vi/{id}/hqdefault.jpg", duration, "");
    }

    private static async Task<SongInfo?> SpotifyAsync(Uri uri, CancellationToken ct)
    {
        var m = SpotifyTrack().Match(uri.AbsolutePath);
        if (!m.Success) return null;
        var page = await TextAsync($"https://open.spotify.com/embed/track/{m.Groups[1].Value}", ct);
        var data = NextData().Match(page);
        if (!data.Success) throw new FormatException("Spotify page without data");
        using var doc = JsonDocument.Parse(data.Groups[1].Value);
        var e = doc.RootElement.GetProperty("props").GetProperty("pageProps").GetProperty("state").GetProperty("data").GetProperty("entity");
        var artists = e.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
            ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name")).Where(n => n.Length > 0))
            : "";
        var cover = "";
        if (e.TryGetProperty("visualIdentity", out var vi) && vi.TryGetProperty("image", out var images) && images.ValueKind == JsonValueKind.Array)
            cover = images.EnumerateArray().OrderByDescending(i => i.TryGetProperty("maxWidth", out var w) && w.TryGetInt32(out var px) ? px : 0)
                .Select(i => Str(i, "url")).FirstOrDefault(u => u.StartsWith("https://", StringComparison.Ordinal)) ?? "";
        var ms = e.TryGetProperty("duration", out var d) && d.TryGetInt64(out var v) ? v : 0;
        return new("Spotify", Str(e, "name"), artists, "", cover, (int)(ms / 1000), "");
    }

    private static async Task<SongInfo?> SoundCloudAsync(Uri uri, CancellationToken ct)
    {
        var clean = $"https://soundcloud.com{uri.AbsolutePath}";
        using var o = await JsonAsync($"https://soundcloud.com/oembed?format=json&url={Uri.EscapeDataString(clean)}", ct);
        var author = Str(o.RootElement, "author_name");
        var title = Str(o.RootElement, "title");
        var by = $" by {author}";
        if (author.Length > 0 && title.EndsWith(by, StringComparison.Ordinal)) title = title[..^by.Length];

        // The length is only on the song's own page.
        var duration = 0;
        try
        {
            var m = FullDuration().Match(await TextAsync(clean, ct));
            if (m.Success) duration = (int)(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1000);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // the length stays empty; everything else is known
        }
        return new("SoundCloud", title.Trim(), author, "", Str(o.RootElement, "thumbnail_url"), duration, "");
    }

    private static async Task<SongInfo?> AppleAsync(Uri uri, CancellationToken ct)
    {
        // A song inside an album page carries ?i=<song>; a song page ends in /song/<name>/<id>.
        var id = HttpUtility.ParseQueryString(uri.Query)["i"];
        if (string.IsNullOrEmpty(id)) id = AppleSong().Match(uri.AbsolutePath).Groups[1].Value;
        if (!Digits().IsMatch(id ?? "")) return null;
        var country = AppleCountry().Match(uri.AbsolutePath) is { Success: true } c ? $"&country={c.Groups[1].Value}" : "";
        using var o = await JsonAsync($"https://itunes.apple.com/lookup?id={id}&entity=song{country}", ct);
        var song = o.RootElement.GetProperty("results").EnumerateArray().FirstOrDefault(r => Str(r, "kind") == "song");
        if (song.ValueKind != JsonValueKind.Object) return null;
        var art = Str(song, "artworkUrl100").Replace("100x100bb", "600x600bb", StringComparison.Ordinal);
        var ms = song.TryGetProperty("trackTimeMillis", out var t) && t.TryGetInt64(out var v) ? v : 0;
        return new("Apple Music", Str(song, "trackName"), Str(song, "artistName"), Str(song, "collectionName"), art, (int)(ms / 1000), "");
    }

    private static async Task<SongInfo?> DeezerAsync(Uri uri, CancellationToken ct)
    {
        var m = DeezerTrack().Match(uri.AbsolutePath);
        if (!m.Success) return null;
        using var o = await JsonAsync($"https://api.deezer.com/track/{m.Groups[1].Value}", ct);
        var r = o.RootElement;
        if (r.TryGetProperty("error", out _)) return null;
        var artist = r.TryGetProperty("artist", out var a) ? Str(a, "name") : "";
        var (album, cover) = r.TryGetProperty("album", out var al) ? (Str(al, "title"), Str(al, "cover_xl")) : ("", "");
        var seconds = r.TryGetProperty("duration", out var d) && d.TryGetInt32(out var s) ? s : 0;
        return new("Deezer", Str(r, "title"), artist, album, cover, seconds, "");
    }

    // ---------------- covers for songs playing in other programs ----------------

    private static readonly Dictionary<string, (string Cover, string Album)> CoverCache = new();

    /// <summary>
    /// Cover (and album) of a song that plays in a program which doesn't give a public cover address,
    /// from Apple's public music search. Only the artist and title are sent. Empty when nothing matches.
    /// </summary>
    public static async Task<(string Cover, string Album)> FindCoverAsync(string artist, string title, CancellationToken ct = default)
    {
        var key = $"{artist}\n{title}".ToLowerInvariant();
        lock (CoverCache) if (CoverCache.TryGetValue(key, out var cached)) return cached;
        var found = ("", "");
        try
        {
            var term = Uri.EscapeDataString($"{artist} {CleanTitle(title)}".Trim());
            using var o = await JsonAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=5", ct);
            var wanted = Simple(artist);
            foreach (var r in o.RootElement.GetProperty("results").EnumerateArray())
            {
                // The artist must match, so a common title never brings a stranger's cover.
                var a = Simple(Str(r, "artistName"));
                if (wanted.Length > 0 && !a.Contains(wanted) && !wanted.Contains(a)) continue;
                found = (Str(r, "artworkUrl100").Replace("100x100bb", "600x600bb", StringComparison.Ordinal), Str(r, "collectionName"));
                break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return ("", ""); // not cached: try again next time
        }
        lock (CoverCache)
        {
            if (CoverCache.Count > 300) CoverCache.Clear();
            CoverCache[key] = found;
        }
        return found;
    }

    private static string Simple(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>"Artist - Song (Official Video)" by "Artist - Topic" → ("Artist", "Song").</summary>
    public static (string Artist, string Song) SplitVideoTitle(string title, string channel)
    {
        var artist = TopicSuffix().Replace(channel, "").Trim();
        var song = CleanTitle(title);
        var dash = song.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) (artist, song) = (song[..dash].Trim(), song[(dash + 3)..].Trim());
        return (artist, song);
    }

    // ---------------- helpers ----------------

    /// <summary>"Song (Official Video) [4K]" → "Song": drops the video labels music videos carry.</summary>
    internal static string CleanTitle(string title) => Spaces().Replace(VideoLabels().Replace(title, ""), " ").Trim();

    private static bool IsHost(Uri uri, string host) =>
        uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);

    private static bool IsKnown(Uri uri) => KnownHosts.Any(h => IsHost(uri, h));

    /// <summary>Follows a short link hop by hop, only through the known services.</summary>
    private static async Task<Uri> ResolveAsync(Uri uri, CancellationToken ct)
    {
        for (var i = 0; i < MaxRedirects; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is < 300 or > 399 || response.Headers.Location is null) return uri;
            var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
            if (next.Scheme != Uri.UriSchemeHttps || !IsKnown(next)) throw new ApiException(400, L.T("presence.lookupUnknown"));
            uri = next;
        }
        return uri;
    }

    private static async Task<JsonDocument> JsonAsync(string url, CancellationToken ct) => JsonDocument.Parse(await TextAsync(url, ct));

    private static async Task<string> TextAsync(string url, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            limited.Write(buffer, 0, read);
            if (limited.Length > MaxBytes) throw new InvalidOperationException("Response too large");
        }
        return System.Text.Encoding.UTF8.GetString(limited.GetBuffer(), 0, (int)limited.Length);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()?.Trim() ?? "" : "";

    [GeneratedRegex(@"^[\w-]{6,20}$")] private static partial Regex VideoId();
    [GeneratedRegex(@"^/(?:shorts|live|embed)/([\w-]{6,20})")] private static partial Regex ShortsId();
    [GeneratedRegex(@"""lengthSeconds"":""(\d{1,6})""")] private static partial Regex LengthSeconds();
    [GeneratedRegex(@"""full_duration"":(\d{1,10})")] private static partial Regex FullDuration();
    [GeneratedRegex(@"\s*-\s*Topic$|VEVO$", RegexOptions.IgnoreCase)] private static partial Regex TopicSuffix();
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|remaster(ed)?|visuali[sz]er|hd|4k|mv|clip)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)] private static partial Regex VideoLabels();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex Spaces();
    [GeneratedRegex(@"^/(?:intl-[a-z]{2}(?:-[a-z]{2})?/)?track/([A-Za-z0-9]{10,40})")] private static partial Regex SpotifyTrack();
    [GeneratedRegex(@"<script id=""__NEXT_DATA__"" type=""application/json"">([\s\S]*?)</script>")] private static partial Regex NextData();
    [GeneratedRegex(@"/song/(?:[^/]+/)?(\d+)")] private static partial Regex AppleSong();
    [GeneratedRegex(@"^/([a-z]{2})/")] private static partial Regex AppleCountry();
    [GeneratedRegex(@"^\d{1,15}$")] private static partial Regex Digits();
    [GeneratedRegex(@"^/(?:[a-z]{2}/)?track/(\d{1,15})")] private static partial Regex DeezerTrack();
}
