using GameDeck.Core.Localization;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GameDeck.Core.Diagnostics;

namespace GameDeck.App.Web;

/// <summary>
/// Real-time channel. Server pushes "state" and "playing"; clients send "play"/"stop"/"stopAll".
/// Button presses go over the socket rather than HTTP to skip per-request overhead.
/// </summary>
public sealed class WsHub
{
    private const int MaxMessageBytes = 16 * 1024;

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly GameDeckService _service;
    private readonly JsonSerializerOptions _json;
    private int _stateQueued;
    private int _youtubeQueued;
    private int _updatesQueued;

    private readonly YouTubeBridge _youtube;

    public WsHub(GameDeckService service, JsonSerializerOptions json, YouTubeBridge youtube)
    {
        _service = service;
        _json = json;
        _youtube = youtube;
        youtube.Changed += QueueYouTubeBroadcast;
        service.Updates.Changed += QueueUpdatesBroadcast;
        service.LanguageChanged += QueueUpdatesBroadcast; // announcements come in the chosen language
        service.StateChanged += QueueStateBroadcast;
        service.PlayingChanged += () => _ = BroadcastAsync(new { type = "playing", data = _service.Engine.GetPlaying() });
        service.DeviceRevoked += CloseDevice;
        service.PlayingChanged += WakeLevels;
        service.StateChanged += WakeLevels;
        _ = PushLevelsAsync();
        _ = PushHardwareAsync();
    }

    private readonly SemaphoreSlim _levelsWake = new(0, 1);

    private void WakeLevels()
    {
        try { if (_levelsWake.CurrentCount == 0) _levelsWake.Release(); }
        catch (SemaphoreFullException) { /* already awake */ }
    }

    /// <summary>
    /// ~15 Hz meter frames while anything is audible, then one zero frame so meters settle, then it
    /// sleeps (no timer) until playback starts, the mic opens, or a client connects.
    /// </summary>
    private async Task PushLevelsAsync()
    {
        var lastWasSilent = true;
        while (true)
        {
            try
            {
                if (_clients.IsEmpty || (_service.Engine.IsIdle && lastWasSilent))
                {
                    await _levelsWake.WaitAsync();
                    _service.Engine.TakeLevels(); // discard anything stale from before the sleep
                    continue;
                }
                await Task.Delay(66);
                var levels = _service.Engine.TakeLevels();
                if (levels.IsSilent && lastWasSilent) continue;
                lastWasSilent = levels.IsSilent;
                await BroadcastAsync(new { type = "levels", data = levels });
            }
            catch (Exception ex)
            {
                Log.Warn("Level push failed", ex);
                await Task.Delay(1000);
            }
        }
    }

    /// <summary>How often hardware readings are pushed while some client is watching.</summary>
    public static TimeSpan HardwareInterval { get; set; } = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _hardwareWake = new(0, 1);

    private void WakeHardware()
    {
        try { if (_hardwareWake.CurrentCount == 0) _hardwareWake.Release(); }
        catch (SemaphoreFullException) { /* already awake */ }
    }

    /// <summary>
    /// One sampling loop for all clients, and only while some client asked for hardware data (the
    /// monitor page or a sensor widget is on screen). With no watcher it sleeps without a timer.
    /// </summary>
    private async Task PushHardwareAsync()
    {
        while (true)
        {
            try
            {
                var watchers = _clients.Values.Where(c => c.WantsHardware).ToList();
                if (watchers.Count == 0)
                {
                    await _hardwareWake.WaitAsync();
                    continue;
                }
                var bytes = Serialize(new { type = "hardware", data = await Task.Run(_service.SampleHardware) });
                await Task.WhenAll(watchers.Select(c => c.SendAsync(bytes)));
                await Task.Delay(HardwareInterval);
            }
            catch (Exception ex)
            {
                Log.Warn("Hardware push failed", ex);
                await Task.Delay(5000);
            }
        }
    }

    public async Task HandleAsync(WebSocket socket, string deviceId, CancellationToken ct)
    {
        var client = new Client(socket, deviceId);
        _clients[client.Id] = client;
        WakeLevels();
        try
        {
            await client.SendAsync(Serialize(new { type = "state", data = _service.BuildState() }));
            await client.SendAsync(Serialize(new { type = "youtube", data = _youtube.Snapshot() }));
            await client.SendAsync(Serialize(new { type = "updates", data = _service.Updates.Snapshot() }));
            await ReceiveLoopAsync(client, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // client went away
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
            }
        }
    }

    private async Task ReceiveLoopAsync(Client client, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (client.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await client.Socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                await client.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, null, ct);
                return;
            }
            if (!result.EndOfMessage) continue;

