using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDeck.App;
using GameDeck.App.Web;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Localization;
using GameDeck.Core.Security;
using Xunit;

namespace GameDeck.Tests;

/// <summary>PC-side text (API errors, routing advice, tray and Windows dialogs).</summary>
[Collection("Localization")] // L.Language is process-wide
public class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(:[^}]*)?\}");

    public static TheoryData<string> Translations => new(L.Supported.Where(l => l != L.English));

    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_language_has_exactly_the_same_keys_as_english(string language)
    {
        var table = L.Table(language);
        var missing = L.En.Keys.Except(table.Keys).ToList();
        var extra = table.Keys.Except(L.En.Keys).ToList();
        Assert.True(missing.Count == 0, $"Missing in {language}: " + string.Join(", ", missing));
        Assert.True(extra.Count == 0, $"Extra in {language}: " + string.Join(", ", extra));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Placeholders_match_between_languages(string language)
    {
        var table = L.Table(language);
        foreach (var (key, en) in L.En)
        {
            var a = Placeholder.Matches(en).Select(m => m.Groups[0].Value).Distinct().Order();
            var b = Placeholder.Matches(table[key]).Select(m => m.Groups[0].Value).Distinct().Order();
            Assert.True(a.SequenceEqual(b), $"{language} {key}: en {string.Join(",", a)} vs {string.Join(",", b)}");
            Assert.False(string.IsNullOrWhiteSpace(table[key]), $"{language} {key}");
        }
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_language_is_actually_translated(string language)
    {
        // A few strings are product names or formats with only placeholders.
        var same = L.En.Where(p => L.Table(language)[p.Key] == p.Value).Select(p => p.Key).ToList();
        Assert.True(same.Count < 12, $"{language} keeps English for: " + string.Join(", ", same));
    }

    [Fact]
    public void No_empty_or_untranslated_romanian_text()
    {
        // Strings that are legitimately identical (product names, formats with only placeholders).
        var sameAllowed = new HashSet<string> { "tray.tooltip.idle", "deck.newProfile" }; // "Deck" is the feature name
        foreach (var (key, ro) in L.Ro)
        {
            Assert.False(string.IsNullOrWhiteSpace(ro), key);
            if (!sameAllowed.Contains(key)) Assert.NotEqual(L.En[key], ro);
        }
    }

    [Fact]
    public void Romanian_uses_correct_diacritics()
    {
        var all = string.Concat(L.Ro.Values);
        foreach (var ch in "ăâîșț") Assert.Contains(ch, all);
        // Comma-below ș/ț, never the legacy cedilla forms.
        Assert.DoesNotContain('ş', all);
        Assert.DoesNotContain('ţ', all);
        Assert.DoesNotContain('Ş', all);
        Assert.DoesNotContain('Ţ', all);
        // Romanian quotes „…”, not English “…”, around names.
        Assert.DoesNotContain(L.Ro.Values, v => v.Contains('“'));
    }

    [Fact]
    public void Interpolation_keeps_device_names_and_numbers_untouched()
    {
        var ro = L.TIn(L.Romanian, "engine.feedback", "Microphone (USB Mic)", "CABLE Input (VB-Audio Virtual Cable)");
        Assert.Contains("„Microphone (USB Mic)”", ro);
        Assert.Contains("„CABLE Input (VB-Audio Virtual Cable)”", ro);
        Assert.Equal("Prea multe încercări. Încearcă din nou peste 42 s.", L.TIn(L.Romanian, "api.pairLocked", 42));
        Assert.Equal("Single use · new code in 4:07", L.TIn(L.English, "pair.expiry", 4, 7));
    }

    [Fact]
    public void Regional_codes_map_to_their_language_and_anything_else_to_english()
    {
        Assert.Equal(L.German, L.Normalize("de-DE"));
        Assert.Equal(L.Portuguese, L.Normalize("pt-BR"));
        Assert.Equal(L.Romanian, L.Normalize("RO"));
        Assert.Equal(L.English, L.Normalize("ja-JP"));
        Assert.Equal(L.English, L.Normalize(null));
        Assert.Equal("no.such.key", L.TIn(L.English, "no.such.key"));
        Assert.Equal(L.En["api.pairWrong"], L.TIn("xx", "api.pairWrong"));
    }

    [Fact]
    public void New_installs_default_to_english_and_the_choice_persists()
    {
        var dir = TestAudio.TempDir();
        Assert.Equal("en", new ConfigStore(dir).Read(c => c.Ui.Language));

        var store = new ConfigStore(dir);
        store.Update(c => c.Ui.Language = "pl");
        Assert.Equal("pl", new ConfigStore(dir).Read(c => c.Ui.Language)); // survives a restart

        File.WriteAllText(Path.Combine(dir, "config.json"), File.ReadAllText(Path.Combine(dir, "config.json")).Replace("\"pl\"", "\"xx\""));
        Assert.Equal("en", new ConfigStore(dir).Read(c => c.Ui.Language)); // garbage → default
    }

    [Fact]
    public async Task Api_errors_follow_the_selected_language_and_switching_is_immediate()
    {
        var store = new ConfigStore(TestAudio.TempDir());
        var port = FreePort();
        store.Update(c => { c.Server.Port = port; c.Audio.MonitorEnabled = false; });
        using var engine = await Task.Run(() => new AudioEngine());
        var svc = new GameDeckService(store, engine, new PairingService());
        L.Language = store.Read(c => c.Ui.Language);
        var app = DashboardServer.Build(svc, IPAddress.Loopback);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            async Task<string> WrongCodeMessage() =>
                JsonDocument.Parse(await (await http.PostAsJsonAsync("/api/pair", new { code = "000000x", deviceName = "t" })).Content.ReadAsStringAsync())
                    .RootElement.GetProperty("error").GetString()!;

            Assert.Equal(L.En["api.pairWrong"], await WrongCodeMessage());

            var changed = 0;
            svc.LanguageChanged += () => changed++;
            svc.SetLanguage("de-DE");
            Assert.Equal(L.De["api.pairWrong"], await WrongCodeMessage());
            Assert.Equal("de", svc.BuildState().Ui.Language);
            Assert.Equal(1, changed);

            Assert.Throws<ApiException>(() => svc.SetLanguage("klingon"));
            svc.SetLanguage("ro");
            Assert.Equal(L.Ro["api.pairWrong"], await WrongCodeMessage());
        }
        finally
        {
            await app.StopAsync();
            L.Language = L.English;
        }
    }

    [Fact]
    public void Every_key_used_in_code_exists()
    {
        var root = FindRepoRoot();
        var used = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"L\.T\(""([a-zA-Z0-9.]+)""").Select(m => m.Groups[1].Value))
            .Distinct()
            .Where(k => !k.EndsWith('.')) // dynamic prefixes like "route.state." + state; covered below
            .ToList();
        Assert.NotEmpty(used);
        foreach (var state in new[] { "Active", "Disabled", "Unplugged", "NotPresent" }) Assert.True(L.En.ContainsKey("route.state." + state));
        var missing = used.Where(k => !L.En.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Keys used but not defined: " + string.Join(", ", missing));
    }

    [Fact]
    public void Source_has_no_mojibake()
    {
        // UTF-8 text decoded as Windows-1252 and saved again: "→" becomes "â†’", "✓" becomes "âœ“", "ă" becomes "Äƒ", "ș" becomes "È™".
        var root = FindRepoRoot();
        var broken = new[] { "src", "web/src" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".ts") || f.EndsWith(".tsx"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "â€|âœ|â†|Äƒ|Ä‚|Ã¢|Ã®|ÃŽ|È™|È›|È˜|Èš"))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.True(broken.Count == 0, "Garbled characters in: " + string.Join(", ", broken));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameDeck.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
