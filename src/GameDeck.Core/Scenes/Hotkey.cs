namespace GameDeck.Core.Scenes;

/// <summary>Same values as the RegisterHotKey MOD_* flags.</summary>
[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

/// <summary>
/// A global shortcut for a scene, e.g. "Ctrl+Alt+1". Plain keys and single-modifier letters are
/// refused so a scene shortcut never swallows game controls or everyday ones like Ctrl+C: it needs
/// two modifiers, or one modifier with a function or numpad key.
/// </summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, string Key, int VirtualKey)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    /// <param name="errorKey">Localization key explaining the failure ("scene.hotkeyInvalid" or "scene.hotkeyNeedsModifiers").</param>
    public static bool TryParse(string? text, out Hotkey? hotkey, out string? errorKey)
    {
        hotkey = null;
        errorKey = "scene.hotkeyInvalid";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;

        var mods = HotkeyModifiers.None;
        string? key = null;
        var vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var part = raw.ToLowerInvariant();
            var mod = part switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "meta" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (mod != HotkeyModifiers.None)
            {
                if (mods.HasFlag(mod)) return false;
                mods |= mod;
                continue;
            }
            if (key is not null || !TryKey(raw, out key, out vk)) return false;
        }
        if (key is null) return false;

        var count = System.Numerics.BitOperations.PopCount((uint)mods);
        var special = key.StartsWith('F') && key.Length > 1 || key.StartsWith("Num", StringComparison.Ordinal);
        var strongEnough = count >= 2 || (count == 1 && special && mods != HotkeyModifiers.Shift);
        if (!strongEnough)
        {
            errorKey = "scene.hotkeyNeedsModifiers";
            return false;
        }
        hotkey = new Hotkey(mods, key, vk);
        errorKey = null;
        return true;
    }

    /// <summary>Canonical text, or null when <paramref name="text"/> is empty or not a valid shortcut.</summary>
    public static string? Normalize(string? text) => TryParse(text, out var h, out _) ? h!.ToString() : null;

    private static bool TryKey(string raw, out string? key, out int vk)
    {
        key = null;
        vk = 0;
        var k = raw.ToUpperInvariant();
        if (k.Length == 1 && k[0] is >= 'A' and <= 'Z') { key = k; vk = k[0]; return true; }
        if (k.Length == 1 && k[0] is >= '0' and <= '9') { key = k; vk = k[0]; return true; }
        if (k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24)
        {
            key = $"F{f}";
            vk = 0x70 + f - 1;
            return true;
        }
        if (k.Length == 4 && k.StartsWith("NUM") && k[3] is >= '0' and <= '9')
        {
            key = $"Num{k[3]}";
            vk = 0x60 + (k[3] - '0');
            return true;
        }
        return false;
    }
}
