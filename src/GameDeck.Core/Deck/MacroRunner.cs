using System.Collections.Concurrent;
using GameDeck.Core.Config;
using GameDeck.Core.Diagnostics;
using GameDeck.Core.Localization;

namespace GameDeck.Core.Deck;

/// <summary>
/// Runs macros: steps in order, one after another. A macro stops at its first failing step, when it
/// is cancelled, or after <see cref="Timeout"/>. The same macro never runs twice at once and at most
/// <see cref="MaxConcurrent"/> run together; steps cannot start macros (see DeckRules), so a macro
/// can never loop back into itself.
/// </summary>
public sealed class MacroRunner(Func<DeckAction, CancellationToken, Task> execute)
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
    public const int MaxConcurrent = 4;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, string> _errors = new();

    /// <summary>Raised on a worker thread when a macro starts, finishes or fails.</summary>
    public event Action? Changed;

    public IReadOnlyList<string> Running => _running.Keys.ToList();

    /// <summary>Macro id → why its last run stopped early. Cleared when it is started again.</summary>
    public IReadOnlyDictionary<string, string> Errors => new Dictionary<string, string>(_errors);

    public bool IsRunning(string id) => _running.ContainsKey(id);

    /// <summary>Starts the macro in the background. The returned task completes when it ends (it never throws).</summary>
    public Task Start(DeckMacro macro)
    {
        if (_running.Count >= MaxConcurrent && !_running.ContainsKey(macro.Id)) throw new DeckException(L.T("deck.macroBusy"));
        var cts = new CancellationTokenSource(Timeout);
        if (!_running.TryAdd(macro.Id, cts))
        {
            cts.Dispose();
            throw new DeckException(L.T("deck.macroRunning"));
        }
        _errors.TryRemove(macro.Id, out _);
        var steps = macro.Steps.ToList();
        Changed?.Invoke();
        return Task.Run(() => RunAsync(macro.Id, macro.Name, steps, cts));
    }

    public bool Stop(string id)
    {
        if (!_running.TryGetValue(id, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    public void StopAll()
    {
        foreach (var id in _running.Keys) Stop(id);
    }

    private async Task RunAsync(string id, string name, List<DeckAction> steps, CancellationTokenSource cts)
    {
        var started = DateTime.UtcNow;
        var step = 0;
        try
        {
            for (; step < steps.Count; step++)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (steps[step].Type == "wait") await Task.Delay(steps[step].DelayMs ?? 0, cts.Token);
                else await execute(steps[step], cts.Token);
            }
            Log.Info($"Macro '{name}' finished ({steps.Count} steps)");
        }
        catch (OperationCanceledException)
        {
            if (DateTime.UtcNow - started >= Timeout)
            {
                _errors[id] = L.T("deck.macroTimeout");
                Log.Warn($"Macro '{name}' timed out at step {step + 1}");
            }
            else Log.Info($"Macro '{name}' stopped at step {step + 1}");
        }
        catch (Exception ex)
        {
            _errors[id] = L.T("deck.macroStepFailed", step + 1, ex.Message);
            Log.Warn($"Macro '{name}' failed at step {step + 1}: {ex.Message}");
        }
        finally
        {
            _running.TryRemove(id, out _);
            cts.Dispose();
            Changed?.Invoke();
        }
    }
}
