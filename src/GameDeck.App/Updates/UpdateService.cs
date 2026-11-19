using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using GameDeck.App.Web;
using GameDeck.Core.Diagnostics;
using GameDeck.Core.Localization;
using Microsoft.Win32;

namespace GameDeck.App.Updates;

public sealed record UpdateRelease(string Version, string Notes, string Page, DateTimeOffset? Published);
public sealed record NewsMessage(string Id, string Title, string Body, string? Link, string? LinkText);
public sealed record UpdatesDto(
    string Current, UpdateRelease? Available, bool CanInstall, string State, string? Error,
    IReadOnlyList<NewsMessage> News, string? DonateUrl, string Source, DateTimeOffset? CheckedAt);

/// <summary>
/// Looks for a newer GameDeck on GitHub and reads the announcements file from the repository. It only
/// reads public data and sends nothing about the user. Installing needs a click on the PC, and the
/// downloaded installer must match the checksum GitHub publishes for it.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "kalinvisuals/GameDeck";
    public const string SourcePage = "https://github.com/" + Repository;
    /// <summary>The installer's AppId, which is also its uninstall key name.</summary>
    private const string InstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6E0B7C1A-3F55-4C1E-9B8D-2A7F4E9C5D31}_is1";
    private const long MaxInstallerBytes = 400L * 1024 * 1024;
    private const int MaxNews = 10;
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan MinManualGap = TimeSpan.FromSeconds(30);

    /// <summary>Development only: GAMEDECK_UPDATE_FEED points the checks at a local copy of these two files.</summary>
    private static readonly string? TestFeed = Environment.GetEnvironmentVariable("GAMEDECK_UPDATE_FEED")?.TrimEnd('/');
    private static readonly string ReleaseUrl = TestFeed is null ? $"https://api.github.com/repos/{Repository}/releases/latest" : TestFeed + "/release.json";
    private static readonly string NewsUrl = TestFeed is null ? $"https://raw.githubusercontent.com/{Repository}/main/news.json" : TestFeed + "/news.json";

    private readonly HttpClient _http;
    private readonly Func<string> _language;
    private readonly string _current;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private Release? _release;
    private NewsFile? _news;
    private string _state = "idle";
    private string? _error;
    private DateTimeOffset? _checkedAt;

    public event Action? Changed;

    public UpdateService(Func<string> language, string currentVersion, HttpMessageHandler? handler = null)
    {
        _language = language;
        _current = currentVersion;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GameDeck-Updater", currentVersion));
    }

    /// <summary>Checks shortly after start and then a few times a day, never in a tight loop.</summary>
    public void Start() => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(FirstCheck, _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                await CheckAsync();
                await Task.Delay(CheckEvery, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // closing
        }
    });

    public UpdatesDto Snapshot()
    {
        lock (_gate)
        {
            var language = _language();
            var available = _release is { } r && IsNewer(r.Version, _current) ? new UpdateRelease(r.Version, r.Notes, r.Page, r.Published) : null;
            var news = _news?.Messages.Select(m => m.For(language)).OfType<NewsMessage>().ToList() ?? [];
            return new(_current, available, available is not null && CanInstall(), _state, _error, news, _news?.DonateUrl, SourcePage, _checkedAt);
        }
    }

    public async Task CheckAsync(bool manual = false)
    {
        if (manual && _checkedAt is { } last && DateTimeOffset.UtcNow - last < MinManualGap) return;
        if (!await _checking.WaitAsync(0)) return;
        try
        {
            SetState("checking", null);
            var release = await FetchReleaseAsync();
            var news = await FetchNewsAsync();
            lock (_gate)
            {
                if (release is not null) _release = release;
                if (news is not null) _news = news;
                _checkedAt = DateTimeOffset.UtcNow;
            }
            SetState("idle", null);
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>Downloads the new installer, checks it and starts it. Windows then asks for permission.</summary>
    public async Task InstallAsync()
    {
        Release? release;
        lock (_gate) release = _release;
        if (release is null || !IsNewer(release.Version, _current)) throw new ApiException(409, L.T("updates.nothing"));
        if (!CanInstall()) throw new ApiException(409, L.T("updates.notInstalled"));
        if (_state is "downloading" or "installing") return;

        SetState("downloading", null);
        string file;
        try
        {
            file = await DownloadAsync(release);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Log.Warn("Update download failed", ex);
            SetState("failed", ex is InvalidDataException ? L.T("updates.badFile") : L.T("updates.downloadFailed"));
            return;
        }

        try
        {
            SetState("installing", null);
            // The installer closes GameDeck, replaces it and starts the new version.
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetState("idle", L.T("updates.cancelled")); // "No" on the Windows prompt
        }
        catch (Win32Exception ex)
        {
            Log.Warn("Could not start the installer", ex);
            SetState("failed", L.T("updates.startFailed"));
        }
    }

    private async Task<string> DownloadAsync(Release release)
    {
        var folder = Path.Combine(Path.GetTempPath(), "GameDeck-Update");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, release.InstallerName);
        var expected = release.Sha256 ?? await FetchChecksumAsync(release) ?? throw new InvalidDataException("no checksum published");

        using (var response = await _http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, _stop.Token))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxInstallerBytes) throw new InvalidDataException("installer too large");
            await using var input = await response.Content.ReadAsStreamAsync(_stop.Token);
            await using var output = File.Create(file);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, _stop.Token)) > 0)
            {
                total += read;
                if (total > MaxInstallerBytes) throw new InvalidDataException("installer too large");
                await output.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }
        }

        await using (var check = File.OpenRead(file))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, _stop.Token));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                check.Close();
                File.Delete(file);
                throw new InvalidDataException("checksum mismatch");
            }
        }
        return file;
    }

    private async Task<string?> FetchChecksumAsync(Release release)
    {
        if (release.ChecksumUrl is null) return null;
        var text = await _http.GetStringAsync(release.ChecksumUrl, _stop.Token);
        var hash = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return IsSha256(hash) ? hash : null;
    }

    private async Task<Release?> FetchReleaseAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, _stop.Token);
            if (!response.IsSuccessStatusCode) return null; // no release yet, or GitHub's hourly limit
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(_stop.Token));
            return Release.Read(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            Log.Info($"Update check skipped: {ex.GetType().Name}");
            return null;
        }
    }

    private async Task<NewsFile?> FetchNewsAsync()
    {
        try
        {
            using var response = await _http.GetAsync(NewsUrl, _stop.Token);
            if (!response.IsSuccessStatusCode) return response.StatusCode == System.Net.HttpStatusCode.NotFound ? new NewsFile([], null) : null;
            if (response.Content.Headers.ContentLength > 64 * 1024) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(_stop.Token));
            return NewsFile.Read(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            Log.Info($"News check skipped: {ex.GetType().Name}");
            return null;
        }
    }

    private void SetState(string state, string? error)
    {
        lock (_gate)
        {
            _state = state;
            _error = error;
        }
        Changed?.Invoke();
    }

    /// <summary>Only a copy installed by the setup replaces itself; a development build never does.</summary>
    private static bool CanInstall()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(InstallKey);
            var location = key?.GetValue("InstallLocation") as string;
            return location is not null && string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current, out var b) && a > b;

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static string? SafeLink(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && value!.Length <= 300 ? uri.AbsoluteUri : null;

    private static string Text(JsonElement e, string name, int max)
    {
        var value = e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        return value.Length > max ? value[..max] : value;
    }

    internal sealed record Release(string Version, string Notes, string Page, DateTimeOffset? Published, string InstallerName, string InstallerUrl, string? Sha256, string? ChecksumUrl)
    {
        public static Release? Read(JsonElement root)
        {
            var tag = Text(root, "tag_name", 40).TrimStart('v', 'V');
            if (!System.Version.TryParse(tag, out _) || root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

            JsonElement? installer = null;
            string? checksumUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name", 120);
                if (name.Equals($"GameDeck-Setup-{tag}.exe", StringComparison.OrdinalIgnoreCase)) installer = asset;
                else if (name.Equals($"GameDeck-Setup-{tag}.exe.sha256", StringComparison.OrdinalIgnoreCase)) checksumUrl = SafeLink(Text(asset, "browser_download_url", 300));
            }
            if (installer is not { } file || SafeLink(Text(file, "browser_download_url", 300)) is not { } url) return null;
            if (!url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;

            var digest = Text(file, "digest", 80);
            var sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsSha256(digest[7..]) ? digest[7..] : null;
            DateTimeOffset? published = DateTimeOffset.TryParse(Text(root, "published_at", 40), out var p) ? p : null;
            return new(tag, Text(root, "body", 4000), SafeLink(Text(root, "html_url", 300)) ?? SourcePage + "/releases", published, $"GameDeck-Setup-{tag}.exe", url, sha, checksumUrl);
        }
    }

    internal sealed record NewsItem(string Id, Dictionary<string, (string Title, string Body, string? LinkText)> Texts, string? Link, DateTimeOffset? Until)
    {
        public NewsMessage? For(string language)
        {
            if (Until is { } until && until < DateTimeOffset.UtcNow) return null;
            if (!Texts.TryGetValue(language, out var text) && !Texts.TryGetValue("en", out text)) text = Texts.Values.FirstOrDefault();
            if (text.Title is null) return null;
            return new(Id, text.Title, text.Body, Link, Link is null ? null : text.LinkText);
        }
    }

    /// <summary>
    /// news.json: { "donate": "https://revolut.me/...", "messages": [ { "id": "...", "until": "2027-01-01",
    /// "link": "https://...", "ro": { "title", "body", "linkText" }, "en": { ... } } ] }
    /// </summary>
    internal sealed record NewsFile(IReadOnlyList<NewsItem> Messages, string? DonateUrl)
    {
        public static NewsFile Read(JsonElement root)
        {
            var messages = new List<NewsItem>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in list.EnumerateArray().Take(MaxNews))
                {
                    var id = Text(m, "id", 60);
                    if (id.Length == 0) continue;
                    var texts = new Dictionary<string, (string, string, string?)>();
                    foreach (var language in GameDeck.Core.Localization.L.Supported)
                    {
                        if (!m.TryGetProperty(language, out var t) || t.ValueKind != JsonValueKind.Object) continue;
                        var title = Text(t, "title", 120);
                        if (title.Length > 0) texts[language] = (title, Text(t, "body", 600), Text(t, "linkText", 40) is { Length: > 0 } lt ? lt : null);
                    }
                    if (texts.Count == 0) continue;
                    DateTimeOffset? until = DateTimeOffset.TryParse(Text(m, "until", 40), out var u) ? u : null;
                    messages.Add(new(id, texts, SafeLink(Text(m, "link", 300)), until));
                }
            }
            return new(messages, SafeLink(Text(root, "donate", 300)));
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }
}
