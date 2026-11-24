using GameDeck.Core.Config;
using GameDeck.Core.Localization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameDeck.Core.Diagnostics;
using GameDeck.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameDeck.App.Web;

/// <summary>Kestrel host for the dashboard: static SPA + JSON API + WebSocket, LAN only.</summary>
public static class DashboardServer
{
    public const string SessionCookie = "gamedeck_session";
    private const string DeviceItem = "gamedeck.device";

    /// <summary>Endpoints reachable before pairing.</summary>
    private static readonly string[] PublicApi = ["/api/session", "/api/pair"];

    /// <param name="bindAddress">Defaults to all IPv4 interfaces; tests pass loopback.</param>
    public static WebApplication Build(GameDeckService service, IPAddress? bindAddress = null)
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = webRoot,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();

        builder.WebHost.ConfigureKestrel(k =>
        {
            // All IPv4 interfaces so phones on the Wi-Fi can connect; the guard below rejects
            // anything that isn't a private/LAN address. No public exposure, no port forwarding.
            k.Listen(bindAddress ?? IPAddress.Any, service.Port);
            k.Limits.MaxRequestBodySize = 210 * 1024 * 1024;
            k.AddServerHeader = false;
        });
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 210 * 1024 * 1024);
        builder.Services.ConfigureHttpJsonOptions(o => ConfigureJson(o.SerializerOptions));

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ConfigureJson(json);
        var youtube = new YouTubeBridge(() => service.Language);
        service.Presence.UseYouTube(youtube);
        var hub = new WsHub(service, json, youtube);

        var app = builder.Build();

        app.Use(ErrorsAsJson);
        app.Use(GuardNetwork);
        app.Use(SecurityHeaders);
        app.Use(CheckOrigin);
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Use((ctx, next) => Authenticate(ctx, next, service));
        app.UseDefaultFiles();
        // Built assets have the content hash in their name; the page naming them must always be current.
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = file => file.Context.Response.Headers.CacheControl =
                file.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
        });

        MapApi(app, service, hub, youtube);
        app.MapFallback(async ctx =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            var index = Path.Combine(webRoot, "index.html");
            if (ctx.Request.Path.StartsWithSegments("/api") || !File.Exists(index))
            {
                ctx.Response.StatusCode = 404;
                if (!File.Exists(index))
                    await ctx.Response.WriteAsync(L.T("api.notBuilt"));
                return;
            }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(index);
        });

        return app;
    }

    private static void ConfigureJson(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.Converters.Add(new JsonStringEnumConverter());
    }

    private static void MapApi(WebApplication app, GameDeckService svc, WsHub hub, YouTubeBridge youtube)
    {
        // --- session / pairing ---
        app.MapGet("/api/session", (HttpContext ctx) =>
        {
            var device = svc.Authenticate(ctx.Request.Cookies[SessionCookie]);
            return new SessionDto(device is not null, device?.Id, device?.Name, IsLocal(ctx));
        });

        app.MapPost("/api/pair", (PairRequest body, HttpContext ctx) =>
        {
            var (attempt, device) = svc.Pair(body.Code, body.DeviceName);
            switch (attempt.Outcome)
            {
                case PairOutcome.Success:
                    ctx.Response.Cookies.Append(SessionCookie, attempt.Token!, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict,
                        Secure = false, // plain HTTP on the LAN; see docs/SECURITY.md
                        IsEssential = true,
                        Path = "/",
                        Expires = DateTimeOffset.UtcNow.AddYears(1),
                    });
                    return Results.Ok(new SessionDto(true, device!.Id, device.Name, IsLocal(ctx)));
                case PairOutcome.LockedOut:
                    ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(attempt.RetryAfter.TotalSeconds)).ToString();
                    return Results.Json(new { error = L.T("api.pairLocked", Math.Ceiling(attempt.RetryAfter.TotalSeconds)) }, statusCode: 429);
                default:
                    return Results.Json(new { error = L.T("api.pairWrong") }, statusCode: 401);
            }
        });

        app.MapPost("/api/logout", (HttpContext ctx) =>
        {
            svc.Revoke(CurrentDevice(ctx));
            ctx.Response.Cookies.Delete(SessionCookie);
            return Results.NoContent();
        });

        // --- state ---
        app.MapGet("/api/state", () => svc.BuildState());
        app.MapPost("/api/devices/refresh", () => { svc.RefreshDevices(); return svc.BuildState(); });
        app.MapPut("/api/audio", (AudioPatch patch) => { svc.UpdateAudio(patch); return Results.NoContent(); });
        app.MapPost("/api/audio/route-test", () => svc.RunRouteTestAsync());

        // --- interface ---
        app.MapPut("/api/ui", (UiPatch body) => { svc.SetLanguage(body.Language); return Results.NoContent(); });
        app.MapPut("/api/ui/logo", (LogoPatch body) => { svc.SetLogo(body.Model, body.Finish); return Results.NoContent(); });
        app.MapGet("/api/autostart", (HttpContext ctx) => new AutoStartDto(svc.AutoStartEnabled(IsLocal(ctx))));
        app.MapPut("/api/autostart", (AutoStartDto body, HttpContext ctx) =>
        {
            svc.SetAutoStart(body.Enabled, IsLocal(ctx));
            return new AutoStartDto(svc.AutoStartEnabled(IsLocal(ctx)));
        });

        // --- playback (HTTP fallback; the dashboard normally uses the socket) ---
        app.MapPost("/api/sounds/{id}/trigger", async (string id) => new { result = (await svc.TriggerAsync(id)).ToString() });
        app.MapPost("/api/sounds/{id}/stop", (string id) => { svc.Stop(id); return Results.NoContent(); });
        app.MapPost("/api/stop-all", () => { svc.StopAll(); return Results.NoContent(); });

        // --- sounds ---
        app.MapPost("/api/sounds", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType) throw new ApiException(400, L.T("api.expectedForm"));
            var form = await req.ReadFormAsync();
            if (form.Files.Count == 0) throw new ApiException(400, L.T("api.noFiles"));
            var profileId = form["profileId"].FirstOrDefault();

            var created = new List<SoundDto>();
            var errors = new List<string>();
            foreach (var file in form.Files)
            {
                try
                {
                    await using var stream = file.OpenReadStream();
                    created.Add(await svc.ImportAsync(stream, file.FileName, file.Length, profileId));
                }
                catch (ApiException ex)
                {
                    errors.Add(ex.Message);
                }
            }
            return Results.Json(new { created, errors }, statusCode: created.Count > 0 ? 200 : 400);
        });
        app.MapPatch("/api/sounds/{id}", (string id, SoundPatch patch) => svc.UpdateSound(id, patch));
        app.MapGet("/api/sounds/{id}/audio", (string id) =>
        {
            var (path, type) = svc.SoundFile(id);
            return Results.File(path, type, enableRangeProcessing: true);
        });
        app.MapDelete("/api/sounds/{id}", (string id) => { svc.DeleteSound(id); return Results.NoContent(); });

        // --- profiles ---
        app.MapPost("/api/profiles", (ProfileCreate body) => svc.CreateProfile(body.Name));
        app.MapPatch("/api/profiles/{id}", (string id, ProfilePatch patch) => svc.UpdateProfile(id, patch));
        app.MapPost("/api/profiles/{id}/sounds", (string id, LayoutAdd body) => { svc.AddToProfile(id, body.SoundId); return Results.NoContent(); });
        app.MapDelete("/api/profiles/{id}", (string id) => { svc.DeleteProfile(id); return Results.NoContent(); });
        app.MapPost("/api/profiles/{id}/activate", (string id) => { svc.ActivateProfile(id); return Results.NoContent(); });

        // --- applications (Windows volume mixer) ---
        app.MapPost("/api/apps/refresh", () => svc.RefreshApps());
        app.MapPut("/api/apps/{key}", (string key, AppVolumePatch patch) => { svc.SetAppVolume(key, patch); return Results.NoContent(); });

        // --- Smart Audio Scenes ---
        app.MapPost("/api/scenes", (SceneCreate body) => svc.CreateScene(body));
        app.MapPatch("/api/scenes/{id}", (string id, ScenePatch patch) => svc.UpdateScene(id, patch));
        app.MapPost("/api/scenes/{id}/capture", (string id) => svc.CaptureScene(id));
        app.MapPost("/api/scenes/{id}/duplicate", (string id) => svc.DuplicateScene(id));
        app.MapPost("/api/scenes/{id}/apply", (string id) => svc.ApplyScene(id, "dashboard"));
        app.MapDelete("/api/scenes/{id}", (string id) => { svc.DeleteScene(id); return Results.NoContent(); });
        app.MapPut("/api/scenes/options", (SceneOptionsPatch patch) => { svc.SetSceneOptions(patch); return Results.NoContent(); });
        app.MapPost("/api/scenes/hotkeys/suspend", (HotkeySuspend body) => { svc.SuspendHotkeys(body.Seconds); return Results.NoContent(); });

        // --- Smart Deck ---
        // Pressing only names a stored button. Adding or changing launch, link and key actions needs IsLocal.
        app.MapPost("/api/deck/buttons/{id}/press", (string id, bool? hold) => svc.PressButtonAsync(id, hold == true));
        app.MapPost("/api/deck/profiles", (DeckProfileCreate body) => svc.CreateDeckProfile(body));
        app.MapPut("/api/deck/profiles/{id}", (string id, DeckProfile body, HttpContext ctx) => svc.SaveDeckProfile(id, body, IsLocal(ctx)));
        app.MapDelete("/api/deck/profiles/{id}", (string id) => { svc.DeleteDeckProfile(id); return Results.NoContent(); });
        app.MapPost("/api/deck/profiles/{id}/activate", (string id) => { svc.ActivateDeckProfile(id); return Results.NoContent(); });
        app.MapGet("/api/deck/profiles/{id}/export", (string id) => svc.ExportDeckProfile(id));
        app.MapPost("/api/deck/import", (DeckExport body, HttpContext ctx) => svc.ImportDeck(body, IsLocal(ctx)));
        app.MapPost("/api/deck/macros", (DeckMacro body, HttpContext ctx) => svc.SaveMacro(null, body, IsLocal(ctx)));
        app.MapPut("/api/deck/macros/{id}", (string id, DeckMacro body, HttpContext ctx) => svc.SaveMacro(id, body, IsLocal(ctx)));
        app.MapDelete("/api/deck/macros/{id}", (string id) => { svc.DeleteMacro(id); return Results.NoContent(); });
        app.MapPost("/api/deck/macros/{id}/run", (string id) => { svc.RunMacro(id); return Results.NoContent(); });
        app.MapPost("/api/deck/macros/{id}/stop", (string id) => { svc.StopMacro(id); return Results.NoContent(); });
        app.MapGet("/api/deck/windows", (HttpContext ctx) => svc.OpenWindows(IsLocal(ctx)));
        app.MapPost("/api/deck/program-of", async (DeckIconRequest body, HttpContext ctx) => new { program = await svc.ProgramOfAsync(body.Path, IsLocal(ctx)) });
        app.MapPost("/api/deck/browse", async (DeckBrowse body, HttpContext ctx) => new { path = await svc.BrowseForPathAsync(body.Folder, body.Current, IsLocal(ctx)) });
        app.MapPost("/api/deck/icon", async (DeckIconRequest body, HttpContext ctx) => new { name = await svc.SaveProgramIconAsync(body.Path, IsLocal(ctx)) });
        app.MapPost("/api/deck/images", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType) throw new ApiException(400, L.T("api.expectedForm"));
            var form = await req.ReadFormAsync();
            if (form.Files.Count != 1) throw new ApiException(400, L.T("api.noFiles"));
            await using var stream = form.Files[0].OpenReadStream();
            return await svc.SaveDeckImageAsync(stream, form.Files[0].Length);
        });
        app.MapGet("/api/deck/images/{name}", (string name, HttpContext ctx) =>
        {
            var (path, type) = svc.DeckImageFile(name);
            // Names are random and a file never changes, so browsers keep (and stay decoded) one copy.
            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(path, type);
        });

        // --- hardware monitor (live updates go over the socket) ---
        app.MapGet("/api/hardware", () => svc.SampleHardware());

        // --- paired devices ---
        app.MapDelete("/api/paired-devices/{id}", (string id) => { svc.Revoke(id); return Results.NoContent(); });

        // --- YouTube tab, through the browser extension ---
        app.MapGet("/api/updates", () => svc.Updates.Snapshot());
        app.MapPost("/api/updates/check", async () => { await svc.Updates.CheckAsync(manual: true); return svc.Updates.Snapshot(); });
        // Any paired device may start it: only the checked GitHub release is installed, and Windows still
        // asks for permission on the PC's own screen.
        app.MapPost("/api/updates/install", async () =>
        {
            await svc.Updates.InstallAsync();
            return Results.NoContent();
        });
        app.MapPost("/api/youtube", async (YouTubeAction body) => { await youtube.ApplyAsync(body); return Results.NoContent(); });
        app.MapGet("/api/youtube/extension", (HttpContext ctx) => IsLocal(ctx)
            ? Results.Ok(new { path = Path.Combine(AppContext.BaseDirectory, "extension") })
            : throw new ApiException(403, L.T("api.pcOnly")));
        // Not behind pairing: only our extension, in a browser on this PC, can open it.
        app.Map("/ext/youtube", async (HttpContext ctx) =>
        {
            var fromThisPc = ctx.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote);
            if (!ctx.WebSockets.IsWebSocketRequest || !fromThisPc || ctx.Request.Headers.Origin != YouTubeBridge.ExtensionOrigin)
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await youtube.HandleExtensionAsync(socket, ctx.RequestAborted);
        });

        // --- Discord Rich Presence (live updates go over the socket) ---
        app.MapGet("/api/presence", () => svc.Presence.Snapshot());
        app.MapPut("/api/presence/enabled", (PresenceEnabled body) => { svc.Presence.SetEnabled(body.Enabled); return svc.Presence.Snapshot(); });
        app.MapPost("/api/presence/presets", (GameDeck.Core.Presence.PresencePreset body) => svc.Presence.CreatePreset(body));
        app.MapPut("/api/presence/presets/{id}", (string id, GameDeck.Core.Presence.PresencePreset body) => svc.Presence.SavePreset(id, body));
        app.MapDelete("/api/presence/presets/{id}", (string id) => { svc.Presence.DeletePreset(id); return Results.NoContent(); });
        app.MapPost("/api/presence/presets/{id}/activate", (string id) => { svc.Presence.Activate(id); return svc.Presence.Snapshot(); });
        // Song details from a pasted link; only the five known music services are contacted.
        app.MapPost("/api/presence/lookup", (GameDeck.App.Presence.SongLookupRequest body, HttpContext ctx) => GameDeck.App.Presence.SongLookup.LookupAsync(body.Url, ctx.RequestAborted));
        app.MapPut("/api/presence/options", (GameDeck.App.Presence.PresenceOptionsPatch body) => { svc.Presence.SetOptions(body); return svc.Presence.Snapshot(); });
        app.MapPost("/api/presence/{slot}/restart", (string slot) => { svc.Presence.RestartTimer(slot); return svc.Presence.Snapshot(); });
        app.MapPost("/api/presence/{slot}/hide", (string slot) => { svc.Presence.Hide(slot); return svc.Presence.Snapshot(); });

        // --- realtime ---
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.HandleAsync(socket, CurrentDevice(ctx), ctx.RequestAborted);
        });
    }

    /// <summary>The browser runs on this PC: a loopback connection, or one from the PC's own LAN address.</summary>
    private static bool IsLocal(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } remote && (IPAddress.IsLoopback(remote) || remote.Equals(ctx.Connection.LocalIpAddress));

    private static string CurrentDevice(HttpContext ctx) =>
        ctx.Items[DeviceItem] as string ?? throw new ApiException(401, L.T("api.notPaired"));

    // ---------------- middleware ----------------

    private static async Task ErrorsAsJson(HttpContext ctx, RequestDelegate next)
    {
        try
        {
            await next(ctx);
        }
        catch (ApiException ex) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = ex.Status;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
        catch (BadHttpRequestException ex) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = ex.StatusCode;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && ex is not OperationCanceledException)
        {
            Log.Error($"{ctx.Request.Method} {ctx.Request.Path} failed", ex);
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { error = L.T("api.internal") });
        }
    }

    private static Task GuardNetwork(HttpContext ctx, RequestDelegate next)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (!NetworkGuard.IsLocalNetworkAddress(remote))
        {
            Log.Warn($"Rejected non-LAN client {remote}");
            ctx.Abort();
            return Task.CompletedTask;
        }
        if (!NetworkGuard.IsAllowedHost(ctx.Request.Host.Host))
        {
            ctx.Response.StatusCode = 421;
            return ctx.Response.WriteAsync(L.T("api.useIp"));
        }
        return next(ctx);
    }

    private static Task SecurityHeaders(HttpContext ctx, RequestDelegate next)
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; " +
            "connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
        return next(ctx);
    }

    /// <summary>CSRF defence in depth (on top of SameSite=Strict): state changes must come from our own origin.</summary>
    private static Task CheckOrigin(HttpContext ctx, RequestDelegate next)
    {
        var unsafeMethod = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method);
        var isSocket = ctx.Request.Path.StartsWithSegments("/ws");
        if (!unsafeMethod && !isSocket) return next(ctx);

        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)
                || !string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 403;
                return ctx.Response.WriteAsync(L.T("api.crossOrigin"));
            }
        }
        return next(ctx);
    }

    private static Task Authenticate(HttpContext ctx, RequestDelegate next, GameDeckService svc)
    {
        var path = ctx.Request.Path;
        var needsAuth = (path.StartsWithSegments("/api") && !PublicApi.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
                        || path.StartsWithSegments("/ws");
        if (!needsAuth) return next(ctx);

        var device = svc.Authenticate(ctx.Request.Cookies[SessionCookie]);
        if (device is null)
        {
            ctx.Response.StatusCode = 401;
            return ctx.Response.WriteAsJsonAsync(new { error = L.T("api.notPaired") });
        }
        ctx.Items[DeviceItem] = device.Id;
        return next(ctx);
    }
}
