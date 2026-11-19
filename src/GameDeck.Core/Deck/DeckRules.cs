using System.Text.RegularExpressions;
using GameDeck.Core.Config;
using GameDeck.Core.Localization;

namespace GameDeck.Core.Deck;

/// <summary>A deck configuration or action that is not allowed; the message is localized and safe to show.</summary>
public sealed class DeckException(string message) : Exception(message);

/// <summary>
/// The only gate between what a dashboard sends and what the PC stores and runs. Every profile,
/// macro and import goes through here, and actions are checked again when they run.
/// </summary>
public static partial class DeckRules
{
    public const int MaxRows = 8, MaxColumns = 10, MaxPages = 10, MaxProfiles = 20;
    public const int MaxMacros = 50, MaxSteps = 32, MaxWaitMs = 60_000;
    /// <summary>Still images (PNG, JPEG, single-frame WebP); animations (GIF, animated WebP or PNG) may be larger.</summary>
    public const int MaxStillBytes = 1024 * 1024, MaxAnimationBytes = 4 * 1024 * 1024;
    /// <summary>The largest image of any kind; anything bigger is refused before it is examined.</summary>
    public const int MaxImageBytes = MaxAnimationBytes;

    /// <summary>"loop" (always), "active" (only while the button's state is active), "off" (first frame only).</summary>
    public static readonly IReadOnlyList<string> AnimationModes = ["loop", "active", "off"];

    /// <summary>Media key name → virtual key.</summary>
    public static readonly IReadOnlyDictionary<string, ushort> MediaKeys = new Dictionary<string, ushort>
    {
        ["playPause"] = 0xB3, ["next"] = 0xB0, ["previous"] = 0xB1,
        ["volumeUp"] = 0xAF, ["volumeDown"] = 0xAE, ["mute"] = 0xAD,
    };

    public static readonly IReadOnlyList<string> Channels = ["mic", "virtual", "monitor"];

    public static readonly IReadOnlyList<string> Sensors =
        ["cpu.usage", "cpu.temp", "cpu.clock", "gpu.usage", "gpu.temp", "gpu.vram", "gpu.clock", "gpu.fan", "ram.usage", "ram.used"];

    /// <summary>Launching, opening links and pressing keys act outside GameDeck: only the PC itself may add or change them.</summary>
    public static readonly IReadOnlyList<string> SensitiveTypes = ["launch", "url", "keys", "window"];

    /// <summary>What a "window" action does to a program's windows.</summary>
    public static readonly IReadOnlyList<string> WindowModes = ["minimize", "restore", "close"];

    private static readonly string[] UrlSchemes = ["http", "https", "steam", "com.epicgames.launcher"];

