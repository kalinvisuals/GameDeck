using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameDeck.Core.Presence;

/// <summary>
/// The local connection to the Discord desktop app (named pipe "discord-ipc-N"), the official way
/// for programs on the same PC to set Rich Presence. No login, no token, nothing leaves the PC
/// except what Discord itself sends. Messages are an 8-byte header (opcode, length) and JSON.
/// </summary>
public sealed class DiscordIpc : IAsyncDisposable
{
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    private const int MaxFrameBytes = 64 * 1024;

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Discord's display name of the signed-in account, from the READY message.</summary>
    public string? User { get; private set; }

    private DiscordIpc(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Connects to the running Discord app; null when it isn't running or refused the application.</summary>
    public static async Task<DiscordIpc?> TryConnectAsync(string clientId, CancellationToken ct)
    {
        foreach (var name in PipeNames())
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300, ct);
                var ipc = new DiscordIpc(pipe);
                await ipc.WriteAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var (op, ready) = await ipc.ReadAsync(cts.Token);
                if (op == OpFrame && ready is not null && Str(ready, "evt") == "READY")
                {
                    var user = ready["data"]?["user"];
                    ipc.User = user is null ? null : (Str(user, "global_name") is { Length: > 0 } g ? g : Str(user, "username"));
                    return ipc;
                }
                await ipc.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or UnauthorizedAccessException or InvalidDataException)
            {
                await pipe.DisposeAsync();
                ct.ThrowIfCancellationRequested();
            }
        }
        return null;
    }

    /// <summary>Discord, Discord PTB and Canary take the first free of discord-ipc-0..9.</summary>
    private static IEnumerable<string> PipeNames()
    {
        List<string> found;
        try
        {
            found = Directory.EnumerateFiles(@"\\.\pipe\")
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => n.StartsWith("discord-ipc-", StringComparison.Ordinal) && n.Length <= 13 && char.IsAsciiDigit(n[^1]))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            found = Enumerable.Range(0, 10).Select(i => $"discord-ipc-{i}").ToList();
        }
        return found;
    }

    /// <summary>Sets the activity of this process (<paramref name="pid"/>); null clears it.</summary>
    public Task SetActivityAsync(JsonObject? activity, int pid, CancellationToken ct) =>
        WriteAsync(OpFrame, new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["nonce"] = Guid.NewGuid().ToString("N"),
            ["args"] = new JsonObject { ["pid"] = pid, ["activity"] = activity?.DeepClone() },
        }, ct);

    /// <summary>
    /// Reads Discord's replies until the connection ends. Errors (a field Discord refused) are reported
    /// through <paramref name="onError"/>; a successful update reports null.
    /// </summary>
    public async Task ListenAsync(Action<string?> onReply, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (op, body) = await ReadAsync(ct);
            switch (op)
            {
                case OpPing:
                    await WriteAsync(OpPong, body ?? new JsonObject(), ct);
                    break;
                case OpClose:
                    throw new IOException(body is null ? "closed" : Str(body, "message"));
                case OpFrame when body is not null && Str(body, "cmd") == "SET_ACTIVITY":
                    onReply(Str(body, "evt") == "ERROR" ? Str(body["data"] ?? body, "message") : null);
                    break;
            }
        }
    }

    private async Task WriteAsync(int op, JsonNode body, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(body.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await _writeLock.WaitAsync(ct);
        try
        {
            await _pipe.WriteAsync(frame, ct);
            await _pipe.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<(int Op, JsonNode? Body)> ReadAsync(CancellationToken ct)
    {
        var header = new byte[8];
        await _pipe.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length < 0 || length > MaxFrameBytes) throw new InvalidDataException("Discord sent an oversized message");
        var body = new byte[length];
        await _pipe.ReadExactlyAsync(body, ct);
        return (op, length == 0 ? null : JsonNode.Parse(body));
    }

    private static string Str(JsonNode node, string name) =>
        node is JsonObject o && o.TryGetPropertyValue(name, out var v) && v is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";

    public async ValueTask DisposeAsync()
    {
        try { await _pipe.DisposeAsync(); }
        catch (IOException) { /* already gone */ }
    }
}
