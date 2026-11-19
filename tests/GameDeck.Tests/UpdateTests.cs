using System.Net;
using System.Text;
using GameDeck.App.Updates;
using Xunit;

namespace GameDeck.Tests;

public class UpdateTests
{
    /// <summary>Answers like GitHub would, without any network.</summary>
    private sealed class FakeGitHub(Dictionary<string, (HttpStatusCode Status, string Body)> answers) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            var (status, body) = answers.TryGetValue(url, out var a) ? a : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private const string ReleaseUrl = "https://api.github.com/repos/kalinvisuals/GameDeck/releases/latest";
    private const string NewsUrl = "https://raw.githubusercontent.com/kalinvisuals/GameDeck/main/news.json";

    private static string Release(string tag, string assetName = "GameDeck-Setup-{0}.exe", string host = "https://github.com/kalinvisuals/GameDeck") => $$"""
        {
          "tag_name": "{{tag}}", "body": "Notes for {{tag}}", "html_url": "https://github.com/kalinvisuals/GameDeck/releases/tag/{{tag}}",
          "published_at": "2026-11-20T10:00:00Z",
          "assets": [{
            "name": "{{string.Format(assetName, tag.TrimStart('v'))}}",
            "browser_download_url": "{{host}}/releases/download/{{tag}}/{{string.Format(assetName, tag.TrimStart('v'))}}",
            "digest": "sha256:{{new string('a', 64)}}"
          }]
        }
        """;

    private static async Task<UpdatesDto> CheckAsync(string current, string? release, string? news, string language = "ro")
    {
        var answers = new Dictionary<string, (HttpStatusCode, string)>();
        if (release is not null) answers[ReleaseUrl] = (HttpStatusCode.OK, release);
        if (news is not null) answers[NewsUrl] = (HttpStatusCode.OK, news);
        using var updates = new UpdateService(() => language, current, new FakeGitHub(answers));
        await updates.CheckAsync();
        return updates.Snapshot();
    }

    [Fact]
    public async Task A_newer_release_is_offered_with_its_notes()
    {
        var state = await CheckAsync("0.1.0", Release("v0.2.0"), null);
        Assert.Equal("0.2.0", state.Available?.Version);
        Assert.Equal("Notes for v0.2.0", state.Available?.Notes);
        Assert.False(state.CanInstall); // a test run is not an installed copy
    }

    [Theory]
    [InlineData("0.2.0")] // the same version
    [InlineData("0.3.0")] // already newer
    public async Task The_same_or_an_older_release_is_not_offered(string current) =>
        Assert.Null((await CheckAsync(current, Release("v0.2.0"), null)).Available);

    [Fact]
    public async Task A_release_without_the_installer_or_from_elsewhere_is_ignored()
    {
        Assert.Null((await CheckAsync("0.1.0", Release("v0.2.0", "Other-{0}.zip"), null)).Available);
        Assert.Null((await CheckAsync("0.1.0", Release("v0.2.0", host: "https://evil.example/kalinvisuals/GameDeck"), null)).Available);
    }

    [Fact]
    public async Task Without_a_release_or_internet_nothing_breaks()
    {
        var state = await CheckAsync("0.1.0", null, null);
        Assert.Null(state.Available);
        Assert.Empty(state.News);
        Assert.Equal("idle", state.State);
    }

    [Fact]
    public async Task Announcements_follow_the_language_and_their_end_date()
    {
        const string news = """
            {
              "donate": "https://revolut.me/example",
              "messages": [
                { "id": "hello", "link": "https://github.com/kalinvisuals/GameDeck", "ro": { "title": "Salut", "body": "Text", "linkText": "Vezi" }, "en": { "title": "Hello", "body": "Text" } },
                { "id": "only-en", "en": { "title": "English only", "body": "" } },
                { "id": "old", "until": "2020-01-01", "ro": { "title": "Expirat", "body": "" } },
                { "id": "unsafe", "link": "javascript:alert(1)", "ro": { "title": "Fără link", "body": "" } }
              ]
            }
            """;
        var ro = await CheckAsync("0.1.0", null, news, "ro");
        Assert.Equal(["Salut", "English only", "Fără link"], ro.News.Select(n => n.Title));
        Assert.Equal("Vezi", ro.News[0].LinkText);
        Assert.Null(ro.News[2].Link);
        Assert.Equal("https://revolut.me/example", ro.DonateUrl);

        var en = await CheckAsync("0.1.0", null, news, "en");
        Assert.Equal("Hello", en.News[0].Title);
    }

    [Fact]
    public async Task Only_https_donation_links_are_shown() =>
        Assert.Null((await CheckAsync("0.1.0", null, """{ "donate": "http://revolut.me/example" }""")).DonateUrl);

    [Theory]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("1.0", "0.9.9", true)]
    [InlineData("0.2.0-beta", "0.1.0", false)]
    public void Versions_compare_as_numbers(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(candidate, current));
}
