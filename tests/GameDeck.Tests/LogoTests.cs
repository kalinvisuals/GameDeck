using System.Drawing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GameDeck.App;
using GameDeck.App.Tray;
using GameDeck.App.Web;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Security;
using Xunit;

namespace GameDeck.Tests;

[Collection("Localization")]
public class LogoTests
{
    [Fact]
    public void Cobalt_large_key_is_the_default_and_the_choice_survives_a_restart()
    {
        var dir = TestAudio.TempDir();
        var store = new ConfigStore(dir);
        Assert.Equal(("bigkey", "cobalt"), store.Read(c => (c.Ui.LogoModel, c.Ui.LogoFinish)));

        store.Update(c => { c.Ui.LogoModel = "mono"; c.Ui.LogoFinish = "champagne"; });
        Assert.Equal(("mono", "champagne"), new ConfigStore(dir).Read(c => (c.Ui.LogoModel, c.Ui.LogoFinish)));
    }

    [Fact]
    public void The_choice_survives_an_older_version_rewriting_the_config()
    {
        var dir = TestAudio.TempDir();
        new ConfigStore(dir).Update(c => { c.Ui.LogoModel = "dynamic"; c.Ui.LogoFinish = "arctic"; });

        var path = Path.Combine(dir, "config.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["ui"] = new JsonObject { ["language"] = "ro" };
        File.WriteAllText(path, json.ToJsonString());

        Assert.Equal(("dynamic", "arctic"), new ConfigStore(dir).Read(c => (c.Ui.LogoModel, c.Ui.LogoFinish)));
    }

    [Fact]
    public void Unknown_values_in_the_config_fall_back_to_the_default()
    {
        var dir = TestAudio.TempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"), """{"ui":{"logoModel":"nope","logoFinish":"red"}}""");
        Assert.Equal(("bigkey", "cobalt"), new ConfigStore(dir).Read(c => (c.Ui.LogoModel, c.Ui.LogoFinish)));
    }

    [Fact]
    public void Every_design_and_finish_can_be_drawn_and_the_web_offers_the_same_ones()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "GameDeck.sln"))) root = root.Parent!;
        var web = File.ReadAllText(Path.Combine(root.FullName, "web", "src", "lib", "logo.ts"));

        foreach (var model in LogoCatalog.Models)
        {
            Assert.Contains($"{model}: {{ key:", web);
            foreach (var finish in LogoCatalog.Finishes)
            {
                Assert.True(LogoRenderer.Knows(model, finish), $"{model}/{finish}");
                using var bitmap = LogoRenderer.RenderMark(model, finish, 16, lightSurface: false);
                Assert.True(CoveredPixels(bitmap) > 40, $"{model}/{finish} is nearly empty");
            }
        }
        foreach (var finish in LogoCatalog.Finishes) Assert.Contains($"{finish}: {{ swatch:", web);
    }

    [Fact]
    public void The_mark_is_light_on_dark_surfaces_and_dark_on_light_ones()
    {
        using var onDark = LogoRenderer.RenderMark("bigkey", "cobalt", 64, lightSurface: false);
        using var onLight = LogoRenderer.RenderMark("bigkey", "cobalt", 64, lightSurface: true);
        Assert.True(onDark.GetPixel(10, 50).GetBrightness() > 0.8f);
        Assert.True(onLight.GetPixel(10, 50).GetBrightness() < 0.2f);
        Assert.Equal(0, onDark.GetPixel(0, 63).A); // transparent outside the shape
    }

    [Fact]
    public void A_rendered_logo_becomes_an_independent_icon()
    {
        using var bitmap = LogoRenderer.RenderAppIcon("bigkey", "cobalt", 32);
        using var icon = LogoRenderer.ToIcon(bitmap);
        using var copy = icon.ToBitmap();
        Assert.Equal(32, copy.Width);
        Assert.True(CoveredPixels(copy) > 500);
    }

    [Fact]
    public async Task A_paired_device_changes_the_logo_and_bad_values_are_refused()
    {
        var store = new ConfigStore(TestAudio.TempDir());
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        store.Update(c => { c.Server.Port = port; c.Audio.MonitorEnabled = false; });

        var engine = await Task.Run(() => new AudioEngine());
        var service = new GameDeckService(store, engine, new PairingService());
        await Task.Run(service.Start);
        var app = DashboardServer.Build(service, IPAddress.Loopback);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PutAsJsonAsync("/api/ui/logo", new { model = "mono", finish = "steel" })).StatusCode);

            var (code, _) = service.Pairing.GetCurrentCode();
            (await http.PostAsJsonAsync("/api/pair", new { code, deviceName = "Test" })).EnsureSuccessStatusCode();
            var changes = 0;
            service.LogoChanged += () => changes++;

            Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsJsonAsync("/api/ui/logo", new { model = "mono", finish = "steel" })).StatusCode);
            Assert.Equal("mono", service.BuildState().Ui.LogoModel);
            Assert.Equal("steel", service.BuildState().Ui.LogoFinish);
            Assert.Equal(1, changes);

            Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/ui/logo", new { model = "mono", finish = "red" })).StatusCode);
            Assert.Equal("steel", service.BuildState().Ui.LogoFinish);
        }
        finally
        {
            await app.StopAsync();
            await Task.Run(engine.Dispose);
        }
    }

    private static int CoveredPixels(Bitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).A > 128) count++;
        return count;
    }
}
