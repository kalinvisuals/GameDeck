using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GameDeck.App;
using GameDeck.App.Web;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Scenes;
using GameDeck.Core.Security;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace GameDeck.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("ctrl+alt+1", "Ctrl+Alt+1")]
    [InlineData("Alt + Shift + k", "Alt+Shift+K")]
    [InlineData("Ctrl+F9", "Ctrl+F9")]
    [InlineData("alt+num5", "Alt+Num5")]
    [InlineData("Shift+Ctrl+F24", "Ctrl+Shift+F24")]
    [InlineData("Win+Alt+G", "Alt+Win+G")]
    public void Valid_shortcuts_are_normalized(string text, string expected) =>
        Assert.Equal(expected, Hotkey.Normalize(text));

    [Theory]
    [InlineData("Ctrl+C", "scene.hotkeyNeedsModifiers")]   // would swallow copy everywhere
    [InlineData("Alt+1", "scene.hotkeyNeedsModifiers")]
    [InlineData("Shift+F9", "scene.hotkeyNeedsModifiers")] // Shift alone is too easy to hit in games
    [InlineData("F9", "scene.hotkeyNeedsModifiers")]
    [InlineData("Ctrl+Alt", "scene.hotkeyInvalid")]
    [InlineData("Ctrl+Alt+1+2", "scene.hotkeyInvalid")]
    [InlineData("Ctrl+Ctrl+A", "scene.hotkeyInvalid")]
    [InlineData("Ctrl+Alt+Space", "scene.hotkeyInvalid")]
    [InlineData("", "scene.hotkeyInvalid")]
    public void Weak_or_invalid_shortcuts_are_refused(string text, string error)
    {
        Assert.False(Hotkey.TryParse(text, out _, out var e));
        Assert.Equal(error, e);
    }

    [Fact]
    public void Virtual_keys_match_Windows()
    {
        Hotkey.TryParse("Ctrl+Alt+A", out var a, out _);
        Hotkey.TryParse("Ctrl+Alt+7", out var d, out _);
        Hotkey.TryParse("Ctrl+F1", out var f, out _);
        Hotkey.TryParse("Ctrl+Num0", out var n, out _);
        Assert.Equal((0x41, 0x37, 0x70, 0x60), (a!.VirtualKey, d!.VirtualKey, f!.VirtualKey, n!.VirtualKey));
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, a.Modifiers); // MOD_CONTROL | MOD_ALT = 3
        Assert.Equal(3, (int)a.Modifiers);
    }
}

public class SceneAutoSwitchTests
{
    private static readonly (string, string)[] Rules = [("game", "game.exe"), ("chat", "discord.exe")];
    private static readonly DateTime T0 = new(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Switches_only_for_a_matching_application_that_is_not_already_active()
    {
        var auto = new SceneAutoSwitch();
        Assert.Null(auto.Decide("notepad.exe", Rules, null, T0).SceneId);
        Assert.Null(auto.Decide(null, Rules, null, T0).SceneId);
        Assert.Null(auto.Decide("game.exe", Rules, "game", T0).SceneId); // already active: no repeat
        Assert.Equal("game", auto.Decide("GAME.EXE", Rules, null, T0).SceneId);
    }

    [Fact]
    public void A_cooldown_prevents_rapid_switching_then_allows_it()
    {
        var auto = new SceneAutoSwitch();
        Assert.Equal("game", auto.Decide("game.exe", Rules, null, T0).SceneId);

        var tooSoon = auto.Decide("discord.exe", Rules, "game", T0.AddSeconds(2));
        Assert.Null(tooSoon.SceneId);
        Assert.Equal(SceneAutoSwitch.Cooldown - TimeSpan.FromSeconds(2), tooSoon.RetryAfter);

        Assert.Equal("chat", auto.Decide("discord.exe", Rules, "game", T0 + SceneAutoSwitch.Cooldown).SceneId);
    }
}

[Collection("Localization")] // scene names use the process-wide language
public class SceneServiceTests : IAsyncLifetime
{
    private string _dir = "";
    private AudioEngine _engine = null!;
    private GameDeckService _service = null!;

    public async Task InitializeAsync()
    {
        _dir = TestAudio.TempDir();
        var store = new ConfigStore(_dir);
        // No devices at all: these tests are about scenes, not audio output.
        store.Update(c => { c.Audio.MonitorEnabled = false; c.Audio.MicVolume = 1.2f; c.Audio.VirtualVolume = 0.7f; c.Audio.MonitorVolume = 0.5f; });
        _engine = await Task.Run(() => new AudioEngine());
        _service = new GameDeckService(store, _engine, new PairingService());
        await Task.Run(_service.Start);
    }

