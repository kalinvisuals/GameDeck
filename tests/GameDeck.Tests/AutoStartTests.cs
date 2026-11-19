using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using GameDeck.App;
using GameDeck.App.Web;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Win32;
using Xunit;

namespace GameDeck.Tests;

/// <summary>Runs against a throwaway registry key, never the real Windows Run key.</summary>
[Collection("Localization")]
public class AutoStartTests : IAsyncLifetime
{
    private const string Exe = @"C:\Test Folder\GameDeck.exe";
    private readonly string _keyPath = $@"Software\GameDeckTests-{Guid.NewGuid():N}";
    private AudioEngine _engine = null!;
    private GameDeckService _service = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    private string? StoredValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue("GameDeck") as string;
    }

    public async Task InitializeAsync()
    {
        var store = new ConfigStore(TestAudio.TempDir());
        store.Update(c => { c.Server.Port = FreePort(); c.Audio.MonitorEnabled = false; });
        _engine = await Task.Run(() => new AudioEngine());
        _service = new GameDeckService(store, _engine, new PairingService()) { AutoStart = new WindowsAutoStart(_keyPath, Exe) };
        await Task.Run(_service.Start);
        _app = DashboardServer.Build(_service, IPAddress.Loopback);
        await _app.StartAsync();
        _http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{_service.Port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await Task.Run(_engine.Dispose);
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
    }

    private async Task PairAsync()
    {
        var (code, _) = _service.Pairing.GetCurrentCode();
        (await _http.PostAsJsonAsync("/api/pair", new { code, deviceName = "Test" })).EnsureSuccessStatusCode();
    }

    private static async Task<bool> EnabledIn(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean();
    }

    [Fact]
    public async Task The_switch_on_the_pc_turns_it_on_and_off()
    {
        await PairAsync();
        Assert.False(await EnabledIn(await _http.GetAsync("/api/autostart")));

        Assert.True(await EnabledIn(await _http.PutAsJsonAsync("/api/autostart", new { enabled = true })));
        Assert.Equal($"\"{Exe}\"", StoredValue()); // quoted, as the path can contain spaces
        Assert.True(await EnabledIn(await _http.GetAsync("/api/autostart")));

        Assert.False(await EnabledIn(await _http.PutAsJsonAsync("/api/autostart", new { enabled = false })));
        Assert.Null(StoredValue());
    }

    [Fact]
    public async Task It_needs_pairing()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PutAsJsonAsync("/api/autostart", new { enabled = true })).StatusCode);
        Assert.Null(StoredValue());
    }

    [Fact]
    public void Another_device_can_neither_read_nor_change_it()
    {
        Assert.Equal(403, Assert.Throws<ApiException>(() => _service.SetAutoStart(true, local: false)).Status);
        Assert.Equal(403, Assert.Throws<ApiException>(() => _service.AutoStartEnabled(local: false)).Status);
        Assert.Null(StoredValue());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
