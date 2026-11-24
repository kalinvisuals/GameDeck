using System.Text.Json.Nodes;

namespace GameDeck.Core.Presence;

/// <summary>
/// Cleaning of saved presets and their translation into Discord's activity object. Pure functions,
/// so the same input always gives the same activity.
/// </summary>
public static class PresenceRules
{
    public const int MaxPresets = 50;
    public const int MaxText = 128;
    public const int MaxLabel = 40;
    public const int MaxUrl = 512;
    public const int MaxButtons = 2;
    public const int MaxButtonLabel = 32;
    public const int MaxCountdownMinutes = 24 * 60;
    public const int MaxDurationSec = 24 * 3600;

    public static readonly string[] Kinds = ["activity", "music"];
    public static readonly string[] Types = ["playing", "listening", "watching", "competing"];
    public static readonly string[] Timers = ["none", "elapsed", "countdown", "track"];
    public static readonly string[] StatusDisplays = ["name", "details", "state"];

    public static void Normalize(PresenceConfig c)
    {
        c.Presets ??= new();
        c.Presets.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Id));
        c.Presets = c.Presets.DistinctBy(p => p.Id).Take(MaxPresets).ToList();
        foreach (var p in c.Presets) Clean(p);
        // Each slot holds a preset of its own kind.
        var active = c.Presets.FirstOrDefault(p => p.Id == c.ActivePresetId);
        if (active?.Kind == "music" && c.ActiveMusicPresetId is null) c.ActiveMusicPresetId = active.Id;
        if (active?.Kind != "activity") c.ActivePresetId = null;
        if (c.Presets.FirstOrDefault(p => p.Id == c.ActiveMusicPresetId)?.Kind != "music") c.ActiveMusicPresetId = null;

        c.AutoMusic ??= new AutoMusicSettings();
        c.AutoMusic.Apps = (c.AutoMusic.Apps ?? new()).Select(AppKey).Where(k => k.Length > 0).Distinct().Take(MaxApps).ToList();
        c.ActivityMode = OneOf(c.ActivityMode, Modes);
        c.MusicMode = OneOf(c.MusicMode, Modes);
    }

    public const string GameDeckYouTube = "gamedeck-youtube";
    public const int MaxApps = 30;
    /// <summary>"manual" (what the person chose) or "auto" (the detected game, the song playing). See <see cref="PresenceConfig"/>.</summary>
    public static readonly string[] Modes = ["manual", "auto"];

    /// <summary>A media program key: lower-case letters, digits, '.', '-' only.</summary>
    public static string AppKey(string? key)
    {
        var k = new string((key ?? "").Trim().ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray());
        return k.Length > 60 ? k[..60] : k;
    }

    /// <summary>"activity" or "music": the card (and Discord application) a preset is shown on.</summary>
    public static string SlotOf(PresencePreset p) => p.Kind == "music" ? "music" : "activity";

    public static void Clean(PresencePreset p)
    {
        p.Kind = OneOf(p.Kind, Kinds);
        p.Type = p.Kind == "music" ? "listening" : OneOf(p.Type, Types);
        p.Label = Text(p.Label, MaxLabel);
        p.Name = Text(p.Name, MaxText);
        p.Details = Text(p.Details, MaxText);
        p.State = Text(p.State, MaxText);
        p.LargeText = Text(p.LargeText, MaxText);
        p.SmallText = Text(p.SmallText, MaxText);
        p.DetailsUrl = Url(p.DetailsUrl);
        p.StateUrl = Url(p.StateUrl);
        p.LargeImage = Url(p.LargeImage);
        p.SmallImage = Url(p.SmallImage);
        p.Buttons = (p.Buttons ?? new())
            .Where(b => b is not null)
            .Select(b => new PresenceButton { Label = Text(b.Label, MaxButtonLabel), Url = Url(b.Url) })
            .Where(b => b.Label.Length > 0 && b.Url.Length > 0)
            .Take(MaxButtons)
            .ToList();
        p.Timer = OneOf(p.Timer, Timers, "elapsed");
        p.CountdownMinutes = Math.Clamp(p.CountdownMinutes, 1, MaxCountdownMinutes);
        p.DurationSec = Math.Clamp(p.DurationSec, 0, MaxDurationSec);
        p.PositionSec = Math.Clamp(p.PositionSec, 0, Math.Max(0, p.DurationSec - 1));
        if (p.Timer == "track" && p.DurationSec == 0) p.Timer = "elapsed";
        p.StatusDisplay = OneOf(p.StatusDisplay, StatusDisplays);
    }

    /// <summary>
    /// Discord's activity for <paramref name="p"/>, applied at <paramref name="appliedAt"/> and seen
    /// at <paramref name="now"/>. <c>NextChange</c> is when the activity has to be sent again (a
    /// song starting over, a countdown reaching zero); null when it never changes by itself.
    /// </summary>
    public static (JsonObject Activity, DateTimeOffset? NextChange) Build(PresencePreset p, DateTimeOffset appliedAt, DateTimeOffset now)
    {
        var a = new JsonObject
        {
            ["type"] = p.Type switch { "listening" => 2, "watching" => 3, "competing" => 5, _ => 0 },
            ["instance"] = false,
        };
        if (p.Name.Length > 0) a["name"] = p.Name;
        if (p.Details.Length > 0) a["details"] = MinTwo(p.Details);
        if (p.DetailsUrl.Length > 0 && p.Details.Length > 0) a["details_url"] = p.DetailsUrl;
        if (p.State.Length > 0) a["state"] = MinTwo(p.State);
        if (p.StateUrl.Length > 0 && p.State.Length > 0) a["state_url"] = p.StateUrl;
        a["status_display_type"] = p.StatusDisplay switch
        {
            "state" when p.State.Length > 0 => 1,
            "details" when p.Details.Length > 0 => 2,
            _ => 0,
        };

        var assets = new JsonObject();
        if (p.LargeImage.Length > 0)
        {
            assets["large_image"] = p.LargeImage;
            if (p.LargeText.Length > 0) assets["large_text"] = MinTwo(p.LargeText);
        }
        if (p.SmallImage.Length > 0)
        {
            assets["small_image"] = p.SmallImage;
            if (p.SmallText.Length > 0) assets["small_text"] = MinTwo(p.SmallText);
        }
        if (assets.Count > 0) a["assets"] = assets;

        if (p.Buttons.Count > 0)
            a["buttons"] = new JsonArray(p.Buttons.Select(b => (JsonNode)new JsonObject { ["label"] = b.Label, ["url"] = b.Url }).ToArray());

        DateTimeOffset? next = null;
        switch (p.Timer)
        {
            case "elapsed":
                a["timestamps"] = new JsonObject { ["start"] = appliedAt.ToUnixTimeMilliseconds() };
                break;
            case "countdown":
                var end = appliedAt.AddMinutes(p.CountdownMinutes);
                if (now < end)
                {
                    a["timestamps"] = new JsonObject { ["end"] = end.ToUnixTimeMilliseconds() };
                    next = end;
                }
                break;
            case "track":
                var length = TimeSpan.FromSeconds(p.DurationSec);
                var first = appliedAt - TimeSpan.FromSeconds(p.PositionSec);
                // The song repeats: the current round started a whole number of lengths after the first.
                var rounds = Math.Max(0, (long)Math.Floor((now - first) / length));
                var start = first + length * rounds;
                a["timestamps"] = new JsonObject
                {
                    ["start"] = start.ToUnixTimeMilliseconds(),
                    ["end"] = (start + length).ToUnixTimeMilliseconds(),
                };
                next = start + length;
                break;
        }
        return (a, next);
    }

    /// <summary>Discord refuses texts shorter than two characters; a braille blank keeps one-letter texts.</summary>
    private static string MinTwo(string value) => value.Length >= 2 ? value : value + "⠀";

    private static string OneOf(string? value, string[] allowed, string? fallback = null)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is not null && allowed.Contains(v) ? v : fallback ?? allowed[0];
    }

    internal static string Text(string? value, int max)
    {
        var clean = new string((value ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return clean.Length > max ? clean[..max].TrimEnd() : clean;
    }

    /// <summary>Absolute http(s) address up to <see cref="MaxUrl"/> characters, otherwise empty.</summary>
    internal static string Url(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0 || v.Length > MaxUrl) return "";
        return Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && u.Host.Length > 0
            ? v
            : "";
    }
}