    /// <summary>Script, installer and settings files: opening one would run arbitrary commands.</summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".msp",
        ".reg", ".scr", ".pif", ".com", ".cpl", ".jar", ".py", ".pyw", ".sh", ".inf", ".scf", ".msc", ".gadget", ".application", ".appref-ms",
    };

    [GeneratedRegex("^[a-f0-9]{12}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[a-f0-9]{12}\.(png|jpg|gif|webp)$")]
    private static partial Regex ImagePattern();

    public static bool IsId(string? id) => id is not null && IdPattern().IsMatch(id);

    public static bool IsImageName(string? name) => name is not null && ImagePattern().IsMatch(name);

    /// <summary>"png", "jpg", "gif" or "webp" for a complete, well-formed image; null for anything else, whatever its name says.</summary>
    public static string? ImageExtension(ReadOnlySpan<byte> data) => ImageCheck.Inspect(data)?.Extension;

    /// <summary>
    /// The image if it is well formed and within the size limit for its kind; otherwise a localized
    /// reason. Shared by uploads and imports so both accept exactly the same files.
    /// </summary>
    public static ImageInfo? ImageCheckOrNull(ReadOnlySpan<byte> data, out string? problemKey)
    {
        problemKey = null;
        if (data.Length > MaxImageBytes) { problemKey = "deck.imageTooLarge"; return null; }
        var info = ImageCheck.Inspect(data);
        if (info is null) { problemKey = "deck.imageInvalid"; return null; }
        if (data.Length > (info.Animated ? MaxAnimationBytes : MaxStillBytes)) { problemKey = "deck.imageTooLarge"; return null; }
        return info;
    }

    /// <summary>The localized message for a key from <see cref="ImageCheckOrNull"/>.</summary>
    public static string Explain(string problemKey) =>
        problemKey == "deck.imageTooLarge" ? L.T(problemKey, MaxStillBytes / 1024, MaxAnimationBytes / 1024) : L.T(problemKey);

    public static ImageInfo CheckImage(ReadOnlySpan<byte> data) =>
        ImageCheckOrNull(data, out var problem) ?? throw new DeckException(Explain(problem!));

    /// <summary>A program, file or folder on this PC: absolute, local, outside GameDeck's own data, and not a script.</summary>
    public static string CleanLaunchPath(string? path, string dataDirectory)
    {
        var p = (path ?? "").Trim().Trim('"').Trim();
        if (p.Length is 0 or > 400 || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw Bad("deck.launchPath");
        if (p.StartsWith(@"\\") || p.StartsWith("//")) throw Bad("deck.launchNetwork");
        if (!Path.IsPathFullyQualified(p) || p.IndexOf(':', 2) >= 0) throw Bad("deck.launchPath");
        string full;
        try { full = Path.GetFullPath(p); }
        catch { throw Bad("deck.launchPath"); }
        if (dataDirectory.Length > 0 && IsInside(full, dataDirectory)) throw Bad("deck.launchOwn");
        var ext = Path.GetExtension(full);
        if (BlockedExtensions.Contains(ext)) throw Bad("deck.launchBlocked", ext.ToLowerInvariant());
        return full;
    }

    public static string CleanUrl(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length is 0 or > 2048 || !Uri.TryCreate(u, UriKind.Absolute, out var uri) || !UrlSchemes.Contains(uri.Scheme.ToLowerInvariant()))
            throw Bad("deck.urlInvalid");
        if (uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase) && uri.Host.Length == 0) throw Bad("deck.urlInvalid");
        return u;
    }

    /// <summary>Validates one action and returns a copy holding only the fields its type uses.</summary>
    public static DeckAction CleanAction(DeckAction? a, bool inMacro, string dataDirectory)
    {
        a ??= new DeckAction();
        switch (a.Type)
        {
            case "none" when !inMacro:
            case "stopAll":
                return new DeckAction { Type = a.Type };
            case "launch":
                return new DeckAction { Type = "launch", Target = CleanLaunchPath(a.Target, dataDirectory) };
            case "url":
                return new DeckAction { Type = "url", Target = CleanUrl(a.Target) };
            case "keys":
                if (!KeyCombo.TryParse(a.Keys, out var combo, out var error)) throw Bad(error!);
                return new DeckAction { Type = "keys", Keys = combo!.ToString() };
            case "media":
                if (a.Mode is null || !MediaKeys.ContainsKey(a.Mode)) throw Bad("deck.actionInvalid");
                return new DeckAction { Type = "media", Mode = a.Mode };
            case "window":
                if (a.Mode is null || !WindowModes.Contains(a.Mode)) throw Bad("deck.actionInvalid");
                return new DeckAction { Type = "window", Target = CleanProgram(a.Target), Mode = a.Mode };
            case "sound":
            case "scene":
                return new DeckAction { Type = a.Type, Target = IsId(a.Target) ? a.Target : throw Bad("deck.targetMissing") };
            case "macro" when !inMacro:
                return new DeckAction { Type = "macro", Target = IsId(a.Target) ? a.Target : throw Bad("deck.targetMissing") };
            case "appMute":
                var app = (a.Target ?? "").Trim().ToLowerInvariant();
                if (app.Length is 0 or > 260) throw Bad("deck.targetMissing");
                return new DeckAction { Type = "appMute", Target = app, Mode = OnOffToggle(a.Mode) };
            case "mute":
                return new DeckAction { Type = "mute", Channel = Channel(a.Channel), Mode = OnOffToggle(a.Mode) };
            case "volume":
                var channel = Channel(a.Channel);
                var mode = a.Mode is "set" or "up" or "down" ? a.Mode : throw Bad("deck.actionInvalid");
                var value = a.Value is { } v && float.IsFinite(v) ? v : throw Bad("deck.actionInvalid");
                value = mode == "set" ? Math.Clamp(value, 0f, channel == "mic" ? 2f : 1.5f) : Math.Clamp(value, 0.01f, 0.5f);
                return new DeckAction { Type = "volume", Channel = channel, Mode = mode, Value = value };
            case "sensor" when !inMacro:
                if (a.Sensor is null || !Sensors.Contains(a.Sensor)) throw Bad("deck.sensorInvalid");
                // Mode "monitor": pressing opens the Hardware Monitor in the dashboard (nothing runs on the PC).
                return new DeckAction { Type = "sensor", Sensor = a.Sensor, Unit = string.IsNullOrWhiteSpace(a.Unit) ? null : Text(a.Unit, 8), Mode = a.Mode == "monitor" ? "monitor" : null };
            case "wait" when inMacro:
                return new DeckAction { Type = "wait", DelayMs = Math.Clamp(a.DelayMs ?? 500, 50, MaxWaitMs) };
            case "macro" or "sensor" or "none":
                throw Bad("deck.notInMacro");
            default:
                throw Bad("deck.actionInvalid");
        }
    }

    /// <summary>
    /// Validates a whole profile. Buttons keep their slot when it is free and inside the grid; otherwise
    /// they move to the first free slot, and a grid too small for its buttons is refused (nothing is dropped).
    /// </summary>
    public static DeckProfile CleanProfile(DeckProfile? p, string dataDirectory, Func<string, bool> imageExists)
    {
        p ??= new DeckProfile();
        var clean = new DeckProfile
        {
            Id = IsId(p.Id) ? p.Id : SoundButton.NewId(),
            Name = Text(p.Name, 40) is { Length: > 0 } n ? n : "Deck",
            Rows = Math.Clamp(p.Rows, 1, MaxRows),
            Columns = Math.Clamp(p.Columns, 1, MaxColumns),
        };
        var pages = p.Pages ?? [];
        if (pages.Count > MaxPages) throw Bad("deck.tooMany");
        var capacity = clean.Rows * clean.Columns;
        var ids = new HashSet<string>();
        string Unique(string? id) => IsId(id) && ids.Add(id!) ? id! : Fresh();
        string Fresh() { string id; do id = SoundButton.NewId(); while (!ids.Add(id)); return id; }

        foreach (var page in pages)
        {
            var buttons = (page?.Buttons ?? []).Where(b => b is not null).ToList();
            if (buttons.Count > capacity) throw Bad("deck.gridFull", buttons.Count, capacity);
            var cleanPage = new DeckPage { Id = Unique(page?.Id), Name = Text(page?.Name, 24) };
            var taken = new HashSet<int>();
            var homeless = new List<DeckButton>();
            foreach (var b in buttons)
            {
                var button = new DeckButton
                {
                    Id = Unique(b.Id),
                    Slot = b.Slot,
                    Label = Text(b.Label, 40),
                    Icon = b.Icon is { Length: > 0 and <= 40 } ? b.Icon : "zap",
                    Color = IsHexColor(b.Color) ? b.Color : "#a9d6ff",
                    Image = IsImageName(b.Image) && imageExists(b.Image!) ? b.Image : null,
                    Action = CleanAction(b.Action, inMacro: false, dataDirectory),
                    HoldAction = CleanSecondary(b.HoldAction, dataDirectory),
                    ToggleAction = CleanSecondary(b.ToggleAction, dataDirectory),
                    ActiveImage = IsImageName(b.ActiveImage) && imageExists(b.ActiveImage!) ? b.ActiveImage : null,
                    ActiveIcon = b.ActiveIcon is { Length: > 0 and <= 40 } ? b.ActiveIcon : null,
                    ActiveColor = IsHexColor(b.ActiveColor) ? b.ActiveColor : null,
                    Animation = b.Animation is not null && AnimationModes.Contains(b.Animation) ? b.Animation : "loop",
                    WarnAt = Threshold(b.WarnAt),
                    CriticalAt = Threshold(b.CriticalAt),
                };
                if (b.Slot >= 0 && b.Slot < capacity && taken.Add(b.Slot)) cleanPage.Buttons.Add(button);
                else homeless.Add(button);
            }
            foreach (var button in homeless)
            {
                button.Slot = Enumerable.Range(0, capacity).First(s => !taken.Contains(s));
                taken.Add(button.Slot);
                cleanPage.Buttons.Add(button);
            }
            cleanPage.Buttons.Sort((x, y) => x.Slot.CompareTo(y.Slot));
            clean.Pages.Add(cleanPage);
        }
        if (clean.Pages.Count == 0) clean.Pages.Add(new DeckPage { Id = Fresh() });
        return clean;
    }

    public static DeckMacro CleanMacro(DeckMacro? m, string dataDirectory)
    {
        m ??= new DeckMacro();
        var steps = m.Steps ?? [];
        if (steps.Count > MaxSteps) throw Bad("deck.macroTooLong", MaxSteps);
        return new DeckMacro
        {
            Id = IsId(m.Id) ? m.Id : SoundButton.NewId(),
            Name = Text(m.Name, 40) is { Length: > 0 } n ? n : "Macro",
            Steps = steps.Select(s => CleanAction(s, inMacro: true, dataDirectory)).ToList(),
        };
    }

    /// <summary>One entry per distinct launch, link and key action anywhere in the deck.</summary>
    public static HashSet<string> SensitiveSignatures(DeckConfig deck) =>
        deck.Profiles.SelectMany(p => p.Pages).SelectMany(p => p.Buttons).SelectMany(ActionsOf)
            .Concat(deck.Macros.SelectMany(m => m.Steps))
            .Where(a => SensitiveTypes.Contains(a.Type))
            .Select(a => $"{a.Type}|{(a.Type == "keys" ? a.Keys : a.Type == "window" ? a.Target + "|" + a.Mode : a.Type == "launch" ? a.Target?.ToLowerInvariant() : a.Target)}")
            .ToHashSet();

    /// <summary>
    /// Lenient pass for whatever is on disk (called on every load and save): anything invalid is
    /// neutralised instead of failing the whole config. Never throws.
    /// </summary>
    public static void Normalize(DeckConfig deck)
    {
        deck.Profiles = (deck.Profiles ?? []).Where(p => p is not null).Take(MaxProfiles).Select(p =>
        {
            p.Rows = Math.Clamp(p.Rows, 1, MaxRows);
            p.Columns = Math.Clamp(p.Columns, 1, MaxColumns);
            p.Pages = (p.Pages ?? []).Where(x => x is not null).Take(MaxPages).ToList();
            foreach (var page in p.Pages)
            {
                page.Buttons = (page.Buttons ?? []).Where(b => b is not null).Take(p.Rows * p.Columns).ToList();
                foreach (var b in page.Buttons)
                {
                    try { CleanAction(b.Action, false, ""); }
                    catch (DeckException) { b.Action = new DeckAction(); }
                    try { CleanSecondary(b.HoldAction, ""); }
                    catch (DeckException) { b.HoldAction = null; }
                    try { CleanSecondary(b.ToggleAction, ""); }
                    catch (DeckException) { b.ToggleAction = null; }
                }
            }
            return CleanProfile(p, "", _ => true);
        }).ToList();
        if (deck.Profiles.Count == 0) deck.Profiles.Add(CleanProfile(new DeckProfile { Name = L.T("deck.defaultProfile") }, "", _ => true));
        if (deck.Profiles.All(p => p.Id != deck.ActiveProfileId)) deck.ActiveProfileId = deck.Profiles[0].Id;

        // Only two-state buttons that still exist keep their state.
        var twoState = deck.Profiles.SelectMany(p => p.Pages).SelectMany(p => p.Buttons).Where(b => b.ToggleAction is not null).Select(b => b.Id).ToHashSet();
        deck.Toggled = (deck.Toggled ?? []).Where(twoState.Contains).Distinct().ToList();

        var macroIds = new HashSet<string>();
        deck.Macros = (deck.Macros ?? []).Where(m => m is not null).Take(MaxMacros).Select(m =>
        {
            m.Steps = (m.Steps ?? []).Take(MaxSteps).Where(s =>
            {
                try { CleanAction(s, true, ""); return true; }
                catch (DeckException) { return false; }
            }).ToList();
            return CleanMacro(m, "");
        }).Where(m => macroIds.Add(m.Id)).ToList();
    }

    public static bool IsInside(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return (Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static string Text(string? value, int max)
    {
        var s = new string((value ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }

    private static string Channel(string? c) => c is not null && Channels.Contains(c) ? c : throw Bad("deck.actionInvalid");

    private static string OnOffToggle(string? m) => m is "on" or "off" ? m : "toggle";

    /// <summary>
    /// A long-press or second-state action: the same rules as a button's own action, but it must do
    /// something (no "none") and can't be a read-only sensor. Missing or "none" means not set.
    /// </summary>
    public static DeckAction? CleanSecondary(DeckAction? a, string dataDirectory)
    {
        if (a is null || a.Type is null or "none") return null;
        if (a.Type == "sensor") throw Bad("deck.notInMacro");
        return CleanAction(a, inMacro: false, dataDirectory);
    }

    /// <summary>Every action a button can run: on press, on long press and in its second state.</summary>
    public static IEnumerable<DeckAction> ActionsOf(DeckButton b)
    {
        yield return b.Action;
        if (b.HoldAction is not null) yield return b.HoldAction;
        if (b.ToggleAction is not null) yield return b.ToggleAction;
    }

    /// <summary>An executable name like "obs64.exe": no folder, no other extension, never GameDeck itself.</summary>
    public static string CleanProgram(string? program)
    {
        var p = (program ?? "").Trim().Trim('"').ToLowerInvariant();
        if (p.Length is < 5 or > 100 || !p.EndsWith(".exe") || p.IndexOfAny(['\\', '/', ':']) >= 0 || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw Bad("deck.programInvalid");
        if (p == "gamedeck.exe") throw Bad("deck.programOwn");
        return p;
    }

    private static float? Threshold(float? v) => v is { } x && float.IsFinite(x) ? Math.Clamp(x, -1000f, 100_000f) : null;

    private static bool IsHexColor(string? v) => v is { Length: 7 } && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit);

    private static DeckException Bad(string key, params object?[] args) => new(L.T(key, args));
}
