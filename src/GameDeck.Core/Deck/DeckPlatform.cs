using System.Diagnostics;
using System.Runtime.InteropServices;
using GameDeck.Core.Scenes;

namespace GameDeck.Core.Deck;

/// <summary>The Windows side effects of deck actions, behind an interface so tests never launch or type anything.</summary>
public interface IDeckPlatform
{
    void Launch(string path);
    void OpenUrl(string url);
    void SendKeys(KeyCombo combo);
    /// <param name="key">One of <see cref="DeckRules.MediaKeys"/>.</param>
    void Media(string key);

    /// <summary>Minimizes, restores (and brings to the front) or closes every main window of a program.</summary>
    /// <param name="program">Executable name, e.g. "obs64.exe".</param>
    /// <param name="mode">One of <see cref="DeckRules.WindowModes"/>.</param>
    /// <returns>How many windows were affected; 0 when the program has no window open.</returns>
    int Window(string program, string mode);

    /// <summary>Programs with a main window open right now, to choose from in the editor.</summary>
    IReadOnlyList<OpenWindow> OpenWindows();
}

/// <param name="Program">Executable name in lower case, e.g. "obs64.exe".</param>
/// <param name="Title">The window's title bar text.</param>
/// <param name="Minimized">Every window of the program is minimized.</param>
public sealed record OpenWindow(string Program, string Title, bool Minimized = false);

/// <summary>
/// Real implementation. Programs, files and links open through the Windows shell, exactly as a
/// double-click would, with no arguments. Keys are ordinary simulated input (SendInput): nothing is
/// injected into other processes and no keyboard hook is installed, so games that only read raw or
/// protected input may ignore them.
/// </summary>
public sealed class WindowsDeckPlatform : IDeckPlatform
{
    public void Launch(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? "" })?.Dispose();

    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    public void SendKeys(KeyCombo combo)
    {
        var keys = new List<(ushort Vk, bool Extended)>();
        if (combo.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) keys.Add((0x11, false));
        if (combo.Modifiers.HasFlag(HotkeyModifiers.Alt)) keys.Add((0x12, false));
        if (combo.Modifiers.HasFlag(HotkeyModifiers.Shift)) keys.Add((0x10, false));
        if (combo.Modifiers.HasFlag(HotkeyModifiers.Win)) keys.Add((0x5B, true));
        keys.Add((combo.VirtualKey, combo.Extended));
        Press(keys);
    }

    public void Media(string key) => Press([(DeckRules.MediaKeys[key], true)]);

    // Desktop, taskbar and other shell surfaces: closing one would log the user off or hide the taskbar.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow",
    };

    public int Window(string program, string mode)
    {
        var windows = MainWindows().Where(w => w.Program == program.ToLowerInvariant()).ToList();
        foreach (var (hwnd, _, _) in windows)
        {
            switch (mode)
            {
                case "minimize":
                    ShowWindow(hwnd, SwMinimize);
                    break;
                case "restore":
                    if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
                    BringToFront(hwnd);
                    break;
                case "close":
                    // The same message as clicking X: the program may ask to save, or close to its tray icon.
                    PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                    break;
            }
        }
        return windows.Count;
    }

    /// <summary>
    /// Windows only lets the foreground app hand focus over. Sharing input state with the current
    /// foreground thread for a moment allows it without simulating any key (a fake Alt would open the
    /// target's menu). If Windows still refuses, the window is restored and flashes in the taskbar.
    /// </summary>
    private static void BringToFront(IntPtr hwnd)
    {
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var current = GetCurrentThreadId();
        var attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(current, foreground, false);
        }
    }

    public IReadOnlyList<OpenWindow> OpenWindows() =>
        MainWindows().GroupBy(w => w.Program).Select(g => new OpenWindow(g.Key, g.First().Title, g.All(w => IsIconic(w.Hwnd)))).OrderBy(w => w.Program).ToList();

    /// <summary>
    /// Visible, titled, unowned top-level windows (what the taskbar shows), minimized ones included.
    /// Never GameDeck's own windows, and never the desktop or taskbar.
    /// </summary>
    private static List<(IntPtr Hwnd, string Program, string Title)> MainWindows()
    {
        var own = Environment.ProcessId;
        var names = new Dictionary<uint, string?>();
        var list = new List<(IntPtr, string, string)>();
        var className = new System.Text.StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            var length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (ShellClasses.Contains(className.ToString())) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own || pid == 0) return true;
            if (!names.TryGetValue(pid, out var program))
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    program = p.ProcessName.ToLowerInvariant() + ".exe";
                }
                catch { program = null; } // exited meanwhile
                names[pid] = program;
            }
            if (program is null || program == "gamedeck.exe") return true;
            var title = new System.Text.StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            list.Add((hwnd, program, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private const int SwMinimize = 6, SwRestore = 9;
    private const uint GwOwner = 4, WmClose = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>All keys down in order, then up in reverse, as one atomic batch.</summary>
    internal static uint Press(IReadOnlyList<(ushort Vk, bool Extended)> keys)
    {
        var inputs = new INPUT[keys.Count * 2];
        for (var i = 0; i < keys.Count; i++)
        {
            inputs[i] = Key(keys[i].Vk, keys[i].Extended, up: false);
            inputs[inputs.Length - 1 - i] = Key(keys[i].Vk, keys[i].Extended, up: true);
        }
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) throw new InvalidOperationException($"Windows accepted {sent} of {inputs.Length} key events (error {Marshal.GetLastWin32Error()})");
        return sent;
    }

    private static INPUT Key(ushort vk, bool extended, bool up) => new()
    {
        type = 1, // INPUT_KEYBOARD
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = (extended ? 0x0001u : 0) | (up ? 0x0002u : 0),
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
