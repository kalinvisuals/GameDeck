using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GameDeck.App;
using GameDeck.App.Web;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Deck;
using GameDeck.Core.Hardware;
using GameDeck.Core.Security;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace GameDeck.Tests;

/// <summary>Records what the deck would have done on Windows; nothing is launched or typed.</summary>
internal sealed class FakePlatform : IDeckPlatform
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public Exception? Fail { get; set; }

    public void Launch(string path) => Record($"launch {path}");
    public void OpenUrl(string url) => Record($"url {url}");
    public void SendKeys(KeyCombo combo) => Record($"keys {combo}");
    public void Media(string key) => Record($"media {key}");

    /// <summary>Programs that have a window "open", for window actions.</summary>
    public HashSet<string> Open { get; } = ["obs64.exe"];
    /// <summary>Open programs whose windows are all minimized.</summary>
    public HashSet<string> Minimized { get; } = [];

    public int Window(string program, string mode)
    {
        Record($"window {program} {mode}");
        return Open.Contains(program) ? 1 : 0;
    }

    public IReadOnlyList<OpenWindow> OpenWindows() => Open.Select(p => new OpenWindow(p, p, Minimized.Contains(p))).ToList();

    private void Record(string call)
    {
        if (Fail is not null) throw Fail;
        Calls.Enqueue(call);
    }
}

[Collection("Localization")]
public class KeyComboTests
{
    [Theory]
    [InlineData("ctrl+shift+s", "Ctrl+Shift+S")]
    [InlineData("Alt+Tab", "Alt+Tab")]
    [InlineData("Ctrl + Alt + M", "Ctrl+Alt+M")]
    [InlineData("win+shift+s", "Shift+Win+S")]
    [InlineData("F5", "F5")]
    [InlineData("shift+f10", "Shift+F10")]
    [InlineData("escape", "Esc")]
    [InlineData("Ctrl+Enter", "Ctrl+Enter")]
    [InlineData("alt+left", "Alt+Left")]
    [InlineData("PrintScreen", "PrintScreen")]
    public void Valid_combinations_are_normalized(string text, string expected)
    {
        Assert.True(KeyCombo.TryParse(text, out var combo, out _));
        Assert.Equal(expected, combo!.ToString());
    }

    [Theory]
    [InlineData("A", "deck.keysWouldType")]          // a bare letter would type text
    [InlineData("Shift+A", "deck.keysWouldType")]
    [InlineData("Enter", "deck.keysWouldType")]      // and Enter would confirm it
    [InlineData("Space", "deck.keysWouldType")]
    [InlineData("7", "deck.keysWouldType")]
    [InlineData("Num5", "deck.keysWouldType")]
    [InlineData("Ctrl+Alt", "deck.keysInvalid")]
    [InlineData("Ctrl+A+B", "deck.keysInvalid")]
    [InlineData("Ctrl+;", "deck.keysInvalid")]
    [InlineData("", "deck.keysInvalid")]
    public void Combinations_that_could_type_text_or_make_no_sense_are_refused(string text, string error)
    {
        Assert.False(KeyCombo.TryParse(text, out _, out var e));
        Assert.Equal(error, e);
    }

    [Fact]
    public void Key_events_have_the_layout_Windows_expects()
    {
        Assert.Equal(40, Marshal.SizeOf<WindowsDeckPlatform.INPUT>()); // x64 INPUT
        KeyCombo.TryParse("Ctrl+Delete", out var c, out _);
        Assert.Equal((0x2E, true), (c!.VirtualKey, c.Extended));
        Assert.Equal(0xB3, DeckRules.MediaKeys["playPause"]);
    }
}

[Collection("Localization")]
public class DeckRulesTests
{
    private static readonly string Data = Path.Combine(Path.GetTempPath(), "gamedeck-tests", "data-dir");

    [Theory]
    [InlineData(@"C:\Games\Game.exe")]
    [InlineData(@"C:\Users\Me\Desktop\Game.lnk")]
    [InlineData(@"D:\Docs\notes.txt")]
    [InlineData(@"C:\Games")]
    [InlineData("\"C:\\Program Files\\App\\app.exe\"")]
    public void Local_programs_files_and_folders_are_accepted(string path) =>
        Assert.True(Path.IsPathFullyQualified(DeckRules.CleanLaunchPath(path, Data)));

    [Theory]
    [InlineData(@"game.exe")]                              // relative: would depend on the working directory
    [InlineData(@"..\..\Windows\System32\cmd.exe")]
    [InlineData(@"\\server\share\tool.exe")]               // network
    [InlineData(@"\\?\C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Scripts\run.bat")]
    [InlineData(@"C:\Scripts\run.ps1")]
    [InlineData(@"C:\Scripts\run.BAT.")]                   // Windows drops the trailing dot
    [InlineData(@"C:\Scripts\setup.msi")]
    [InlineData(@"C:\Scripts\run.vbs")]
    [InlineData(@"C:\Games\Game.exe:stream.bat")]          // alternate data stream
    [InlineData(@"C:\Games\Game.exe --flag")]              // no arguments: the path is the whole thing
    [InlineData("")]
    public void Unsafe_or_ambiguous_launch_targets_are_refused(string path)
    {
        if (path.EndsWith("--flag"))
        {
            // Accepted as a (non-existent) file name; it can never run with arguments. It fails at press time.
            Assert.EndsWith("--flag", DeckRules.CleanLaunchPath(path, Data));
            return;
        }
        Assert.Throws<DeckException>(() => DeckRules.CleanLaunchPath(path, Data));
    }

    [Fact]
    public void Files_inside_the_data_folder_cannot_be_launched_even_through_dot_dot()
    {
        Assert.Throws<DeckException>(() => DeckRules.CleanLaunchPath(Path.Combine(Data, "sounds", "x.exe"), Data));
        Assert.Throws<DeckException>(() => DeckRules.CleanLaunchPath(Path.Combine(Data, "..", "data-dir", "deck-images", "a.exe"), Data));
    }

