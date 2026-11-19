using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GameDeck.App;
using GameDeck.App.Web;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Security;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace GameDeck.Tests;

public class YouTubeCommandTests
{
    [Theory]
    [InlineData("pauză", "pause")]
    [InlineData("Continuă", "play")]
    [InlineData("următoarea", "next")]
    [InlineData("precedenta", "previous")]
    public void Control_words_are_commands(string text, string control) =>
        Assert.Equal(control, YouTubeCommand.Parse(text).Control);

    [Theory]
    [InlineData("Weiter", "play")]
    [InlineData("nächstes", "next")]
    [InlineData("précédent", "previous")]
    [InlineData("Siguiente", "next")]
    [InlineData("wznów", "play")]
    [InlineData("следующий", "next")]
    [InlineData("предыдущая", "previous")]
    [InlineData("пауза", "pause")]
    public void Command_words_work_in_every_interface_language(string text, string control) =>
        Assert.Equal(control, YouTubeCommand.Parse(text).Control);

    [Theory]
    [InlineData("lautstärke 30")]
    [InlineData("głośność 30")]
    [InlineData("громкость 30")]
    [InlineData("volumen 30")]
    public void Volume_works_in_every_interface_language(string text) =>
        Assert.Equal(30, YouTubeCommand.Parse(text).Value);

    [Fact]
    public void Volume_is_limited_to_100()
    {
        var command = YouTubeCommand.Parse("volum 140");
        Assert.Equal("volume", command.Control);
        Assert.Equal(100, command.Value);
    }

    [Fact]
    public void Anything_else_is_a_search_and_every_line_is_one_song()
    {
        Assert.Equal(["Daft Punk One More Time"], YouTubeCommand.Parse("  Daft Punk One More Time ").Searches);
        Assert.Equal(["one", "next"], YouTubeCommand.Parse("one\r\n\r\nnext\n").Searches); // "next" in a list is a song
    }
}

[Collection("Localization")]
public class YouTubeServerTests : IAsyncLifetime
{
    private AudioEngine _engine = null!;
    private GameDeckService _service = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var store = new ConfigStore(TestAudio.TempDir());
        store.Update(c => { c.Server.Port = _port; c.Audio.MonitorEnabled = false; });
        _engine = await Task.Run(() => new AudioEngine());
        _service = new GameDeckService(store, _engine, new PairingService());
        await Task.Run(_service.Start);
        _app = DashboardServer.Build(_service, IPAddress.Loopback);
        await _app.StartAsync();
        _http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        var (code, _) = _service.Pairing.GetCurrentCode();
        (await _http.PostAsJsonAsync("/api/pair", new { code, deviceName = "Test" })).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await Task.Run(_engine.Dispose);
    }

    private async Task<ClientWebSocket> ConnectExtensionAsync(string origin = YouTubeBridge.ExtensionOrigin)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin);
        await socket.ConnectAsync(new Uri($"ws://localhost:{_port}/ext/youtube"), CancellationToken.None);
        var config = await ReceiveAsync(socket); // the panel follows GameDeck's language
        Assert.Equal("config", config.GetProperty("type").GetString());
        Assert.Equal("en", config.GetProperty("language").GetString());
        return socket;
    }

    private static Task SendAsync(ClientWebSocket socket, object message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone();
    }

    private Task<HttpResponseMessage> ActAsync(object action) => _http.PostAsJsonAsync("/api/youtube", action);

    [Fact]
    public async Task Other_origins_cannot_pose_as_the_extension()
    {
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectExtensionAsync("https://example.com"));
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectExtensionAsync("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    }

    [Fact]
    public async Task Commands_need_pairing_and_an_open_tab()
    {
        using var stranger = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync("/api/youtube", new { action = "command", text = "song" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(new { action = "command", text = "song" })).StatusCode);
    }

    [Fact]
    public async Task A_list_plays_the_first_song_and_the_next_when_it_ends()
    {
        using var extension = await ConnectExtensionAsync();
        (await ActAsync(new { action = "command", text = "first song\nsecond song" })).EnsureSuccessStatusCode();

        var search = await ReceiveAsync(extension);
        Assert.Equal("search", search.GetProperty("type").GetString());
        Assert.Equal("first song", search.GetProperty("query").GetString());
        Assert.True(search.GetProperty("autoplay").GetBoolean());

        await SendAsync(extension, new
        {
            type = "results", requestId = search.GetProperty("requestId").GetInt32(), query = "first song",
            items = new object[] { new { id = "abcDEF_1234", title = "First", channel = "Band", duration = "3:20" }, new { id = "../bad", title = "Rejected" } },
        });
        await SendAsync(extension, new { type = "ended" });

        var next = await ReceiveAsync(extension);
        Assert.Equal("second song", next.GetProperty("query").GetString());
    }

    [Fact]
    public async Task One_song_waits_for_a_choice_and_a_miss_starts_nothing()
    {
        using var extension = await ConnectExtensionAsync();
        (await ActAsync(new { action = "command", text = "first song\nsecond song\nthird song" })).EnsureSuccessStatusCode();
        await ReceiveAsync(extension);

        (await ActAsync(new { action = "command", text = "missing song" })).EnsureSuccessStatusCode();
        var search = await ReceiveAsync(extension);
        Assert.False(search.GetProperty("autoplay").GetBoolean());
        await SendAsync(extension, new { type = "results", requestId = search.GetProperty("requestId").GetInt32(), query = "missing song", items = Array.Empty<object>() });

        (await ActAsync(new { action = "command", text = "volum 30" })).EnsureSuccessStatusCode();
        Assert.Equal("control", (await ReceiveAsync(extension)).GetProperty("type").GetString()); // no queued search came first
    }

    [Fact]
    public async Task Lists_on_the_page_reach_the_dashboard()
    {
        var bridge = new YouTubeBridge(() => "ro");
        await bridge.HandleMessageAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "results", requestId = 0, query = "typed on youtube",
            items = new object[] { new { id = "abcDEF_1234", title = "Found", channel = "Band", duration = "3:20" } },
        }));
        await bridge.HandleMessageAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "upNext", kind = "playlist",
            items = new object[] { new { id = "nextVid_123", title = "Next", channel = "Band", duration = "4:00" } },
        }));

        var state = bridge.Snapshot();
        Assert.Equal("typed on youtube", state.Query);
        Assert.Equal("Found", Assert.Single(state.Results).Title);
        Assert.Equal("Next", Assert.Single(state.UpNext).Title);
        Assert.Equal("playlist", state.UpNextKind);
    }

    [Fact]
    public async Task Controls_reach_the_tab_and_unknown_actions_are_refused()
    {
        using var extension = await ConnectExtensionAsync();
        (await ActAsync(new { action = "command", text = "volum 30" })).EnsureSuccessStatusCode();
        var message = await ReceiveAsync(extension);
        Assert.Equal("control", message.GetProperty("type").GetString());
        Assert.Equal("volume", message.GetProperty("action").GetString());
        Assert.Equal(30, message.GetProperty("value").GetDouble());

        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(new { action = "open", id = "../../evil" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(new { action = "control", text = "delete" })).StatusCode);
    }
}