    public async Task DisposeAsync() => await Task.Run(_engine.Dispose);

    [Fact]
    public void Create_rename_duplicate_delete_and_persist()
    {
        var a = _service.CreateScene(new SceneCreate("Gaming", "gamepad"));
        Assert.Equal(("Gaming", "gamepad"), (a.Name, a.Icon));
        Assert.Equal((1.2f, 0.7f, 0.5f), (a.Channels!.MicVolume, a.Channels.VirtualVolume, a.Channels.MonitorVolume));
        Assert.True(a.CreatedUtc <= a.ModifiedUtc);

        _service.UpdateScene(a.Id, new ScenePatch(Name: "  Ranked  ", Hotkey: "ctrl+alt+1", AutoApp: "Game.EXE"));
        var copy = _service.DuplicateScene(a.Id);
        Assert.NotEqual(a.Id, copy.Id);
        Assert.Contains("Ranked", copy.Name);
        Assert.Null(copy.Hotkey);   // a shortcut belongs to one scene
        Assert.Null(copy.AutoApp);

        // A fresh store on the same folder sees everything (restart).
        var reloaded = new ConfigStore(_dir).Read(c => c.Scenes.ToList());
        Assert.Equal(2, reloaded.Count);
        var first = reloaded.Single(s => s.Id == a.Id);
        Assert.Equal(("Ranked", "Ctrl+Alt+1", "game.exe"), (first.Name, first.Hotkey, first.AutoApp));

        _service.ApplyScene(copy.Id);
        Assert.Equal(copy.Id, _service.ActiveSceneId);
        _service.DeleteScene(copy.Id);
        Assert.Null(_service.ActiveSceneId);
        Assert.Single(_service.BuildState().Scenes.Items);
        Assert.Equal(404, Assert.Throws<ApiException>(() => _service.DeleteScene(copy.Id)).Status);
    }

    [Fact]
    public void Shortcut_and_trigger_conflicts_are_refused_and_can_be_cleared()
    {
        var a = _service.CreateScene(new SceneCreate("A", null));
        var b = _service.CreateScene(new SceneCreate("B", null));
        _service.UpdateScene(a.Id, new ScenePatch(Hotkey: "Ctrl+Alt+1", AutoApp: "game.exe"));

        Assert.Equal(409, Assert.Throws<ApiException>(() => _service.UpdateScene(b.Id, new ScenePatch(Hotkey: "alt+ctrl+1"))).Status);
        Assert.Equal(409, Assert.Throws<ApiException>(() => _service.UpdateScene(b.Id, new ScenePatch(AutoApp: "GAME.exe"))).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => _service.UpdateScene(b.Id, new ScenePatch(Hotkey: "Ctrl+C"))).Status);

        _service.UpdateScene(a.Id, new ScenePatch(Hotkey: "", AutoApp: ""));
        var freed = _service.UpdateScene(b.Id, new ScenePatch(Hotkey: "Ctrl+Alt+1", AutoApp: "game.exe"));
        Assert.Equal(("Ctrl+Alt+1", "game.exe"), (freed.Hotkey, freed.AutoApp));
        Assert.Equal([(b.Id, "Ctrl+Alt+1")], _service.HotkeyBindings());
    }

    [Fact]
    public void Shortcuts_are_released_while_a_dashboard_records_one_and_come_back()
    {
        var a = _service.CreateScene(new SceneCreate("A", null));
        _service.UpdateScene(a.Id, new ScenePatch(Hotkey: "Ctrl+Alt+1"));

        _service.SuspendHotkeys(20);
        Assert.Empty(_service.HotkeyBindings());
        _service.SuspendHotkeys(0);
        Assert.Equal([(a.Id, "Ctrl+Alt+1")], _service.HotkeyBindings());
    }

    [Fact]
    public void Automatic_rules_exist_only_while_the_global_switch_is_on()
    {
        var a = _service.CreateScene(new SceneCreate("A", null));
        _service.UpdateScene(a.Id, new ScenePatch(AutoApp: "game.exe"));
        Assert.Empty(_service.AutoRules()); // off by default

        _service.SetSceneOptions(new SceneOptionsPatch(true));
        Assert.Equal([(a.Id, "game.exe")], _service.AutoRules());
        _service.SetSceneOptions(new SceneOptionsPatch(false));
        Assert.Empty(_service.AutoRules());
    }

