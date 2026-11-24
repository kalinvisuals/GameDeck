using System.Collections.Concurrent;
using System.Text.Json;
using GameDeck.App.Web;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Deck;
using GameDeck.Core.Diagnostics;
using GameDeck.Core.Hardware;
using GameDeck.Core.Localization;

namespace GameDeck.App;

// Smart Deck: button layouts, macros and the hardware monitor. Every action reuses what the rest of
// the service already does (soundboard, mixer, scenes); the deck adds no audio path of its own.
public sealed partial class GameDeckService
{
    private static readonly TimeSpan DoublePressWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan OrphanImageAge = TimeSpan.FromMinutes(10);
    private const int MaxImportImages = 64;

    private readonly ConcurrentDictionary<string, DateTime> _lastPress = new();

    public MacroRunner Macros { get; }

    /// <summary>Launching, links and keys. Tests swap it so nothing real is started or typed.</summary>
    public IDeckPlatform Platform { get; set; } = new WindowsDeckPlatform();

    /// <summary>Opens a file (false) or folder (true) picker on the PC's screen; set by the tray app, null without one.</summary>
    public Func<bool, string?, Task<string?>>? PathPicker { get; set; }

    private int _browsing;

    /// <summary>
    /// Lets the editor pick a program, file or folder instead of typing its path. The dialog appears on
    /// the PC itself, so it is only offered to a browser running there, and only one at a time.
    /// </summary>
    /// <returns>The chosen path, or null when the dialog was cancelled.</returns>
    public async Task<string?> BrowseForPathAsync(bool folder, string? current, bool local)
    {
        if (!local) throw new ApiException(403, L.T("deck.localOnly"));
        var picker = PathPicker ?? throw new ApiException(503, L.T("deck.browseUnavailable"));
        if (Interlocked.Exchange(ref _browsing, 1) == 1) throw new ApiException(409, L.T("deck.browseOpen"));
        try
        {
            return await picker(folder, current);
        }
        finally
        {
            Interlocked.Exchange(ref _browsing, 0);
        }
    }

    public string DeckImagesDirectory => Path.Combine(Store.DataDirectory, "deck-images");

    // ---------- Profiles ----------

    public DeckProfile CreateDeckProfile(DeckProfileCreate body) => Store.Update(c =>
    {
        if (c.Deck.Profiles.Count >= DeckRules.MaxProfiles) throw new ApiException(400, L.T("deck.tooMany"));
        DeckProfile p;
        if (body.CopyFrom is not null)
        {
            var source = FindDeckProfile(c, body.CopyFrom);
            p = WithNewIds(Clone(source));
            p.Name = DeckRules.Text(body.Name, 40) is { Length: > 0 } n ? n : CleanLabel(L.T("scene.copyName", source.Name));
        }
        else
        {
            p = new DeckProfile { Name = DeckRules.Text(body.Name, 40) is { Length: > 0 } n ? n : L.T("deck.newProfile", c.Deck.Profiles.Count + 1), Pages = [new DeckPage()] };
        }
        c.Deck.Profiles.Add(p);
        c.Deck.ActiveProfileId = p.Id;
        return Clone(p);
    });

    /// <summary>Replaces a profile with a validated copy of <paramref name="body"/> (grid, pages, buttons).</summary>
    /// <param name="local">The request comes from this PC; required to add or change launch, link and key actions.</param>
    public DeckProfile SaveDeckProfile(string id, DeckProfile body, bool local)
    {
        var clean = Guard(() => DeckRules.CleanProfile(body, Store.DataDirectory, DeckImageExists));
        clean.Id = id;
        var saved = Store.Update(c =>
        {
            var index = c.Deck.Profiles.FindIndex(p => p.Id == id);
            if (index < 0) throw new ApiException(404, L.T("deck.profileNotFound"));
            var before = DeckRules.SensitiveSignatures(c.Deck);
            var old = c.Deck.Profiles[index];
            c.Deck.Profiles[index] = clean;
            if (!local && !DeckRules.SensitiveSignatures(c.Deck).IsSubsetOf(before))
            {
                c.Deck.Profiles[index] = old;
                throw new ApiException(403, L.T("deck.localOnly"));
            }
            return Clone(clean);
        });
        RemoveOrphanImages();
        return saved;
    }