    [Theory]
    [InlineData("https://example.com/page?x=1", true)]
    [InlineData("http://192.168.1.2:8080", true)]
    [InlineData("steam://rungameid/730", true)]
    [InlineData("example.com", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("https://", false)]
    public void Only_web_and_game_launcher_links_are_accepted(string url, bool ok)
    {
        if (ok) Assert.Equal(url, DeckRules.CleanUrl(url));
        else Assert.Throws<DeckException>(() => DeckRules.CleanUrl(url));
    }

    [Fact]
    public void An_action_keeps_only_the_fields_its_type_uses()
    {
        var a = DeckRules.CleanAction(new DeckAction { Type = "mute", Channel = "mic", Target = @"C:\x.exe", Keys = "Ctrl+A", Value = 3 }, false, Data);
        Assert.Equal(("mute", "mic", "toggle", null, null, null), (a.Type, a.Channel, a.Mode, a.Target, a.Keys, a.Value));

        var v = DeckRules.CleanAction(new DeckAction { Type = "volume", Channel = "monitor", Mode = "set", Value = 9 }, false, Data);
        Assert.Equal(1.5f, v.Value);
        Assert.Throws<DeckException>(() => DeckRules.CleanAction(new DeckAction { Type = "shell", Target = "cmd /c del" }, false, Data));
        Assert.Throws<DeckException>(() => DeckRules.CleanAction(new DeckAction { Type = "sensor", Sensor = "cpu.voltage" }, false, Data));
    }

    [Fact]
    public void Macro_steps_cannot_start_macros_and_waits_are_bounded()
    {
        Assert.Throws<DeckException>(() => DeckRules.CleanAction(new DeckAction { Type = "macro", Target = "aaaaaaaaaaaa" }, inMacro: true, Data));
        Assert.Throws<DeckException>(() => DeckRules.CleanAction(new DeckAction { Type = "wait", DelayMs = 100 }, inMacro: false, Data));
        Assert.Equal(DeckRules.MaxWaitMs, DeckRules.CleanAction(new DeckAction { Type = "wait", DelayMs = int.MaxValue }, true, Data).DelayMs);
        var tooLong = new DeckMacro { Steps = Enumerable.Repeat(new DeckAction { Type = "stopAll" }, DeckRules.MaxSteps + 1).ToList() };
        Assert.Throws<DeckException>(() => DeckRules.CleanMacro(tooLong, Data));
    }

    [Fact]
    public void Buttons_keep_free_slots_move_out_of_taken_ones_and_are_never_dropped()
    {
        var p = new DeckProfile
        {
            Rows = 2, Columns = 2,
            Pages = [new DeckPage { Buttons = [new() { Slot = 1, Label = "a" }, new() { Slot = 1, Label = "b" }, new() { Slot = 99, Label = "c" }] }],
        };
        var clean = DeckRules.CleanProfile(p, Data, _ => true);
        Assert.Equal([(0, "b"), (1, "a"), (2, "c")], clean.Pages[0].Buttons.Select(b => (b.Slot, b.Label)));
        Assert.Equal(3, clean.Pages[0].Buttons.Select(b => b.Id).Distinct().Count());

        p.Rows = 1; // two slots for three buttons
        Assert.Throws<DeckException>(() => DeckRules.CleanProfile(p, Data, _ => true));
    }

    [Fact]
    public void Every_accepted_format_is_recognised_by_content_with_its_size_and_animation()
    {
        Assert.Equal(new ImageInfo("png", 16, 16, false, 1), ImageCheck.Inspect(TestImages.Png()));
        Assert.Equal(new ImageInfo("jpg", 16, 16, false, 1), ImageCheck.Inspect(TestImages.Jpeg()));
        Assert.Equal(new ImageInfo("gif", 1, 1, true, 3), ImageCheck.Inspect(TestImages.AnimatedGif(frames: 3)));
        Assert.Equal(new ImageInfo("gif", 1, 1, false, 1), ImageCheck.Inspect(TestImages.AnimatedGif(frames: 1)));
        Assert.Equal(new ImageInfo("png", 4, 4, true, 2), ImageCheck.Inspect(TestImages.AnimatedPng()));
        Assert.Equal(new ImageInfo("webp", 1, 1, false, 1), ImageCheck.Inspect(TestImages.Webp()));
        Assert.Equal(new ImageInfo("webp", 1, 1, true, 2), ImageCheck.Inspect(TestImages.Webp(animated: true)));
    }

    [Fact]
    public void Truncated_padded_disguised_and_script_files_are_refused()
    {
        var png = TestImages.Png();
        var gif = TestImages.AnimatedGif();
        var webp = TestImages.Webp(animated: true);
        var jpeg = TestImages.Jpeg();
        foreach (var (name, data) in new (string, byte[])[]
        {
            ("png cut short", png[..^5]),
            ("png with data after IEND", [.. png, 1, 2, 3]),
            ("png header only", png[..16]),
            ("gif without trailer", gif[..^1]),
            ("gif with a sub-block past the end", [.. gif[..^4], 0x40, 0x3B]),
            ("gif with data after the trailer", [.. gif, 0]),
            ("webp whose size field lies", [.. webp[..4], 0xFF, 0xFF, 0, 0, .. webp[8..]]),
            ("webp cut short", webp[..^3]),
            ("webp frame carrying a colour profile", WebpWithIccInFrame()),
            ("jpeg without end marker", jpeg[..^2]),
            ("svg", "<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>"u8.ToArray()),
            ("html", "<html><script>alert(1)</script></html>"u8.ToArray()),
            ("exe", [0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0, 0xFF, 0xFF]),
            ("gif header on a script", [.. "GIF89a"u8.ToArray(), .. "<script>alert(1)</script>"u8.ToArray()]),
        })
            Assert.True(ImageCheck.Inspect(data) is null, name);
    }

    [Fact]
    public void Still_images_and_animations_have_their_own_size_limits()
    {
        Assert.Equal(1024 * 1024, DeckRules.MaxStillBytes);
        Assert.Equal(4 * 1024 * 1024, DeckRules.MaxAnimationBytes);
        // A still image just over 1 MB: a PNG with a large ancillary chunk keeps it valid.
        var png = TestImages.Png();
        var padding = new byte[DeckRules.MaxStillBytes];
        byte[] text = [.. BigEndian(padding.Length), .. "zTXt"u8.ToArray(), .. padding, 0, 0, 0, 0];
        var big = (byte[])[.. png[..33], .. text, .. png[33..]];
        Assert.NotNull(ImageCheck.Inspect(big));
        Assert.Throws<DeckException>(() => DeckRules.CheckImage(big));
        Assert.Equal("gif", DeckRules.CheckImage(TestImages.AnimatedGif()).Extension);
        Assert.Throws<DeckException>(() => DeckRules.CheckImage(new byte[DeckRules.MaxAnimationBytes + 1]));
    }

    private static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>
    /// What naively wrapping a browser-encoded WebP into a frame produces: an ICCP chunk inside ANMF.
    /// Browsers refuse such a file, so GameDeck must too.
    /// </summary>
    private static byte[] WebpWithIccInFrame()
    {
        static byte[] Chunk(string type, byte[] data) =>
            [.. Encoding.ASCII.GetBytes(type), (byte)data.Length, (byte)(data.Length >> 8), 0, 0, .. data, .. (data.Length % 2 == 1 ? new byte[] { 0 } : [])];
        byte[] frame = [.. new byte[16], .. Chunk("ICCP", new byte[4]), .. Chunk("VP8L", [0x2F, 0, 0, 0, 0, 0])];
        byte[] body = [.. "WEBP"u8.ToArray(), .. Chunk("VP8X", [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0]), .. Chunk("ANIM", new byte[6]), .. Chunk("ANMF", frame), .. Chunk("ANMF", frame)];
        return [.. "RIFF"u8.ToArray(), (byte)body.Length, (byte)(body.Length >> 8), 0, 0, .. body];
    }

    [Fact]
    public void Images_are_recognised_by_content_and_names_cannot_escape_the_folder()
    {
        Assert.Equal("png", DeckRules.ImageExtension(TestImages.Png()));
        Assert.Equal("gif", DeckRules.ImageExtension(TestImages.AnimatedGif()));
        Assert.Null(DeckRules.ImageExtension("<svg onload=alert(1)>"u8));
        Assert.Null(DeckRules.ImageExtension("MZ\x90\0"u8));
        Assert.True(DeckRules.IsImageName("0123456789ab.png"));
        Assert.True(DeckRules.IsImageName("0123456789ab.gif"));
        Assert.False(DeckRules.IsImageName("0123456789ab.svg"));
        Assert.False(DeckRules.IsImageName(@"..\config.json"));
        Assert.False(DeckRules.IsImageName("0123456789ab.png/../x"));
        Assert.False(DeckRules.IsImageName("0123456789ab.exe"));
    }

    [Fact]
    public void A_damaged_config_is_neutralised_on_load_instead_of_failing()
    {
        var deck = new DeckConfig
        {
            Profiles = [new DeckProfile { Rows = 500, Columns = -3, Pages = [new DeckPage { Buttons = [new() { Slot = 0, Action = new() { Type = "launch", Target = @"C:\x.bat" } }, null!] }] }],
            Macros = [new DeckMacro { Steps = [new() { Type = "keys", Keys = "A" }, new() { Type = "stopAll" }] }],
            ActiveProfileId = "nope",
        };
        DeckRules.Normalize(deck);
        var p = Assert.Single(deck.Profiles);
        Assert.Equal((DeckRules.MaxRows, 1), (p.Rows, p.Columns));
        Assert.Equal("none", Assert.Single(p.Pages[0].Buttons).Action.Type); // the script launcher is disarmed
        Assert.Equal("stopAll", Assert.Single(deck.Macros[0].Steps).Type);
        Assert.Equal(p.Id, deck.ActiveProfileId);

        var empty = new DeckConfig();
        DeckRules.Normalize(empty);
        Assert.Single(Assert.Single(empty.Profiles).Pages);
    }
}

[Collection("Localization")]
public class DeckServiceTests : IAsyncLifetime
{
    private static readonly byte[] Png = TestImages.Png();
    private string _dir = "";
    private string _exe = "";
    private AudioEngine _engine = null!;
    private GameDeckService _service = null!;
    private readonly FakePlatform _platform = new();