    [Fact]
    public void Applying_sets_levels_and_mutes_but_never_devices_and_skips_missing_apps()
    {
        _service.UpdateAudio(new AudioPatch("", false, null, "", null, "", false, null, null));
        var scene = _service.CreateScene(new SceneCreate("Quiet", null));
        _service.UpdateScene(scene.Id, new ScenePatch(
            Channels: new SceneChannels { MicVolume = 0.4f, MicMuted = true, VirtualVolume = 1.1f, VirtualMuted = false, MonitorVolume = 0.2f, MonitorMuted = true },
            Apps: [new SceneApp { Key = "not-running-gamedeck-test.exe", Name = "Ghost", Volume = 0.3f }]));
        var devicesBefore = _service.Store.Read(c => (c.Audio.MonitorDeviceId, c.Audio.VirtualDeviceId, c.Audio.MicDeviceId, c.Audio.MicEnabled, c.Audio.MonitorEnabled));

        var result = _service.ApplyScene(scene.Id);

        Assert.Empty(result.Applied);
        Assert.Equal(["Ghost"], result.Skipped); // quietly skipped, no error
        var a = _service.Store.Read(c => c.Audio);
        Assert.Equal((0.4f, true, 1.1f, false, 0.2f, true), (a.MicVolume, a.MicMuted, a.VirtualVolume, a.VirtualMuted, a.MonitorVolume, a.MonitorMuted));
        Assert.Equal(devicesBefore, (a.MonitorDeviceId, a.VirtualDeviceId, a.MicDeviceId, a.MicEnabled, a.MonitorEnabled));
        Assert.Equal(scene.Id, _service.BuildState().Scenes.ActiveId);
    }

    [Fact]
    public void A_scene_without_channels_leaves_the_mixer_alone()
    {
        var scene = _service.CreateScene(new SceneCreate("Apps only", null));
        _service.UpdateScene(scene.Id, new ScenePatch(ClearChannels: true, Apps: []));
        _service.UpdateAudio(new AudioPatch(null, null, 0.33f, null, null, null, null, null, null));

        _service.ApplyScene(scene.Id);

        Assert.Equal(0.33f, _service.Store.Read(c => c.Audio.MonitorVolume));
    }

    [Fact]
    public void Capturing_keeps_saved_apps_that_are_not_running()
    {
        var scene = _service.CreateScene(new SceneCreate("S", null));
        _service.UpdateScene(scene.Id, new ScenePatch(Apps: [new SceneApp { Key = "closed-for-now.exe", Name = "Closed", Volume = 0.25f }]));
        _service.UpdateAudio(new AudioPatch(null, null, 0.9f, null, null, null, null, null, null));

        var captured = _service.CaptureScene(scene.Id);

        Assert.Equal(0.9f, captured.Channels!.MonitorVolume);
        Assert.Equal(0.25f, captured.Apps.Single(x => x.Key == "closed-for-now.exe").Volume);
    }
}

/// <summary>
/// Real Windows audio sessions: a child PowerShell plays a silent WAV (inaudible), which gives it a
/// session in the volume mixer. Its volume is restored to 100 % before the process exits, because
/// Windows remembers per-application volumes.
/// </summary>
[Collection("Localization")]
public class AppSessionTests : IAsyncLifetime
{
    private const string Key = "powershell.exe";
    private string _wav = "";
    private AudioEngine _engine = null!;
    private GameDeckService _service = null!;

    public async Task InitializeAsync()
    {
        _wav = Path.Combine(TestAudio.TempDir(), "silence.wav");
        await File.WriteAllBytesAsync(_wav, TestAudio.Wav(30));
        var store = new ConfigStore(TestAudio.TempDir());
        store.Update(c => c.Audio.MonitorEnabled = false);
        _engine = await Task.Run(() => new AudioEngine());
        _service = new GameDeckService(store, _engine, new PairingService());
    }

    public async Task DisposeAsync() => await Task.Run(_engine.Dispose);