            var payload = message.ToArray();
            message.SetLength(0);
            if (result.MessageType == WebSocketMessageType.Text) await HandleMessageAsync(client, payload);
        }
    }

    private async Task HandleMessageAsync(Client client, byte[] payload)
    {
        string? type = null, soundId = null, requestId = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            soundId = root.TryGetProperty("soundId", out var s) ? s.GetString() : null;
            requestId = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;

            switch (type)
            {
                case "play" when soundId is not null:
                    // Not awaited: a first-time decode must not delay a following "stopAll".
                    _ = PlayAsync(client, soundId, requestId);
                    break;
                case "stop" when soundId is not null:
                    _service.Stop(soundId);
                    break;
                case "stopAll":
                    _service.StopAll();
                    break;
                case "hardware":
                    client.WantsHardware = root.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
                    if (client.WantsHardware)
                    {
                        // The newest reading right away, then the shared loop takes over.
                        await client.SendAsync(Serialize(new { type = "hardware", data = await Task.Run(_service.SampleHardware) }));
                        WakeHardware();
                    }
                    break;
                case "ping":
                    await client.SendAsync(Serialize(new { type = "pong", requestId }));
                    break;
            }
        }
        catch (ApiException ex)
        {
            await client.SendAsync(Serialize(new { type = "error", requestId, message = ex.Message }));
        }
        catch (JsonException)
        {
            await client.SendAsync(Serialize(new { type = "error", message = L.T("ws.malformed") }));
        }
        catch (Exception ex) when (ex is not WebSocketException)
        {
            Log.Error($"WS message '{type}' failed", ex);
            await client.SendAsync(Serialize(new { type = "error", requestId, message = L.T("ws.internal") }));
        }
    }

    private async Task PlayAsync(Client client, string soundId, string? requestId)
    {
        try
        {
            var outcome = await _service.TriggerAsync(soundId);
            await client.SendAsync(Serialize(new { type = "ack", requestId, result = outcome.ToString() }));
        }
        catch (ApiException ex)
        {
            await client.SendAsync(Serialize(new { type = "error", requestId, message = ex.Message }));
        }
        catch (Exception ex)
        {
            Log.Error($"Play {soundId} failed", ex);
            await client.SendAsync(Serialize(new { type = "error", requestId, message = L.T("ws.playFailed") }));
        }
    }

    private void QueueStateBroadcast()
    {
        // Coalesce bursts (e.g. several config writes during import) into one push.
        if (Interlocked.Exchange(ref _stateQueued, 1) == 1) return;
        _ = Task.Delay(40).ContinueWith(async _ =>
        {
            Interlocked.Exchange(ref _stateQueued, 0);
            await BroadcastAsync(new { type = "state", data = _service.BuildState() });
        });
    }

    private void QueueYouTubeBroadcast()
    {
        // Dragging the volume or a list loading sends many changes; the dashboard needs the last one.
        if (Interlocked.Exchange(ref _youtubeQueued, 1) == 1) return;
        _ = Task.Delay(50).ContinueWith(async _ =>
        {
            Interlocked.Exchange(ref _youtubeQueued, 0);
            await BroadcastAsync(new { type = "youtube", data = _youtube.Snapshot() });
        });
    }

    private void QueueUpdatesBroadcast()
    {
        if (Interlocked.Exchange(ref _updatesQueued, 1) == 1) return;
        _ = Task.Delay(50).ContinueWith(async _ =>
        {
            Interlocked.Exchange(ref _updatesQueued, 0);
            await BroadcastAsync(new { type = "updates", data = _service.Updates.Snapshot() });
        });
    }

    private async Task BroadcastAsync(object message)
    {
        if (_clients.IsEmpty) return;
        var bytes = Serialize(message);
        await Task.WhenAll(_clients.Values.Select(c => c.SendAsync(bytes)));
    }

    private void CloseDevice(string deviceId)
    {
        foreach (var c in _clients.Values.Where(c => c.DeviceId == deviceId))
        {
            _ = c.Socket.CloseAsync((WebSocketCloseStatus)4001, "revoked", CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private byte[] Serialize(object message) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, _json));

    private sealed class Client(WebSocket socket, string deviceId)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; } = socket;
        public string DeviceId { get; } = deviceId;
        public volatile bool WantsHardware;

        public async Task SendAsync(byte[] bytes)
        {
            if (Socket.State != WebSocketState.Open) return;
            await _sendLock.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
            }
            catch
            {
                // A slow or dead client must not affect others; its receive loop will clean up.
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