    public async Task InitializeAsync()
    {
        _dir = TestAudio.TempDir();
        _exe = Path.Combine(TestAudio.TempDir(), "Game.exe");
        await File.WriteAllBytesAsync(_exe, [0x4D, 0x5A]);
        var store = new ConfigStore(_dir);
        store.Update(c => { c.Audio.MonitorEnabled = false; c.Audio.MicVolume = 1f; c.Audio.MonitorVolume = 0.5f; });
        _engine = await Task.Run(() => new AudioEngine());
        _service = new GameDeckService(store, _engine, new PairingService()) { Platform = _platform };
        await Task.Run(_service.Start);
    }

    public async Task DisposeAsync()
    {
        _service.Macros.StopAll();
        await Task.Run(_engine.Dispose);
    }

    private DeckProfile Active() => _service.BuildState().Deck.Profiles.Single(p => p.Id == _service.BuildState().Deck.ActiveProfileId);

    private string AddButton(DeckAction action, bool local = true)
    {
        var p = Active();
        var slot = p.Pages[0].Buttons.Count;
        p.Pages[0].Buttons.Add(new DeckButton { Slot = slot, Label = action.Type, Action = action });
        return _service.SaveDeckProfile(p.Id, p, local).Pages[0].Buttons.Single(b => b.Slot == slot).Id;
    }

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "condition not reached in time");
    }

    [Fact]
    public void A_fresh_install_has_one_deck_profile_and_edits_survive_a_restart()
    {
        var first = Assert.Single(_service.BuildState().Deck.Profiles);
        Assert.Equal((3, 5, 1), (first.Rows, first.Columns, first.Pages.Count));

        first.Rows = 4;
        first.Pages.Add(new DeckPage { Name = "Stream" });
        first.Pages[0].Buttons.Add(new DeckButton { Slot = 7, Label = "Mute", Icon = "mic", Color = "#c9a4a4", Action = new() { Type = "mute", Channel = "mic" } });
        _service.SaveDeckProfile(first.Id, first, local: false);
        var gaming = _service.CreateDeckProfile(new DeckProfileCreate("Gaming"));
        var copy = _service.CreateDeckProfile(new DeckProfileCreate(null, first.Id));
        _service.SaveDeckProfile(gaming.Id, new DeckProfile { Name = "  Ranked  ", Rows = 2, Columns = 3 }, local: false);

        var reloaded = new ConfigStore(_dir).Read(c => c.Deck);
        Assert.Equal(3, reloaded.Profiles.Count);
        var main = reloaded.Profiles.Single(p => p.Id == first.Id);
        Assert.Equal((4, 2, "Stream"), (main.Rows, main.Pages.Count, main.Pages[1].Name));
        var button = Assert.Single(main.Pages[0].Buttons);
        Assert.Equal((7, "Mute", "mic", "#c9a4a4", "mute"), (button.Slot, button.Label, button.Icon, button.Color, button.Action.Type));
        Assert.Equal("Ranked", reloaded.Profiles.Single(p => p.Id == gaming.Id).Name);
        Assert.NotEqual(button.Id, reloaded.Profiles.Single(p => p.Id == copy.Id).Pages[0].Buttons.Single().Id); // a duplicate has its own buttons
        Assert.Equal(copy.Id, reloaded.ActiveProfileId);

        _service.ActivateDeckProfile(first.Id);
        _service.DeleteDeckProfile(copy.Id);
        _service.DeleteDeckProfile(gaming.Id);
        Assert.Equal(400, Assert.Throws<ApiException>(() => _service.DeleteDeckProfile(first.Id)).Status); // never the last one
        Assert.Equal(404, Assert.Throws<ApiException>(() => _service.ActivateDeckProfile(gaming.Id)).Status);
    }

    [Fact]
    public async Task State_pushes_are_fast_and_the_routing_report_matches_a_fresh_read_of_Windows()
    {
        var cached = JsonSerializer.Serialize(await Task.Run(_service.GetRouting));
        var audio = _service.Store.Read(c => c.Audio);
        var fresh = JsonSerializer.Serialize(await Task.Run(() => RoutingAdvisor.Build(audio, _engine.GetStatus(), _engine.GetMicStats())));
        Assert.Equal(fresh, cached);

        // Button states (mute, active scene, running macro) reach every screen through this; it must not
        // re-enumerate every audio endpoint each time.
        await Task.Run(_service.BuildState);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) await Task.Run(_service.BuildState);
        Assert.True(sw.ElapsedMilliseconds / 10 < 150, $"{sw.ElapsedMilliseconds / 10} ms per state");
    }

    [Fact]
    public async Task Launch_link_key_and_media_buttons_reach_Windows_only_as_configured()
    {
        var launch = AddButton(new() { Type = "launch", Target = _exe });
        var folder = AddButton(new() { Type = "launch", Target = Path.GetDirectoryName(_exe) });
        var url = AddButton(new() { Type = "url", Target = "https://example.com/" });
        var keys = AddButton(new() { Type = "keys", Keys = "ctrl+shift+s" });
        var media = AddButton(new() { Type = "media", Mode = "next" });
        foreach (var id in new[] { launch, folder, url, keys, media }) Assert.True((await _service.PressButtonAsync(id)).Ok);

        Assert.Equal([$"launch {_exe}", $"launch {Path.GetDirectoryName(_exe)}", "url https://example.com/", "keys Ctrl+Shift+S", "media next"], _platform.Calls);

        Assert.Equal(400, Assert.Throws<ApiException>(() => AddButton(new() { Type = "launch", Target = @"C:\tools\run.bat" })).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => AddButton(new() { Type = "launch", Target = Path.Combine(_dir, "sounds", "a.exe") })).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => AddButton(new() { Type = "keys", Keys = "A" })).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => _service.PressButtonAsync("000000000000"))).Status);
    }

    [Fact]
    public async Task A_missing_program_or_a_Windows_failure_is_reported_not_swallowed()
    {
        var id = AddButton(new() { Type = "launch", Target = _exe });
        File.Delete(_exe);
        var gone = await Assert.ThrowsAsync<ApiException>(() => _service.PressButtonAsync(id));
        Assert.Equal(409, gone.Status);
        Assert.Contains("Game.exe", gone.Message);

        var keys = AddButton(new() { Type = "keys", Keys = "Ctrl+F9" });
        _platform.Fail = new InvalidOperationException("blocked");
        Assert.Contains("blocked", (await Assert.ThrowsAsync<ApiException>(() => _service.PressButtonAsync(keys))).Message);
        Assert.Empty(_platform.Calls);
    }

    [Fact]
    public async Task A_remote_device_can_press_but_cannot_add_or_change_program_link_or_key_actions()
    {
        var launch = AddButton(new() { Type = "launch", Target = _exe }, local: true);

        foreach (var action in new DeckAction[] { new() { Type = "launch", Target = @"C:\Windows\System32\calc.exe" }, new() { Type = "url", Target = "https://evil.example/" }, new() { Type = "keys", Keys = "Win+R" } })
            Assert.Equal(403, Assert.Throws<ApiException>(() => AddButton(action, local: false)).Status);
        Assert.Equal(403, Assert.Throws<ApiException>(() => _service.SaveMacro(null, new DeckMacro { Steps = [new() { Type = "keys", Keys = "Win+R" }] }, local: false)).Status);
        Assert.DoesNotContain(Active().Pages[0].Buttons, b => b.Action.Target?.Contains("calc") == true); // nothing was stored

        // Retargeting the existing launch button is a change too.
        var p = Active();
        p.Pages[0].Buttons.Single(b => b.Id == launch).Action.Target = @"C:\Windows\System32\calc.exe";
        Assert.Equal(403, Assert.Throws<ApiException>(() => _service.SaveDeckProfile(p.Id, p, local: false)).Status);

        // Harmless edits and moving the existing button are fine, and so is pressing it.
        AddButton(new() { Type = "mute", Channel = "mic" }, local: false);
        p = Active();
        p.Pages[0].Buttons.Single(b => b.Id == launch).Slot = 9;
        p.Pages[0].Buttons.Single(b => b.Id == launch).Label = "Play";
        _service.SaveDeckProfile(p.Id, p, local: false);
        Assert.True((await _service.PressButtonAsync(launch)).Ok);
        Assert.Equal([$"launch {_exe}"], _platform.Calls);
    }

    [Fact]
    public async Task Audio_buttons_drive_the_existing_mixer_and_scenes()
    {
        var mute = AddButton(new() { Type = "mute", Channel = "mic" });
        var up = AddButton(new() { Type = "volume", Channel = "monitor", Mode = "up", Value = 0.1f });
        var set = AddButton(new() { Type = "volume", Channel = "mic", Mode = "set", Value = 0.4f });
        var scene = _service.CreateScene(new SceneCreate("Quiet", null));
        _service.UpdateScene(scene.Id, new ScenePatch(Apps: [], Channels: new SceneChannels { MicVolume = 1f, VirtualVolume = 0.3f, MonitorVolume = 0.2f }));
        var apply = AddButton(new() { Type = "scene", Target = scene.Id });
        var missingSound = AddButton(new() { Type = "sound", Target = "0123456789ab" });
        var devices = _service.Store.Read(c => (c.Audio.MonitorDeviceId, c.Audio.VirtualDeviceId, c.Audio.MicDeviceId, c.Audio.MicEnabled));

        await _service.PressButtonAsync(mute);
        Assert.True(_service.Store.Read(c => c.Audio.MicMuted));
        await Task.Delay(350);
        await _service.PressButtonAsync(mute);
        Assert.False(_service.Store.Read(c => c.Audio.MicMuted));

        await _service.PressButtonAsync(up);
        await _service.PressButtonAsync(set);
        Assert.Equal((0.6f, 0.4f), _service.Store.Read(c => (MathF.Round(c.Audio.MonitorVolume, 2), c.Audio.MicVolume)));

        await _service.PressButtonAsync(apply);
        Assert.Equal(scene.Id, _service.ActiveSceneId);
        Assert.Equal(0.3f, _service.Store.Read(c => c.Audio.VirtualVolume));

        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => _service.PressButtonAsync(missingSound))).Status);
        Assert.Equal(devices, _service.Store.Read(c => (c.Audio.MonitorDeviceId, c.Audio.VirtualDeviceId, c.Audio.MicDeviceId, c.Audio.MicEnabled))); // routing untouched
        Assert.Empty(_platform.Calls);
    }

    private string AddFullButton(DeckButton button, bool local = true)
    {
        var p = Active();
        button.Slot = p.Pages[0].Buttons.Count;
        p.Pages[0].Buttons.Add(button);
        return _service.SaveDeckProfile(p.Id, p, local).Pages[0].Buttons.Single(b => b.Slot == button.Slot).Id;
    }

    [Fact]
    public async Task A_long_press_runs_its_own_action_and_a_short_one_the_normal_action()
    {
        var id = AddFullButton(new DeckButton
        {
            Label = "Clip",
            Action = new() { Type = "keys", Keys = "Alt+F10" },
            HoldAction = new() { Type = "media", Mode = "playPause" },
        });

        await _service.PressButtonAsync(id);
        await _service.PressButtonAsync(id, hold: true);   // right after the tap: a different gesture, not a double tap
        Assert.Equal(["keys Alt+F10", "media playPause"], _platform.Calls);

        var plain = AddButton(new() { Type = "stopAll" });
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => _service.PressButtonAsync(plain, hold: true))).Status);
    }

    [Fact]
    public async Task A_two_state_button_alternates_and_every_device_sees_its_state_even_after_a_restart()
    {
        var id = AddFullButton(new DeckButton
        {
            Label = "Stream",
            Action = new() { Type = "keys", Keys = "Ctrl+F7" },           // e.g. start streaming
            ToggleAction = new() { Type = "keys", Keys = "Ctrl+F8" },     // stop streaming
        });

        await _service.PressButtonAsync(id);
        Assert.Equal([id], _service.BuildState().Deck.Toggled);
        await Task.Delay(320);
        await _service.PressButtonAsync(id);
        Assert.Empty(_service.BuildState().Deck.Toggled);
        await Task.Delay(320);
        await _service.PressButtonAsync(id);
        Assert.Equal(["keys Ctrl+F7", "keys Ctrl+F8", "keys Ctrl+F7"], _platform.Calls);
        Assert.Equal([id], new ConfigStore(_dir).Read(c => c.Deck.Toggled)); // kept on disk

        // Saving the layout from a device that still has an old copy does not reset the state.
        var p = Active();
        p.Name = "Renamed";
        _service.SaveDeckProfile(p.Id, p, local: true);
        Assert.Equal([id], _service.BuildState().Deck.Toggled);

        // A failed action leaves the state where it was.
        _platform.Fail = new InvalidOperationException("blocked");
        await Task.Delay(320);
        await Assert.ThrowsAsync<ApiException>(() => _service.PressButtonAsync(id));
        Assert.Equal([id], _service.BuildState().Deck.Toggled);

        // Turning the button back into an ordinary one forgets its state.
        _platform.Fail = null;
        p = Active();
        p.Pages[0].Buttons.Single(b => b.Id == id).ToggleAction = null;
        _service.SaveDeckProfile(p.Id, p, local: true);
        Assert.Empty(_service.BuildState().Deck.Toggled);
    }

    [Fact]
    public void Long_press_and_second_state_actions_follow_the_same_rules_as_the_main_action()
    {
        // A remote device can't sneak a program, link or keys in through the extra actions.
        Assert.Equal(403, Assert.Throws<ApiException>(() => AddFullButton(new DeckButton { Action = new() { Type = "stopAll" }, HoldAction = new() { Type = "launch", Target = _exe } }, local: false)).Status);
        Assert.Equal(403, Assert.Throws<ApiException>(() => AddFullButton(new DeckButton { Action = new() { Type = "stopAll" }, ToggleAction = new() { Type = "keys", Keys = "Win+R" } }, local: false)).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => AddFullButton(new DeckButton { Action = new() { Type = "stopAll" }, HoldAction = new() { Type = "launch", Target = @"C:\x\run.bat" } })).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => AddFullButton(new DeckButton { Action = new() { Type = "stopAll" }, ToggleAction = new() { Type = "sensor", Sensor = "cpu.usage" } })).Status);

        // "Nothing" as an extra action simply means none.
        var id = AddFullButton(new DeckButton { Action = new() { Type = "stopAll" }, HoldAction = new() { Type = "none" }, ToggleAction = new() { Type = "none" } });
        var saved = Active().Pages[0].Buttons.Single(b => b.Id == id);
        Assert.Null(saved.HoldAction);
        Assert.Null(saved.ToggleAction);
    }

    [Fact]
    public async Task A_two_state_button_can_open_a_program_and_then_close_it()
    {
        var id = AddFullButton(new DeckButton
        {
            Label = "OBS",
            Action = new() { Type = "launch", Target = _exe },
            ToggleAction = new() { Type = "window", Target = "OBS64.EXE", Mode = "close" },
        });
        _platform.Open.Clear();
        await _service.PressButtonAsync(id);           // not running: opens it
        _platform.Open.Add("obs64.exe");
        await Task.Delay(320);
        await _service.PressButtonAsync(id);           // running: closes it
        Assert.Equal([$"launch {_exe}", "window obs64.exe close"], _platform.Calls);

        var minimize = AddButton(new() { Type = "window", Target = "obs64.exe", Mode = "minimize" });
        await _service.PressButtonAsync(minimize);
        Assert.Equal("window obs64.exe minimize", _platform.Calls.Last());

        // Nothing open: said plainly, and a two-state button stays where it was.
        _platform.Open.Clear();
        await Task.Delay(320);
        var notOpen = await Assert.ThrowsAsync<ApiException>(() => _service.PressButtonAsync(minimize));
        Assert.Equal(409, notOpen.Status);
        Assert.Contains("obs64.exe", notOpen.Message);
        // The open/close button never tries to close a program that isn't open: it opens it instead.
        _platform.Calls.Clear();
        await _service.PressButtonAsync(id);
        await Task.Delay(320);
        await _service.PressButtonAsync(id);                                   // its window hasn't appeared yet
        Assert.Equal([$"launch {_exe}", $"launch {_exe}"], _platform.Calls);
        Assert.Empty(_service.BuildState().Deck.Toggled);                      // shown as closed, as it is
    }

    [Fact]
    public async Task An_open_close_button_follows_the_program_even_when_it_was_closed_another_way()
    {
        _platform.Open.Clear();
        var id = AddFullButton(new DeckButton
        {
            Label = "OBS",
            Action = new() { Type = "launch", Target = _exe },
            ToggleAction = new() { Type = "window", Target = "obs64.exe", Mode = "close" },
            HoldAction = new() { Type = "window", Target = "obs64.exe", Mode = "close" },
        });

        await _service.PressButtonAsync(id);                 // opens it
        _platform.Open.Add("obs64.exe");                     // ...and its window appears
        Assert.Contains(id, _service.BuildState().Deck.Toggled);

        // The reported bug: closed by the long press (or its X), then a tap must open it again, not try to close.
        await _service.PressButtonAsync(id, hold: true);
        _platform.Open.Remove("obs64.exe");
        Assert.DoesNotContain(id, _service.BuildState().Deck.Toggled); // every screen shows it closed
        await Task.Delay(320);
        await _service.PressButtonAsync(id);
        Assert.Equal([$"launch {_exe}", "window obs64.exe close", $"launch {_exe}"], _platform.Calls);

        // Opened outside GameDeck while the button thought it was closed: a tap closes it.
        _platform.Calls.Clear();
        _service.Store.Update(c => c.Deck.Toggled.Clear());
        _platform.Open.Add("obs64.exe");
        await Task.Delay(320);
        await _service.PressButtonAsync(id);
        Assert.Equal(["window obs64.exe close"], _platform.Calls);
    }

    [Fact]
    public async Task A_show_minimize_button_follows_whether_the_window_is_visible()
    {
        _platform.Open.Add("discord.exe");
        var id = AddFullButton(new DeckButton
        {
            Label = "Discord",
            Action = new() { Type = "window", Target = "discord.exe", Mode = "restore" },
            ToggleAction = new() { Type = "window", Target = "discord.exe", Mode = "minimize" },
        });

        await _service.PressButtonAsync(id);                  // visible → minimize
        _platform.Minimized.Add("discord.exe");
        Assert.DoesNotContain(id, _service.BuildState().Deck.Toggled);
        await Task.Delay(320);
        await _service.PressButtonAsync(id);                  // minimized → show
        Assert.Equal(["window discord.exe minimize", "window discord.exe restore"], _platform.Calls);

        // Buttons that don't act on a program keep their remembered state.
        var plain = AddFullButton(new DeckButton { Action = new() { Type = "keys", Keys = "Ctrl+F7" }, ToggleAction = new() { Type = "keys", Keys = "Ctrl+F8" } });
        await _service.PressButtonAsync(plain);
        Assert.Contains(plain, _service.BuildState().Deck.Toggled);
    }

    [Fact]
    public async Task The_program_behind_a_launch_path_is_known_even_through_a_shortcut()
    {
        var dir = TestAudio.TempDir();
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var plain = Path.Combine(dir, "Notepad.lnk");
        await GameDeck.App.Deck.ShellLink.SaveAsync(plain, notepad, "");
        // How Squirrel-installed apps (Discord, Slack) are started: an updater that launches the real program.
        var updater = Path.Combine(dir, "Update.exe");
        File.Copy(notepad, updater);
        var squirrel = Path.Combine(dir, "Discord.lnk");
        await GameDeck.App.Deck.ShellLink.SaveAsync(squirrel, updater, "--processStart \"Discord.exe\"");
        var document = Path.Combine(dir, "notes.txt");
        await File.WriteAllTextAsync(document, "x");
        var toDocument = Path.Combine(dir, "Notes.lnk");
        await GameDeck.App.Deck.ShellLink.SaveAsync(toDocument, document, "");

        Assert.Equal("game.exe", await _service.ProgramOfAsync(_exe, local: true));
        Assert.Equal("notepad.exe", await _service.ProgramOfAsync(plain, local: true));
        Assert.Equal("discord.exe", await _service.ProgramOfAsync(squirrel, local: true));
        Assert.Null(await _service.ProgramOfAsync(toDocument, local: true));     // a document: no program to name
        Assert.Null(await _service.ProgramOfAsync(dir, local: true));            // a folder
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => _service.ProgramOfAsync(plain, local: false))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => _service.ProgramOfAsync(@"C:\x\run.bat", local: true))).Status);
    }

    [Fact]
    public void Window_actions_name_a_program_never_a_path_or_GameDeck_and_only_the_PC_may_add_them()
    {
        foreach (var bad in new[] { @"C:\Program Files\obs\obs64.exe", "obs64", "../obs64.exe", "obs64.bat", "x.exe:stream", "GameDeck.exe", "" })
            Assert.Throws<DeckException>(() => DeckRules.CleanAction(new DeckAction { Type = "window", Target = bad, Mode = "close" }, false, ""));
        Assert.Throws<DeckException>(() => DeckRules.CleanAction(new DeckAction { Type = "window", Target = "obs64.exe", Mode = "kill" }, false, ""));
        Assert.Equal("obs64.exe", DeckRules.CleanAction(new DeckAction { Type = "window", Target = " \"OBS64.exe\" ", Mode = "restore" }, false, "").Target);

        Assert.Equal(403, Assert.Throws<ApiException>(() => AddButton(new() { Type = "window", Target = "obs64.exe", Mode = "close" }, local: false)).Status);
        Assert.Equal(403, Assert.Throws<ApiException>(() => _service.OpenWindows(local: false)).Status);
        Assert.Equal(["obs64.exe"], _service.OpenWindows(local: true).Select(w => w.Program));
    }

    [Fact]
    public async Task A_double_tap_runs_the_action_once()
    {
        var mute = AddButton(new() { Type = "mute", Channel = "virtual" });
        var first = await _service.PressButtonAsync(mute);
        var second = await _service.PressButtonAsync(mute);
        Assert.Equal((false, true), (first.Ignored, second.Ignored));
        Assert.True(_service.Store.Read(c => c.Audio.VirtualMuted)); // toggled once, not twice
    }

    [Fact]
    public async Task A_macro_runs_its_steps_in_order_and_shows_as_running()
    {
        var macro = _service.SaveMacro(null, new DeckMacro
        {
            Name = "Go live",
            Steps = [new() { Type = "keys", Keys = "Ctrl+F1" }, new() { Type = "wait", DelayMs = 4000 }, new() { Type = "mute", Channel = "mic", Mode = "on" }, new() { Type = "url", Target = "https://example.com/" }],
        }, local: true);
        var button = AddButton(new() { Type = "macro", Target = macro.Id });

        await _service.PressButtonAsync(button);
        // Checked right after the press: building the full state can take a while on a busy test machine.
        Assert.True(_service.Macros.IsRunning(macro.Id));
        Assert.Equal(409, Assert.Throws<ApiException>(() => _service.RunMacro(macro.Id)).Status); // not twice at once
        await Until(() => _platform.Calls.Count == 1);
        Assert.Contains(macro.Id, _service.BuildState().Deck.RunningMacros);                         // what the dashboards see
        Assert.Equal(["keys Ctrl+F1"], _platform.Calls);                                            // still waiting

        await Until(() => !_service.Macros.IsRunning(macro.Id), 10_000);
        Assert.Equal(["keys Ctrl+F1", "url https://example.com/"], _platform.Calls);
        Assert.True(_service.Store.Read(c => c.Audio.MicMuted));
        Assert.Empty(_service.BuildState().Deck.MacroErrors);
    }

    [Fact]
    public async Task A_macro_can_be_cancelled_stops_at_a_failing_step_and_times_out()
    {
        var slow = _service.SaveMacro(null, new DeckMacro { Steps = [new() { Type = "keys", Keys = "Ctrl+F1" }, new() { Type = "wait", DelayMs = 30_000 }, new() { Type = "keys", Keys = "Ctrl+F2" }] }, true);
        _service.RunMacro(slow.Id);
        await Until(() => _platform.Calls.Count == 1);
        _service.StopMacro(slow.Id);
        await Until(() => !_service.Macros.IsRunning(slow.Id));
        Assert.Equal(["keys Ctrl+F1"], _platform.Calls);       // the step after the wait never ran
        Assert.Empty(_service.Macros.Errors);                  // a deliberate stop is not an error

        var broken = _service.SaveMacro(null, new DeckMacro { Steps = [new() { Type = "sound", Target = "0123456789ab" }, new() { Type = "keys", Keys = "Ctrl+F3" }] }, true);
        _service.RunMacro(broken.Id);
        await Until(() => _service.Macros.Errors.ContainsKey(broken.Id));
        Assert.DoesNotContain("keys Ctrl+F3", _platform.Calls);
        Assert.Contains("1", _service.BuildState().Deck.MacroErrors[broken.Id]); // names the step

        var previous = MacroRunner.Timeout;
        MacroRunner.Timeout = TimeSpan.FromMilliseconds(200);
        try
        {
            _service.RunMacro(slow.Id);
            await Until(() => _service.Macros.Errors.ContainsKey(slow.Id));
            Assert.False(_service.Macros.IsRunning(slow.Id));
        }
        finally { MacroRunner.Timeout = previous; }

        Assert.Equal(400, Assert.Throws<ApiException>(() => _service.SaveMacro(null, new DeckMacro { Steps = [new() { Type = "macro", Target = slow.Id }] }, true)).Status);
        _service.DeleteMacro(slow.Id);
        Assert.Equal(404, Assert.Throws<ApiException>(() => _service.RunMacro(slow.Id)).Status);
    }

    [Fact]
    public async Task The_file_picker_opens_only_for_the_PC_itself_and_one_at_a_time()
    {
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => _service.BrowseForPathAsync(false, null, local: true))).Status); // no tray, no dialog

        var open = new TaskCompletionSource<string?>();
        var asked = new List<(bool Folder, string? Current)>();
        _service.PathPicker = (folder, current) => { asked.Add((folder, current)); return open.Task; };

        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => _service.BrowseForPathAsync(false, null, local: false))).Status);
        var first = _service.BrowseForPathAsync(true, _exe, local: true);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => _service.BrowseForPathAsync(false, null, local: true))).Status);
        open.SetResult(@"C:\Games");
        Assert.Equal(@"C:\Games", await first);
        Assert.Equal([(true, _exe)], asked);                    // the phone's request never reached the picker

        _service.PathPicker = (_, _) => Task.FromResult<string?>(null); // cancelled
        Assert.Null(await _service.BrowseForPathAsync(false, null, local: true));
    }

    [Fact]
    public async Task A_programs_own_icon_becomes_a_sharp_transparent_button_image_on_the_PC_only()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var name = await _service.SaveProgramIconAsync(notepad, local: true);

        var png = File.ReadAllBytes(_service.DeckImageFile(name).Path);
        Assert.Equal("png", DeckRules.ImageExtension(png));
        using (var image = new System.Drawing.Bitmap(new MemoryStream(png)))
        {
            Assert.True(image.Width >= 128, $"{image.Width}px");
            Assert.Equal(0, image.GetPixel(0, image.Height - 1).A);              // transparent corner, not a black box
            Assert.Contains(Enumerable.Range(0, image.Width), x => image.GetPixel(x, image.Height / 2).A == 255); // the icon itself
        }

        var folderIcon = await _service.SaveProgramIconAsync(Path.GetDirectoryName(_exe), local: true);
        Assert.NotEqual(name, folderIcon);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => _service.SaveProgramIconAsync(notepad, local: false))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => _service.SaveProgramIconAsync(@"C:\tools\run.bat", local: true))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => _service.SaveProgramIconAsync(@"C:\no\such\game.exe", local: true))).Status);
    }

    [Fact]
    public async Task Images_are_checked_by_content_and_served_only_by_safe_names()
    {
        var name = (await _service.SaveDeckImageAsync(new MemoryStream(Png), Png.Length)).Name;
        Assert.Matches(@"^[a-f0-9]{12}\.png$", name);
        Assert.Equal("image/png", _service.DeckImageFile(name).ContentType);

        var gif = TestImages.AnimatedGif(frames: 4);
        var upload = await _service.SaveDeckImageAsync(new MemoryStream(gif), gif.Length);
        Assert.Equal((true, 4, 1, 1), (upload.Animated, upload.Frames, upload.Width, upload.Height));
        Assert.Equal("image/gif", _service.DeckImageFile(upload.Name).ContentType);
        Assert.Equal(gif, File.ReadAllBytes(_service.DeckImageFile(upload.Name).Path)); // stored as uploaded, never re-encoded
        var webp = TestImages.Webp(animated: true);
        Assert.True((await _service.SaveDeckImageAsync(new MemoryStream(webp), webp.Length)).Animated);
        var truncated = gif[..^1];
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => _service.SaveDeckImageAsync(new MemoryStream(truncated), truncated.Length))).Status);

        var script = Encoding.UTF8.GetBytes("<script>alert(1)</script>");
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => _service.SaveDeckImageAsync(new MemoryStream(script), script.Length))).Status);
        var huge = new byte[DeckRules.MaxImageBytes + 1];
        Png.CopyTo(huge, 0);
        Assert.Equal(413, (await Assert.ThrowsAsync<ApiException>(() => _service.SaveDeckImageAsync(new MemoryStream(huge), 10))).Status); // a lying length doesn't help
        Assert.Equal(404, Assert.Throws<ApiException>(() => _service.DeckImageFile(@"..\config.json")).Status);
        Assert.Equal(404, Assert.Throws<ApiException>(() => _service.DeckImageFile("ffffffffffff.png")).Status);
    }

    [Fact]
    public async Task Export_and_import_round_trip_with_new_ids_and_everything_revalidated()
    {
        var image = (await _service.SaveDeckImageAsync(new MemoryStream(Png), Png.Length)).Name;
        var activeGif = TestImages.AnimatedGif();
        var activeImage = (await _service.SaveDeckImageAsync(new MemoryStream(activeGif), activeGif.Length)).Name;
        var macro = _service.SaveMacro(null, new DeckMacro { Name = "M", Steps = [new() { Type = "stopAll" }] }, true);
        var p = Active();
        p.Name = "Streaming";
        p.Pages[0].Buttons.Add(new DeckButton { Slot = 0, Label = "Run", Image = image, ActiveImage = activeImage, ActiveColor = "#c9a4a4", Animation = "active", Action = new() { Type = "macro", Target = macro.Id } });
        p.Pages[0].Buttons.Add(new DeckButton { Slot = 1, Label = "CPU", Action = new() { Type = "sensor", Sensor = "cpu.usage", Unit = "%" } });
        _service.SaveDeckProfile(p.Id, p, true);

        var file = JsonSerializer.Deserialize<DeckExport>(JsonSerializer.Serialize(_service.ExportDeckProfile(p.Id)))!; // as it would travel
        var imported = _service.ImportDeck(file, local: false);

        Assert.Equal("Streaming", imported.Name);
        Assert.NotEqual(p.Id, imported.Id);
        var run = imported.Pages[0].Buttons.Single(b => b.Label == "Run");
        Assert.NotEqual(macro.Id, run.Action.Target);                                               // points at its own copy of the macro
        Assert.Contains(_service.BuildState().Deck.Macros, m => m.Id == run.Action.Target && m.Name == "M");
        Assert.NotEqual(image, run.Image);
        Assert.Equal(Png, File.ReadAllBytes(_service.DeckImageFile(run.Image!).Path));
        Assert.NotEqual(activeImage, run.ActiveImage);                                              // the animation travels too, under a new name
        Assert.Equal(activeGif, File.ReadAllBytes(_service.DeckImageFile(run.ActiveImage!).Path));
        Assert.Equal(("active", "#c9a4a4"), (run.Animation, run.ActiveColor));
        Assert.Equal("cpu.usage", imported.Pages[0].Buttons.Single(b => b.Label == "CPU").Action.Sensor);

        DeckExport With(Action<DeckExport> change) { var f = JsonSerializer.Deserialize<DeckExport>(JsonSerializer.Serialize(file))!; change(f); return f; }
        Assert.Equal(400, Assert.Throws<ApiException>(() => _service.ImportDeck(file with { Format = "something-else" }, true)).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => _service.ImportDeck(file with { Version = 2 }, true)).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => _service.ImportDeck(null, true)).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => _service.ImportDeck(With(f => f.Images![image] = Convert.ToBase64String("MZ not an image"u8)), true)).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => _service.ImportDeck(With(f => f.Images![@"..\..\evil.png"] = Convert.ToBase64String(Png)), true)).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => _service.ImportDeck(With(f => f.Profile!.Pages[0].Buttons[0].Action = new() { Type = "launch", Target = @"C:\x.ps1" }), true)).Status);
        Assert.Equal(403, Assert.Throws<ApiException>(() => _service.ImportDeck(With(f => f.Profile!.Pages[0].Buttons[0].Action = new() { Type = "launch", Target = @"C:\Windows\notepad.exe" }), local: false)).Status);
        Assert.Equal(2, _service.BuildState().Deck.Profiles.Count); // none of the rejected files left a profile behind
        Assert.False(File.Exists(Path.Combine(_dir, "evil.png")));
    }
}