    [Fact]
    public async Task Volume_and_mute_reach_the_app_survive_its_restart_and_leave_other_apps_alone()
    {
        var others = await Task.Run(() => AppSessions.List().Where(a => a.Key != Key).ToDictionary(a => a.Key, a => (a.Volume, a.Muted)));
        var player = await StartPlayerAsync();
        try
        {
            await Task.Run(() => AppSessions.Apply([new AppAudioTarget(Key, 0.37f, true)]));
            var seen = await Task.Run(() => AppSessions.List().Single(a => a.Key == Key));
            Assert.Equal(0.37f, seen.Volume, 2);
            Assert.True(seen.Muted);

            // Restart the application: a new process id, the same scene still applies.
            Kill(player);
            player = await StartPlayerAsync();
            var scene = _service.CreateScene(new SceneCreate("Test", null));
            _service.UpdateScene(scene.Id, new ScenePatch(ClearChannels: true,
                Apps: [new SceneApp { Key = Key, Name = "PowerShell", Volume = 0.55f, Muted = false }]));

            var result = await Task.Run(() => _service.ApplyScene(scene.Id));

            Assert.Equal(["PowerShell"], result.Applied);
            seen = await Task.Run(() => AppSessions.List().Single(a => a.Key == Key));
            Assert.Equal(0.55f, seen.Volume, 2);
            Assert.False(seen.Muted);
            var after = await Task.Run(() => AppSessions.List().Where(a => others.ContainsKey(a.Key)).ToDictionary(a => a.Key, a => (a.Volume, a.Muted)));
            foreach (var (key, before) in others.Where(o => after.ContainsKey(o.Key)))
                Assert.Equal(before, after[key]);
            Assert.DoesNotContain(_service.RefreshApps(), a => a.Key == AppSessions.KeyFromProcessName(Process.GetCurrentProcess().ProcessName));
        }
        finally
        {
            await Task.Run(() => AppSessions.Apply([new AppAudioTarget(Key, 1f, false)]));
            Kill(player);
        }
    }

    private async Task<Process> StartPlayerAsync()
    {
        var p = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"(New-Object System.Media.SoundPlayer '{_wav}').PlaySync()\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        for (var i = 0; i < 50; i++)
        {
            if (await Task.Run(() => AppSessions.List().Any(a => a.Key == Key))) return p;
            await Task.Delay(100);
        }
        Kill(p);
        throw new TimeoutException("The test player never opened an audio session");
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(); p.WaitForExit(3000); } catch { }
        p.Dispose();
    }
}

/// <summary>Remote activation goes through the normal pairing and pushes the new active scene to every socket.</summary>
[Collection("Localization")]
public class SceneServerTests : IAsyncLifetime
{
    private AudioEngine _engine = null!;
    private GameDeckService _service = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private readonly CookieContainer _cookies = new();
    private string _base = "";

    public async Task InitializeAsync()
    {
        var store = new ConfigStore(TestAudio.TempDir());
        store.Update(c => { c.Server.Port = FreePort(); c.Audio.MonitorEnabled = false; });
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
    public async Task Scenes_need_pairing_and_remote_activation_is_pushed_to_every_client()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsJsonAsync("/api/scenes", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsync("/api/scenes/abc/apply", null)).StatusCode);

        var code = _service.Pairing.GetCurrentCode().Code;
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/pair", new { code, deviceName = "phone" })).StatusCode);

        using var ws = new ClientWebSocket();
        ws.Options.Cookies = _cookies;
        ws.Options.SetRequestHeader("Origin", _base);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_service.Port}/ws"), CancellationToken.None);
        await NextStateAsync(ws, _ => true);

        var created = await (await _http.PostAsJsonAsync("/api/scenes", new { name = "Night", icon = "headphones" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        var apply = await _http.PostAsync($"/api/scenes/{id}/apply", null);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);

        var pushed = await NextStateAsync(ws, s => s.GetProperty("scenes").GetProperty("activeId").GetString() == id);
        Assert.Equal("Night", pushed.GetProperty("scenes").GetProperty("items")[0].GetProperty("name").GetString());

        var bad = await _http.PatchAsJsonAsync($"/api/scenes/{id}", new { hotkey = "Ctrl+V" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private static async Task<JsonElement> NextStateAsync(ClientWebSocket ws, Func<JsonElement, bool> match)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[1 << 20];
        while (true)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            } while (!r.EndOfMessage);
            using var doc = JsonDocument.Parse(sb.ToString());
            if (doc.RootElement.GetProperty("type").GetString() != "state") continue;
            var data = doc.RootElement.GetProperty("data").Clone();
            if (match(data)) return data;
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
