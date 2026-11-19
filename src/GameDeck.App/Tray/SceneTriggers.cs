using System.Runtime.InteropServices;
using GameDeck.Core.Audio;
using GameDeck.Core.Diagnostics;
using GameDeck.Core.Localization;
using GameDeck.Core.Scenes;

namespace GameDeck.App.Tray;

/// <summary>
/// Global shortcuts and automatic switching for Smart Audio Scenes. Lives on the tray's UI thread.
///
/// Shortcuts use RegisterHotKey on a message-only window, so nothing watches the keyboard. Automatic
/// switching hooks the Windows "foreground window changed" event, and only while it is turned on and
/// some scene has a trigger application: no polling, and no hook at all otherwise.
/// </summary>
internal sealed class SceneTriggers : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly GameDeckService _service;
    private readonly Dictionary<int, string> _hotkeyScenes = new();
    private string? _hotkeySignature;
    private readonly SceneAutoSwitch _auto = new();
    private readonly System.Windows.Forms.Timer _settle = new();
    private IReadOnlyList<(string SceneId, string AutoApp)> _rules = [];
    private WinEventProc? _hookProc; // kept in a field so the GC never collects the callback
    private IntPtr _hook;
    private string? _foregroundKey;

    public SceneTriggers(GameDeckService service)
    {
        _service = service;
        CreateHandle(new CreateParams { Parent = HwndMessage });
        _settle.Tick += (_, _) => OnSettled();
        Sync();
    }

    /// <summary>Re-reads scenes; cheap when shortcuts and rules are unchanged. UI thread only.</summary>
    public void Sync()
    {
        SyncHotkeys();
        SyncAuto();
    }

    /// <summary>Re-registers shortcuts so problem messages are rebuilt (e.g. after a language change).</summary>
    public void Reset()
    {
        _hotkeySignature = null;
        Sync();
    }

    private void SyncHotkeys()
    {
        var bindings = _service.HotkeyBindings();
        var signature = string.Join('|', bindings.Select(b => $"{b.SceneId}={b.Hotkey}"));
        if (signature == _hotkeySignature) return;
        _hotkeySignature = signature;

        foreach (var id in _hotkeyScenes.Keys) UnregisterHotKey(Handle, id);
        _hotkeyScenes.Clear();

        var problems = new Dictionary<string, string>();
        var next = 1;
        foreach (var (sceneId, text) in bindings)
        {
            if (!Hotkey.TryParse(text, out var hotkey, out _)) continue;
            var id = next++;
            if (RegisterHotKey(Handle, id, (uint)hotkey!.Modifiers | ModNoRepeat, (uint)hotkey.VirtualKey))
            {
                _hotkeyScenes[id] = sceneId;
            }
            else
            {
                problems[sceneId] = L.T("scene.hotkeyInUse", text);
                Log.Warn($"Scenes: shortcut {text} is taken by another program (error {Marshal.GetLastWin32Error()})");
            }
        }
        _service.ReportHotkeyProblems(problems);
    }

    private void SyncAuto()
    {
        _rules = _service.AutoRules();
        if (_rules.Count > 0 && _hook == IntPtr.Zero)
        {
            _hookProc = OnForeground;
            _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _hookProc, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_hook == IntPtr.Zero) Log.Warn("Scenes: could not watch the foreground window; automatic switching is unavailable");
            else Log.Info("Scenes: automatic switching on");
        }
        else if (_rules.Count == 0 && _hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _settle.Stop();
            Log.Info("Scenes: automatic switching off");
        }
    }

    private void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        _foregroundKey = pid == 0 ? null : AppSessions.KeyForProcess((int)pid);
        // Settle: only an application that stays in front counts, so alt-tabbing past it does nothing.
        Restart(SceneAutoSwitch.Settle);
    }

    private void OnSettled()
    {
        _settle.Stop();
        var decision = _auto.Decide(_foregroundKey, _rules, _service.ActiveSceneId, DateTime.UtcNow);
        if (decision.RetryAfter > TimeSpan.Zero) Restart(decision.RetryAfter);
        else if (decision.SceneId is { } id) Apply(id, "auto");
    }

    private void Restart(TimeSpan delay)
    {
        _settle.Stop();
        _settle.Interval = Math.Max(100, (int)delay.TotalMilliseconds);
        _settle.Start();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && _hotkeyScenes.TryGetValue((int)m.WParam, out var sceneId))
        {
            Apply(sceneId, "hotkey");
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Off the UI thread: Core Audio calls belong on the thread pool.</summary>
    private void Apply(string sceneId, string source) => Task.Run(() =>
    {
        try { _service.ApplyScene(sceneId, source); }
        catch (Exception ex) { Log.Warn($"Scenes: {source} activation of {sceneId} failed", ex); }
    });

    public void Dispose()
    {
        _settle.Dispose();
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        if (Handle != IntPtr.Zero)
        {
            foreach (var id in _hotkeyScenes.Keys) UnregisterHotKey(Handle, id);
            DestroyHandle();
        }
        _hotkeyScenes.Clear();
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