public class HardwareMonitorTests
{
    [Fact]
    public async Task Readings_are_real_in_range_and_missing_sensors_stay_null()
    {
        using var monitor = new HardwareMonitor();
        monitor.Sample();
        await Task.Delay(1100);
        var s = monitor.Sample();

        Assert.False(string.IsNullOrWhiteSpace(s.Cpu.Model));
        Assert.InRange(s.Cpu.UsagePercent!.Value, 0, 100);
        Assert.Null(s.Cpu.TemperatureC);                       // no driver, so no value: never invented
        if (s.Cpu.ClockMhz is { } mhz) Assert.InRange(mhz, 200, 10_000);
        Assert.NotNull(s.Memory);
        Assert.InRange(s.Memory!.UsagePercent, 1, 100);
        Assert.Equal(s.Memory.TotalMb, s.Memory.UsedMb + s.Memory.AvailableMb, 1);
        Assert.NotEmpty(s.Drives);
        Assert.All(s.Drives, d => Assert.InRange(d.UsagePercent, 0, 100));
        Assert.Contains(s.Gpu.Source, new[] { "nvml", "windows", "none" });
        if (s.Gpu.Source == "nvml")
        {
            Assert.InRange(s.Gpu.TemperatureC!.Value, 0, 120);
            Assert.InRange(s.Gpu.VramUsedMb!.Value, 0, s.Gpu.VramTotalMb!.Value);
        }
    }

