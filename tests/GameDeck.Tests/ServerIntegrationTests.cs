using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
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

/// <summary>
/// Runs the real server, real WASAPI engine and the default Windows output device, on loopback only.
/// Monitor volume is 0 and the test sound is digital silence, so nothing is audible.
/// </summary>
public sealed class ServerIntegrationTests : IAsyncLifetime
{
    private AudioEngine _engine = null!;
    private GameDeckService _service = null!;
    private WebApplication _app = null!;
    private string _base = "";
    private readonly CookieContainer _cookies = new();
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var store = new ConfigStore(TestAudio.TempDir());
        store.Update(c =>
        {
            c.Server.Port = FreePort();
            c.Audio.MonitorEnabled = true;
            c.Audio.MonitorDeviceId = null;
            c.Audio.MonitorVolume = 0f;
        });
        _engine = await Task.Run(() => new AudioEngine());
        _service = new GameDeckService(store, _engine, new PairingService());
        await Task.Run(_service.Start);
        _app = DashboardServer.Build(_service, IPAddress.Loopback);
        await _app.StartAsync();
        _base = $"http://127.0.0.1:{_service.Port}";
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies }) { BaseAddress = new Uri(_base) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await Task.Run(_engine.Dispose);
    }

    [Fact]
    public async Task Full_flow_pair_import_play_over_websocket_and_logout()
    {
        // Unpaired clients get nothing but the SPA shell.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/")).StatusCode);

        // Wrong code, then right code, then the right code again (single use).
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsJsonAsync("/api/pair", new { code = "000000x", deviceName = "t" })).StatusCode);
        var code = _service.Pairing.GetCurrentCode().Code;
        var paired = await _http.PostAsJsonAsync("/api/pair", new { code, deviceName = "Test phone" });
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var cookie = paired.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        using (var reuse = new HttpClient { BaseAddress = new Uri(_base) })
            Assert.Equal(HttpStatusCode.Unauthorized, (await reuse.PostAsJsonAsync("/api/pair", new { code, deviceName = "x" })).StatusCode);

        // Import a short and a long sound.
        var shortId = await Upload("blip.wav", TestAudio.Wav(0.3));
        var longId = await Upload("long.wav", TestAudio.Wav(3.0, 48000, 2));

        var state = await GetState();
        Assert.True(state.GetProperty("status").GetProperty("monitor").GetProperty("ok").GetBoolean(),
            "Default output device should open: " + state.GetProperty("status").GetProperty("monitor"));
        Assert.Equal(2, state.GetProperty("profiles")[0].GetProperty("layout").GetArrayLength());

        // Real-time: play over the socket, see it playing, see it finish.
        using var ws = new ClientWebSocket();
        ws.Options.Cookies = _cookies;
        ws.Options.SetRequestHeader("Origin", _base);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_service.Port}/ws"), Timeout(5));
        Assert.Equal("state", (await Receive(ws, m => true)).GetProperty("type").GetString());

        await Send(ws, new { type = "play", soundId = shortId });
        await Receive(ws, m => IsPlaying(m, shortId));
        await Receive(ws, m => m.GetProperty("type").GetString() == "playing" && m.GetProperty("data").GetArrayLength() == 0);

        // Default is Restart: a second press keeps exactly one copy playing, from the start.
        Assert.Equal("Restart", state.GetProperty("sounds")[1].GetProperty("mode").GetString());
        await Send(ws, new { type = "play", soundId = longId });
        await Receive(ws, m => IsPlaying(m, longId));
        await Task.Delay(500);
        await Send(ws, new { type = "play", soundId = longId });
        var restarted = await Receive(ws, m => IsPlaying(m, longId)
            && m.GetProperty("data")[0].GetProperty("elapsedMs").GetDouble() < 200);
        Assert.Equal(1, restarted.GetProperty("data").GetArrayLength());
        await Send(ws, new { type = "stopAll" });
        await Receive(ws, m => m.GetProperty("type").GetString() == "playing" && m.GetProperty("data").GetArrayLength() == 0, seconds: 2);

        // Toggle (opt-in per button): second press stops a long sound.
        Assert.Equal(HttpStatusCode.OK, (await _http.PatchAsJsonAsync($"/api/sounds/{longId}", new { mode = "Toggle" })).StatusCode);
        await Send(ws, new { type = "play", soundId = longId });
        await Receive(ws, m => IsPlaying(m, longId));
        await Send(ws, new { type = "play", soundId = longId });
        await Receive(ws, m => m.GetProperty("type").GetString() == "playing" && m.GetProperty("data").GetArrayLength() == 0, seconds: 2);

        // Loop + Stop All.
        Assert.Equal(HttpStatusCode.OK, (await _http.PatchAsJsonAsync($"/api/sounds/{shortId}", new { loop = true })).StatusCode);
        await Receive(ws, m => m.GetProperty("type").GetString() == "state");
        await Send(ws, new { type = "play", soundId = shortId });
        await Receive(ws, m => IsPlaying(m, shortId));
        await Task.Delay(800); // longer than the clip: it must still be looping
        Assert.True(_engine.IsPlaying(shortId));
        await Send(ws, new { type = "stopAll" });
        await Receive(ws, m => m.GetProperty("type").GetString() == "playing" && m.GetProperty("data").GetArrayLength() == 0, seconds: 2);

        // Real metering: a quiet tone registers on the soundboard meter, while monitor volume 0
        // keeps the device output at exactly zero (still inaudible).
        var toneId = await Upload("tone.wav", TestAudio.Wav(1.0, amplitude: 0.05));
        await Send(ws, new { type = "play", soundId = toneId });
        var levels = await Receive(ws, m => m.GetProperty("type").GetString() == "levels"
                                            && m.GetProperty("data").GetProperty("sounds").GetSingle() > 0.01f);
        Assert.Equal(0f, levels.GetProperty("data").GetProperty("monitor").GetSingle());
        await Send(ws, new { type = "stopAll" });

        // Logout revokes the token.
        Assert.Equal(HttpStatusCode.NoContent, (await _http.PostAsync("/api/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Favorites_categories_and_mutes_round_trip_through_the_api()
    {
        var code = _service.Pairing.GetCurrentCode().Code;
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/pair", new { code, deviceName = "t" })).StatusCode);
        var id = await Upload("x.wav", TestAudio.Wav(0.2));

        Assert.Equal(HttpStatusCode.OK, (await _http.PatchAsJsonAsync($"/api/sounds/{id}", new { favorite = true, category = "  Memes  " })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.PutAsJsonAsync("/api/audio", new { micMuted = true, monitorMuted = true, monitorVolume = 0.6 })).StatusCode);

        var state = await GetState();
        var sound = state.GetProperty("sounds")[0];
        Assert.True(sound.GetProperty("favorite").GetBoolean());
        Assert.Equal("Memes", sound.GetProperty("category").GetString());
        var audio = state.GetProperty("audio");
        Assert.True(audio.GetProperty("micMuted").GetBoolean());
        Assert.True(audio.GetProperty("monitorMuted").GetBoolean());
        Assert.False(audio.GetProperty("virtualMuted").GetBoolean());
        Assert.Equal(0.6, audio.GetProperty("monitorVolume").GetDouble(), 3); // volume kept while muted
        Assert.True(state.TryGetProperty("diagnostics", out _));
    }

    [Fact]
    public async Task Trim_round_trips_and_the_editor_can_fetch_the_file()
    {
        var code = _service.Pairing.GetCurrentCode().Code;
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/pair", new { code, deviceName = "t" })).StatusCode);
        var bytes = TestAudio.Wav(1.0);
        var id = await Upload("x.wav", bytes);

        var audio = await _http.GetAsync($"/api/sounds/{id}/audio");
        Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
        Assert.Equal("audio/wav", audio.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes.Length, (await audio.Content.ReadAsByteArrayAsync()).Length);

        Assert.Equal(HttpStatusCode.OK, (await _http.PatchAsJsonAsync($"/api/sounds/{id}", new { trimStartMs = 200, trimEndMs = 600 })).StatusCode);
        var sound = (await GetState()).GetProperty("sounds")[0];
        Assert.Equal(200, sound.GetProperty("trimStartMs").GetInt64());
        Assert.Equal(600, sound.GetProperty("trimEndMs").GetInt64());

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PatchAsJsonAsync($"/api/sounds/{id}", new { trimStartMs = 300, trimEndMs = 310 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/sounds/nope/audio")).StatusCode);

        using var anonymous = new HttpClient { BaseAddress = new Uri(_base) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/sounds/{id}/audio")).StatusCode);
    }

    [Fact]
    public async Task Cross_origin_and_rebinding_requests_are_rejected()
    {
        var code = _service.Pairing.GetCurrentCode().Code;
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/pair", new { code, deviceName = "t" })).StatusCode);

        var csrf = new HttpRequestMessage(HttpMethod.Post, "/api/stop-all");
        csrf.Headers.Add("Origin", "http://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(csrf)).StatusCode);

        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        rebinding.Headers.Host = $"evil.example.com:{_service.Port}";
        Assert.Equal((HttpStatusCode)421, (await _http.SendAsync(rebinding)).StatusCode);

        var badUpload = new MultipartFormDataContent { { new ByteArrayContent(new byte[100]), "files", "notes.txt" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsync("/api/sounds", badUpload)).StatusCode);
    }

    private async Task<string> Upload(string name, byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var res = await _http.PostAsync("/api/sounds", new MultipartFormDataContent { { file, "files", name } });
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("created")[0].GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> GetState() =>
        JsonDocument.Parse(await _http.GetStringAsync("/api/state")).RootElement;

    private static bool IsPlaying(JsonElement m, string soundId) =>
        m.GetProperty("type").GetString() == "playing"
        && m.GetProperty("data").EnumerateArray().Any(p => p.GetProperty("soundId").GetString() == soundId);

    private static Task Send(ClientWebSocket ws, object message) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, Timeout(5));

    private static async Task<JsonElement> Receive(ClientWebSocket ws, Func<JsonElement, bool> match, int seconds = 5)
    {
        var ct = Timeout(seconds);
        var buffer = new byte[256 * 1024];
        while (true)
        {
            var total = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer.AsMemory(total), ct);
                total += r.Count;
            } while (!r.EndOfMessage);
            var msg = JsonDocument.Parse(buffer.AsMemory(0, total)).RootElement;
            if (msg.GetProperty("type").GetString() == "error") throw new Xunit.Sdk.XunitException("Server error: " + msg);
            if (match(msg)) return msg;
        }
    }

    private static CancellationToken Timeout(int seconds) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