    public void DeleteDeckProfile(string id)
    {
        Store.Update(c =>
        {
            if (c.Deck.Profiles.Count <= 1) throw new ApiException(400, L.T("deck.lastProfile"));
            if (c.Deck.Profiles.RemoveAll(p => p.Id == id) == 0) throw new ApiException(404, L.T("deck.profileNotFound"));
        });
        RemoveOrphanImages();
    }

    public void ActivateDeckProfile(string id) => Store.Update(c =>
    {
        FindDeckProfile(c, id);
        c.Deck.ActiveProfileId = id;
    });

    public DeckExport ExportDeckProfile(string id)
    {
        var (profile, macros) = Store.Read(c =>
        {
            var p = Clone(FindDeckProfile(c, id));
            var used = p.Pages.SelectMany(x => x.Buttons).SelectMany(DeckRules.ActionsOf).Where(a => a.Type == "macro").Select(a => a.Target).ToHashSet();
            return (p, c.Deck.Macros.Where(m => used.Contains(m.Id)).Select(Clone).ToList());
        });
        var images = new Dictionary<string, string>();
        foreach (var name in profile.Pages.SelectMany(p => p.Buttons).SelectMany(ImagesOf).Distinct())
        {
            try { images[name] = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(DeckImagesDirectory, name))); }
            catch (IOException) { /* image file missing: the button exports without it */ }
        }
        return new DeckExport("gamedeck-deck", 1, profile, macros, images);
    }

    /// <summary>
    /// Adds an exported profile as a new one. The file is untrusted: everything goes through the same
    /// validation as a manual edit, images are checked by content, and all ids are regenerated.
    /// </summary>
    public DeckProfile ImportDeck(DeckExport? body, bool local)
    {
        if (body is not { Format: "gamedeck-deck", Version: 1, Profile: not null }) throw new ApiException(400, L.T("deck.importInvalid"));
        if ((body.Images?.Count ?? 0) > MaxImportImages || (body.Macros?.Count ?? 0) > DeckRules.MaxMacros) throw new ApiException(400, L.T("deck.importInvalid"));

        // Images: decode and verify in memory first; nothing is written unless the whole file is valid.
        var images = new Dictionary<string, (string Name, byte[] Bytes)>();
        foreach (var (name, base64) in body.Images ?? [])
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64 ?? ""); }
            catch (FormatException) { throw new ApiException(400, L.T("deck.importInvalid")); }
            if (!DeckRules.IsImageName(name)) throw new ApiException(400, L.T("deck.imageInvalid"));
            var info = Guard(() => DeckRules.CheckImage(bytes));
            images[name] = ($"{SoundButton.NewId()}.{info.Extension}", bytes);
        }

        // Old macro id (the one the file's buttons refer to) → the fresh id it gets here.
        var macroIds = new Dictionary<string, string>();
        var macros = Guard(() => (body.Macros ?? []).Select(m =>
        {
            var clean = DeckRules.CleanMacro(m, Store.DataDirectory);
            var fileId = clean.Id;
            clean.Id = SoundButton.NewId();
            macroIds[fileId] = clean.Id;
            return clean;
        }).ToList());

        foreach (var b in (body.Profile.Pages ?? []).SelectMany(p => p?.Buttons ?? []).Where(b => b is not null))
        {
            b.Image = b.Image is not null && images.TryGetValue(b.Image, out var image) ? image.Name : null;
            b.ActiveImage = b.ActiveImage is not null && images.TryGetValue(b.ActiveImage, out var active) ? active.Name : null;
            foreach (var a in new[] { b.Action, b.HoldAction, b.ToggleAction })
                if (a is { Type: "macro", Target: not null } && macroIds.TryGetValue(a.Target, out var macroId)) a.Target = macroId;
        }
        var profile = WithNewIds(Guard(() => DeckRules.CleanProfile(body.Profile, Store.DataDirectory, name => images.Values.Any(i => i.Name == name))));

        Directory.CreateDirectory(DeckImagesDirectory);
        foreach (var (name, bytes) in images.Values) File.WriteAllBytes(Path.Combine(DeckImagesDirectory, name), bytes);
        try
        {
            return Store.Update(c =>
            {
                if (c.Deck.Profiles.Count >= DeckRules.MaxProfiles || c.Deck.Macros.Count + macros.Count > DeckRules.MaxMacros) throw new ApiException(400, L.T("deck.tooMany"));
                var before = DeckRules.SensitiveSignatures(c.Deck);
                c.Deck.Profiles.Add(profile);
                c.Deck.Macros.AddRange(macros);
                if (!local && !DeckRules.SensitiveSignatures(c.Deck).IsSubsetOf(before))
                {
                    c.Deck.Profiles.Remove(profile);
                    c.Deck.Macros.RemoveAll(macros.Contains);
                    throw new ApiException(403, L.T("deck.localOnly"));
                }
                c.Deck.ActiveProfileId = profile.Id;
                Log.Info($"Deck profile '{profile.Name}' imported ({profile.Pages.Sum(p => p.Buttons.Count)} buttons, {macros.Count} macros)");
                return Clone(profile);
            });
        }
        finally
        {
            RemoveOrphanImages(everyAge: true);
        }
    }

    // ---------- Images ----------

    /// <summary>Stores an uploaded still image or animation after checking its content and size.</summary>
    public async Task<DeckImageUpload> SaveDeckImageAsync(Stream content, long length)
    {
        if (length > DeckRules.MaxImageBytes) throw new ApiException(413, DeckRules.Explain("deck.imageTooLarge"));
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > DeckRules.MaxImageBytes) throw new ApiException(413, DeckRules.Explain("deck.imageTooLarge"));
        }
        var bytes = memory.ToArray();
        // The content decides the type; the uploaded file name is never used.
        var info = DeckRules.ImageCheckOrNull(bytes, out var problem) ?? throw new ApiException(problem == "deck.imageTooLarge" ? 413 : 400, DeckRules.Explain(problem!));
        var name = $"{SoundButton.NewId()}.{info.Extension}";
        Directory.CreateDirectory(DeckImagesDirectory);
        await File.WriteAllBytesAsync(Path.Combine(DeckImagesDirectory, name), bytes);
        return new DeckImageUpload(name, info.Animated, info.Width, info.Height, info.Frames);
    }

    /// <summary>
    /// Saves the icon Windows shows for a program, shortcut, file or folder as a button image. Like
    /// launch buttons themselves, only for a browser on the PC.
    /// </summary>
    /// <returns>The image file name, to put on a button.</returns>
    public async Task<string> SaveProgramIconAsync(string? path, bool local)
    {
        if (!local) throw new ApiException(403, L.T("deck.localOnly"));
        var full = Guard(() => DeckRules.CleanLaunchPath(path, Store.DataDirectory));
        if (!File.Exists(full) && !Directory.Exists(full)) throw new ApiException(404, L.T("deck.launchMissing", full));
        var png = await Deck.ShellIcon.ExtractPngAsync(full) ?? throw new ApiException(422, L.T("deck.iconUnavailable"));
        if (png.Length > DeckRules.MaxStillBytes) throw new ApiException(422, L.T("deck.iconUnavailable"));
        var name = $"{SoundButton.NewId()}.png";
        Directory.CreateDirectory(DeckImagesDirectory);
        await File.WriteAllBytesAsync(Path.Combine(DeckImagesDirectory, name), png);
        return name;
    }

    public (string Path, string ContentType) DeckImageFile(string name)
    {
        if (!DeckImageExists(name)) throw new ApiException(404, L.T("deck.imageInvalid"));
        return (Path.Combine(DeckImagesDirectory, name), Path.GetExtension(name) switch { ".png" => "image/png", ".jpg" => "image/jpeg", ".gif" => "image/gif", _ => "image/webp" });
    }

    private bool DeckImageExists(string name) => DeckRules.IsImageName(name) && File.Exists(Path.Combine(DeckImagesDirectory, name));

    /// <summary>Deletes images no button uses. Fresh uploads are kept for a while: their button may not be saved yet.</summary>
    private void RemoveOrphanImages(bool everyAge = false)
    {
        try
        {
            if (!Directory.Exists(DeckImagesDirectory)) return;
            var used = Store.Read(c => c.Deck.Profiles.SelectMany(p => p.Pages).SelectMany(p => p.Buttons).SelectMany(ImagesOf).ToHashSet());
            foreach (var file in Directory.GetFiles(DeckImagesDirectory))
            {
                if (used.Contains(Path.GetFileName(file))) continue;
                if (everyAge || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > OrphanImageAge) TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Deck image cleanup failed", ex);
        }
    }

    /// <summary>Every image file a button shows, in any state.</summary>
    private static IEnumerable<string> ImagesOf(DeckButton b)
    {
        if (b.Image is not null) yield return b.Image;
        if (b.ActiveImage is not null) yield return b.ActiveImage;
    }

    // ---------- Macros ----------

    /// <param name="id">Null creates a macro.</param>
    public DeckMacro SaveMacro(string? id, DeckMacro body, bool local)
    {
        var clean = Guard(() => DeckRules.CleanMacro(body, Store.DataDirectory));
        clean.Id = id ?? SoundButton.NewId();
        return Store.Update(c =>
        {
            var index = id is null ? -1 : c.Deck.Macros.FindIndex(m => m.Id == id);
            if (id is not null && index < 0) throw new ApiException(404, L.T("deck.macroNotFound"));
            if (id is null && c.Deck.Macros.Count >= DeckRules.MaxMacros) throw new ApiException(400, L.T("deck.tooMany"));
            var before = DeckRules.SensitiveSignatures(c.Deck);
            var old = index < 0 ? null : c.Deck.Macros[index];
            if (index < 0) c.Deck.Macros.Add(clean);
            else c.Deck.Macros[index] = clean;
            if (!local && !DeckRules.SensitiveSignatures(c.Deck).IsSubsetOf(before))
            {
                if (old is null) c.Deck.Macros.Remove(clean);
                else c.Deck.Macros[index] = old;
                throw new ApiException(403, L.T("deck.localOnly"));
            }
            return Clone(clean);
        });
    }

    public void DeleteMacro(string id)
    {
        Macros.Stop(id);
        Store.Update(c =>
        {
            if (c.Deck.Macros.RemoveAll(m => m.Id == id) == 0) throw new ApiException(404, L.T("deck.macroNotFound"));
        });
    }

    public void RunMacro(string id)
    {
        var macro = Store.Read(c => c.Deck.Macros.FirstOrDefault(m => m.Id == id) is { } m ? Clone(m) : null) ?? throw new ApiException(404, L.T("deck.macroNotFound"));
        try { _ = Macros.Start(macro); }
        catch (DeckException ex) { throw new ApiException(409, ex.Message); }
    }

    public void StopMacro(string id) => Macros.Stop(id);

    // ---------- Running actions ----------

    /// <summary>
    /// Runs the action stored on the PC for this button. The client only names the button: it cannot
    /// supply an action, a path or keys, so a press can never do more than what was configured.
    /// </summary>
    /// <param name="hold">The button was held down: run its long-press action instead.</param>
    public async Task<DeckPressResult> PressButtonAsync(string buttonId, bool hold = false)
    {
        var (button, toggled) = Store.Read(c =>
            (c.Deck.Profiles.OrderBy(p => p.Id == c.Deck.ActiveProfileId ? 0 : 1)
                .SelectMany(p => p.Pages).SelectMany(p => p.Buttons).FirstOrDefault(b => b.Id == buttonId) is { } b ? Clone(b) : null,
             c.Deck.Toggled.Contains(buttonId)));
        if (button is null) throw new ApiException(404, L.T("deck.buttonNotFound"));

        // A two-state button runs its second action while in its second state; a long press has its own.
        var twoState = !hold && button.ToggleAction is not null;
        // When the second state means "the program is open" (or "visible"), Windows decides, not memory:
        // the program may have been closed with its X, by a long press or by another button.
        var followsProgram = false;
        if (twoState && RealSecondState(button, Platform.OpenWindows) is { } real)
        {
            toggled = real;
            followsProgram = true;
        }
        var action = hold ? button.HoldAction ?? throw new ApiException(409, L.T("deck.noHoldAction"))
            : twoState && toggled ? button.ToggleAction! : button.Action;

        var key = hold ? buttonId + ":hold" : buttonId;
        var now = DateTime.UtcNow;
        if (_lastPress.TryGetValue(key, out var last) && now - last < DoublePressWindow) return new DeckPressResult(true, Ignored: true);
        if (_lastPress.Count > 2000) _lastPress.Clear();
        _lastPress[key] = now;

        try
        {
            await ExecuteAsync(action, CancellationToken.None);
            // The state flips only when its action worked, so the button never shows a state it didn't reach.
            if (twoState) SetToggled(buttonId, !toggled);
            // A program takes a moment to open or close its window: look again so every screen catches up.
            if (followsProgram)
                foreach (var delay in new[] { 2000, 6000 })
                    _ = Task.Delay(delay).ContinueWith(_ => StateChanged?.Invoke(), TaskScheduler.Default);
            return new DeckPressResult(true);
        }
        catch (DeckException ex)
        {
            throw new ApiException(409, ex.Message);
        }
    }

    /// <summary>
    /// For a two-state button whose second action closes or minimizes a program, whether that program is
    /// in the state the second action would undo: open (for close) or open and not minimized (for minimize).
    /// Null for any other button, which keeps its remembered state.
    /// </summary>
    private static bool? RealSecondState(DeckButton b, Func<IReadOnlyList<OpenWindow>> open)
    {
        if (b.ToggleAction is not { Type: "window", Target: { } program, Mode: "close" or "minimize" } second) return null;
        var windows = open().Where(w => w.Program == program).ToList();
        return second.Mode == "close" ? windows.Count > 0 : windows.Any(w => !w.Minimized);
    }

    /// <summary>Two-state buttons in their second state, with program-following ones read from Windows.</summary>
    private IReadOnlyList<string> EffectiveToggled(AppConfig c, IReadOnlyList<OpenWindow>? open)
    {
        var toggled = c.Deck.Toggled.ToHashSet();
        if (open is null) return toggled.ToList();
        foreach (var b in c.Deck.Profiles.SelectMany(p => p.Pages).SelectMany(p => p.Buttons))
        {
            if (RealSecondState(b, () => open) is not { } real) continue;
            if (real) toggled.Add(b.Id);
            else toggled.Remove(b.Id);
        }
        return toggled.ToList();
    }

    /// <summary>Open windows, read only when some button follows a program (it costs a few milliseconds).</summary>
    private IReadOnlyList<OpenWindow>? WindowsForState()
    {
        var follows = Store.Read(c => c.Deck.Profiles.SelectMany(p => p.Pages).SelectMany(p => p.Buttons)
            .Any(b => b.ToggleAction is { Type: "window", Mode: "close" or "minimize" }));
        if (!follows) return null;
        try { return Platform.OpenWindows(); }
        catch (Exception ex)
        {
            Log.Warn("Deck: open windows could not be read", ex);
            return null;
        }
    }

    private void SetToggled(string buttonId, bool on) => Store.Update(c =>
    {
        c.Deck.Toggled.Remove(buttonId);
        if (on) c.Deck.Toggled.Add(buttonId);
    });

    /// <summary>One action, validated again at the moment it runs. Throws with a localized message on failure.</summary>
    internal async Task ExecuteAsync(DeckAction a, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            switch (a.Type)
            {
                case "launch":
                    var path = DeckRules.CleanLaunchPath(a.Target, Store.DataDirectory);
                    if (!File.Exists(path) && !Directory.Exists(path)) throw new DeckException(L.T("deck.launchMissing", path));
                    Platform.Launch(path);
                    Log.Info($"Deck: opened {path}");
                    break;
                case "url":
                    Platform.OpenUrl(DeckRules.CleanUrl(a.Target));
                    break;
                case "keys":
                    if (!KeyCombo.TryParse(a.Keys, out var combo, out var error)) throw new DeckException(L.T(error!));
                    Platform.SendKeys(combo!);
                    break;
                case "media":
                    if (a.Mode is null || !DeckRules.MediaKeys.ContainsKey(a.Mode)) throw new DeckException(L.T("deck.actionInvalid"));
                    Platform.Media(a.Mode);
                    break;
                case "window":
                    var program = DeckRules.CleanProgram(a.Target);
                    if (a.Mode is null || !DeckRules.WindowModes.Contains(a.Mode)) throw new DeckException(L.T("deck.actionInvalid"));
                    if (Platform.Window(program, a.Mode) == 0) throw new DeckException(L.T("deck.programNotOpen", program));
                    break;
                case "sound":
                    await TriggerAsync(a.Target ?? "");
                    break;
                case "stopAll":
                    StopAll();
                    break;
                case "mute":
                    var muted = Store.Read(c => a.Channel switch { "mic" => c.Audio.MicMuted, "virtual" => c.Audio.VirtualMuted, _ => c.Audio.MonitorMuted });
                    var next = a.Mode switch { "on" => true, "off" => false, _ => !muted };
                    UpdateAudio(a.Channel switch
                    {
                        "mic" => ChannelPatch(micMuted: next),
                        "virtual" => ChannelPatch(virtualMuted: next),
                        _ => ChannelPatch(monitorMuted: next),
                    });
                    break;
                case "volume":
                    var current = Store.Read(c => a.Channel switch { "mic" => c.Audio.MicVolume, "virtual" => c.Audio.VirtualVolume, _ => c.Audio.MonitorVolume });
                    var amount = a.Value ?? 0f;
                    var volume = Math.Clamp(a.Mode switch { "up" => current + amount, "down" => current - amount, _ => amount }, 0f, a.Channel == "mic" ? 2f : 1.5f);
                    UpdateAudio(a.Channel switch
                    {
                        "mic" => ChannelPatch(mic: volume),
                        "virtual" => ChannelPatch(virtualVolume: volume),
                        _ => ChannelPatch(monitor: volume),
                    });
                    break;
                case "scene":
                    ApplyScene(a.Target ?? "", "deck");
                    break;
                case "discord":
                    switch (a.Mode)
                    {
                        case "show": Presence.Activate(a.Target ?? ""); break;
                        case "hideActivity": Presence.Hide("activity"); break;
                        case "hideSong": Presence.Hide("music"); break;
                        case "toggle": Presence.Toggle(); break;
                        case "activityMode": Presence.ToggleMode("activity"); break;
                        case "musicMode": Presence.ToggleMode("music"); break;
                        default: throw new DeckException(L.T("deck.actionInvalid"));
                    }
                    break;
                case "appMute":
                    var app = RefreshApps().FirstOrDefault(x => x.Key == a.Target) ?? throw new DeckException(L.T("apps.notRunning"));
                    SetAppVolume(app.Key, new AppVolumePatch(null, a.Mode switch { "on" => true, "off" => false, _ => !app.Muted }));
                    break;
                case "macro":
                    var macro = Store.Read(c => c.Deck.Macros.FirstOrDefault(m => m.Id == a.Target) is { } m ? Clone(m) : null) ?? throw new DeckException(L.T("deck.macroNotFound"));
                    if (Macros.IsRunning(macro.Id)) Macros.Stop(macro.Id); // pressing a running macro's button stops it
                    else _ = Macros.Start(macro);
                    break;
                case "none" or "sensor" or "wait":
                    break;
                default:
                    throw new DeckException(L.T("deck.actionInvalid"));
            }
        }
        catch (ApiException ex)
        {
            throw new DeckException(ex.Message);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Deck action '{a.Type}' failed", ex);
            throw new DeckException(L.T("deck.actionFailed", ex.Message));
        }
    }

    private static AudioPatch ChannelPatch(float? monitor = null, float? virtualVolume = null, float? mic = null, bool? micMuted = null, bool? virtualMuted = null, bool? monitorMuted = null) =>
        new(null, null, monitor, null, virtualVolume, null, null, mic, null, micMuted, virtualMuted, monitorMuted);

    /// <summary>
    /// The program a launch path runs ("obs64.exe"), so the editor can point a minimize/close action at
    /// the same program. Reads shortcuts; never runs anything. Null when the path doesn't tell (a link,
    /// a document, a folder). Only for a browser on the PC, like launch buttons themselves.
    /// </summary>
    public async Task<string?> ProgramOfAsync(string? path, bool local)
    {
        if (!local) throw new ApiException(403, L.T("deck.localOnly"));
        var full = Guard(() => DeckRules.CleanLaunchPath(path, Store.DataDirectory));
        if (!File.Exists(full)) return null;
        var program = await Deck.ShellLink.ProgramOfAsync(full);
        return program is null || program == "gamedeck.exe" ? null : program;
    }

    /// <summary>Programs with a window open, for the editor's "window" action. Only shown on the PC itself.</summary>
    public IReadOnlyList<OpenWindow> OpenWindows(bool local) =>
        local ? Platform.OpenWindows() : throw new ApiException(403, L.T("deck.localOnly"));

    // ---------- Hardware ----------

    private readonly object _hardwareGate = new();
    private HardwareMonitor? _hardware;
    private HardwareSnapshot? _lastHardware;

    /// <summary>
    /// One shared reading for every client: a call within a second of the previous one returns the
    /// cached snapshot. The monitor is created on first use, so a PC that never opens it pays nothing.
    /// </summary>
    public HardwareSnapshot SampleHardware()
    {
        lock (_hardwareGate)
        {
            if (_lastHardware is { } cached && DateTime.UtcNow - cached.TakenUtc < TimeSpan.FromSeconds(1)) return cached;
            _hardware ??= new HardwareMonitor();
            return _lastHardware = _hardware.Sample();
        }
    }

    // ---------- helpers ----------

    private DeckStateDto BuildDeck(AppConfig c, IReadOnlyList<OpenWindow>? open) =>
        new(c.Deck.Profiles.Select(Clone).ToList(), c.Deck.ActiveProfileId!, c.Deck.Macros.Select(Clone).ToList(), Macros.Running, Macros.Errors, EffectiveToggled(c, open));

    private static DeckProfile FindDeckProfile(AppConfig c, string id) =>
        c.Deck.Profiles.FirstOrDefault(p => p.Id == id) ?? throw new ApiException(404, L.T("deck.profileNotFound"));

    private static DeckProfile WithNewIds(DeckProfile p)
    {
        p.Id = SoundButton.NewId();
        foreach (var page in p.Pages)
        {
            page.Id = SoundButton.NewId();
            foreach (var b in page.Buttons) b.Id = SoundButton.NewId();
        }
        return p;
    }

    /// <summary>Deep copy, so nothing handed out (or serialized later) aliases the live config.</summary>
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private static T Guard<T>(Func<T> validate)
    {
        try { return validate(); }
        catch (DeckException ex) { throw new ApiException(400, ex.Message); }
    }
}