    [Fact]
    public async Task Without_the_vendor_library_it_falls_back_and_never_reports_a_temperature()
    {
        using var monitor = new HardwareMonitor(useNvml: false);
        monitor.Sample();
        await Task.Delay(1100);
        var s = monitor.Sample();

        Assert.NotEqual("nvml", s.Gpu.Source);
        Assert.Null(s.Gpu.TemperatureC);
        Assert.Null(s.Gpu.ClockMhz);
        Assert.Null(s.Gpu.FanPercent);
        if (s.Gpu.UsagePercent is { } u) Assert.InRange(u, 0, 100);
        Assert.NotNull(s.Memory); // the rest keeps working

        monitor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => monitor.Sample());
    }
}

/// <summary>The deck over the real server: pairing is required, state and hardware arrive over the socket.</summary>
[Collection("Localization")]
public class DeckServerTests : IAsyncLifetime
{
    private AudioEngine _engine = null!;
    private GameDeckService _service = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private readonly CookieContainer _cookies = new();
    private readonly FakePlatform _platform = new();
    private string _base = "";

    public async Task InitializeAsync()
    {
        var store = new ConfigStore(TestAudio.TempDir());
        store.Update(c => { c.Server.Port = FreePort(); c.Audio.MonitorEnabled = false; });
        _engine = await Task.Run(() => new AudioEngine());
        _service = new GameDeckService(store, _engine, new PairingService()) { Platform = _platform };
        await Task.Run(_service.Start);
        WsHub.HardwareInterval = TimeSpan.FromMilliseconds(300);
        _app = DashboardServer.Build(_service, IPAddress.Loopback);
        await _app.StartAsync();
        _base = $"http://127.0.0.1:{_service.Port}";
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies }) { BaseAddress = new Uri(_base) };
    }

    public async Task DisposeAsync()
    {
        WsHub.HardwareInterval = TimeSpan.FromSeconds(2);
        _http.Dispose();
        await _app.StopAsync();
        await Task.Run(_engine.Dispose);
    }

    [Fact]
    public async Task Nothing_in_the_deck_or_the_hardware_monitor_works_without_pairing()
    {
        var profile = _service.BuildState().Deck.ActiveProfileId;
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsync("/api/deck/buttons/abc/press", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PutAsJsonAsync($"/api/deck/profiles/{profile}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsJsonAsync("/api/deck/import", new { format = "gamedeck-deck" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsJsonAsync("/api/deck/macros", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsync("/api/deck/macros/abc/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/deck/images/0123456789ab.png")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/hardware")).StatusCode);
        Assert.Empty(_platform.Calls);
    }

    [Fact]
    public async Task A_paired_device_edits_and_presses_and_every_client_sees_it_live()
    {
        var code = _service.Pairing.GetCurrentCode().Code;
        var session = await (await _http.PostAsJsonAsync("/api/pair", new { code, deviceName = "phone" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(session.GetProperty("local").GetBoolean()); // loopback counts as the PC itself

        using var ws = new ClientWebSocket();
        ws.Options.Cookies = _cookies;
        ws.Options.SetRequestHeader("Origin", _base);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_service.Port}/ws"), CancellationToken.None);
        var first = await Next(ws, "state", _ => true);
        var profileId = first.GetProperty("deck").GetProperty("activeProfileId").GetString()!;

        var put = await _http.PutAsJsonAsync($"/api/deck/profiles/{profileId}", new
        {
            name = "Desk", rows = 2, columns = 4,
            pages = new[] { new { name = "One", buttons = new object[] {
                new { slot = 0, label = "Mic", icon = "mic", color = "#a9d6ff", action = new { type = "mute", channel = "mic" } },
                new { slot = 5, label = "Save", action = new { type = "keys", keys = "ctrl+s" } },
            } } },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await put.Content.ReadFromJsonAsync<JsonElement>();
        var buttons = saved.GetProperty("pages")[0].GetProperty("buttons");
        Assert.Equal("Ctrl+S", buttons[1].GetProperty("action").GetProperty("keys").GetString());

        var pushed = await Next(ws, "state", s => s.GetProperty("deck").GetProperty("profiles")[0].GetProperty("name").GetString() == "Desk");
        Assert.Equal(4, pushed.GetProperty("deck").GetProperty("profiles")[0].GetProperty("columns").GetInt32());

        // Pressing: the request carries only the button id.
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync($"/api/deck/buttons/{buttons[1].GetProperty("id").GetString()}/press", null)).StatusCode);
        Assert.Equal(["keys Ctrl+S"], _platform.Calls);
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync($"/api/deck/buttons/{buttons[0].GetProperty("id").GetString()}/press", null)).StatusCode);
        await Next(ws, "state", s => s.GetProperty("audio").GetProperty("micMuted").GetBoolean()); // button state reaches every client

        // Invalid input is refused with an explanation, not stored.
        var bad = await _http.PutAsJsonAsync($"/api/deck/profiles/{profileId}", new { pages = new[] { new { buttons = new[] { new { slot = 0, action = new { type = "launch", target = "cmd.exe /c calc" } } } } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/deck/import", new { format = "nope", version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/deck/images/..%5Cconfig.json")).StatusCode);

        // Hardware: nothing is pushed until a client asks, then readings arrive on their own.
        await Send(ws, new { type = "hardware", on = true });
        var hw1 = await Next(ws, "hardware", _ => true);
        var hw2 = await Next(ws, "hardware", h => h.GetProperty("takenUtc").GetString() != hw1.GetProperty("takenUtc").GetString());
        Assert.True(hw2.GetProperty("memory").GetProperty("totalMb").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Null, hw2.GetProperty("cpu").GetProperty("temperatureC").ValueKind);
        await Send(ws, new { type = "hardware", on = false });
    }

    private static Task Send(ClientWebSocket ws, object message) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> Next(ClientWebSocket ws, string type, Func<JsonElement, bool> match)
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
            if (doc.RootElement.GetProperty("type").GetString() != type) continue;
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

