namespace GameDeck.Core.Scenes;

/// <param name="SceneId">The scene to apply now, or null.</param>
/// <param name="RetryAfter">When non-zero, ask again after this delay (the cooldown is still running).</param>
public sealed record SwitchDecision(string? SceneId, TimeSpan RetryAfter)
{
    public static readonly SwitchDecision None = new(null, TimeSpan.Zero);
}

/// <summary>
/// Decides whether the application that just came to the front should switch scenes. Pure logic, no
/// Win32: the caller reports foreground changes (already debounced by <see cref="Settle"/>).
/// A scene that is already active is never applied again, and two automatic switches are at least
/// <see cref="Cooldown"/> apart, so alt-tabbing cannot make scenes flicker.
/// </summary>
public sealed class SceneAutoSwitch
{
    /// <summary>How long an application must stay in front before it counts.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    private DateTime _lastSwitchUtc = DateTime.MinValue;

    /// <param name="rules">Scene id and the executable that triggers it.</param>
    public SwitchDecision Decide(string? foregroundKey, IEnumerable<(string SceneId, string AutoApp)> rules, string? activeSceneId, DateTime nowUtc)
    {
        if (foregroundKey is null) return SwitchDecision.None;
        var match = rules.FirstOrDefault(r => string.Equals(r.AutoApp, foregroundKey, StringComparison.OrdinalIgnoreCase));
        if (match.SceneId is null || match.SceneId == activeSceneId) return SwitchDecision.None;

        var since = nowUtc - _lastSwitchUtc;
        if (since < Cooldown) return new SwitchDecision(null, Cooldown - since);

        _lastSwitchUtc = nowUtc;
        return new SwitchDecision(match.SceneId, TimeSpan.Zero);
    }
}