/// <summary>
/// Real windows: a copy of PowerShell under a unique name opens a small window, so only that program's
/// windows are minimized, restored and closed, never anything the user has open.
/// </summary>
public class WindowActionTests
{
    [Fact]
    public async Task Minimize_restore_and_close_reach_only_the_named_programs_window()
    {
        var dir = TestAudio.TempDir();
        var name = "gdwin" + Guid.NewGuid().ToString("N")[..8];
        var exe = Path.Combine(dir, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), exe);
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe,
            "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; $f = New-Object Windows.Forms.Form; $f.Text = 'GameDeck window test'; [void]$f.ShowDialog()\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var platform = new WindowsDeckPlatform();
            var program = name + ".exe";
            for (var i = 0; i < 100 && !platform.OpenWindows().Any(w => w.Program == program); i++) await Task.Delay(100);
            Assert.Contains(platform.OpenWindows(), w => w.Program == program && w.Title == "GameDeck window test");
            p.Refresh();
            var hwnd = p.MainWindowHandle;

            Assert.Equal(1, platform.Window(program, "minimize"));
            await Task.Delay(300);
            Assert.True(IsIconic(hwnd));

            Assert.Equal(1, platform.Window(program, "restore"));
            await Task.Delay(300);
            Assert.False(IsIconic(hwnd));

            Assert.Equal(0, platform.Window("gd-no-such-program.exe", "close"));
            Assert.Equal(1, platform.Window(program, "close"));
            Assert.True(p.WaitForExit(5000), "the program should close like after clicking X");
            Assert.DoesNotContain(platform.OpenWindows(), w => w.Program == "gamedeck.exe" || w.Program == "testhost.exe");
        }
        finally
        {
            if (!p.HasExited) p.Kill();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
}
