using GameDeck.Core.Scenes;

namespace GameDeck.Core.Deck;

/// <summary>
/// A keyboard combination a deck button presses, e.g. "Ctrl+Shift+S" or "Alt+Tab".
///
/// A combination can never type text: letters, digits, Enter, Space, Backspace and Delete need Ctrl,
/// Alt or Win. Only function, navigation and Esc/Tab/PrintScreen keys may be pressed alone. So a
/// button or a macro cannot be turned into a way of typing commands.
/// </summary>
public sealed record KeyCombo(HotkeyModifiers Modifiers, string Key, ushort VirtualKey, bool Extended)
{
    private static readonly Dictionary<string, (string Name, ushort Vk, bool Extended, bool SafeAlone)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tab"] = ("Tab", 0x09, false, true),
        ["Enter"] = ("Enter", 0x0D, false, false),
        ["Esc"] = ("Esc", 0x1B, false, true),
        ["Escape"] = ("Esc", 0x1B, false, true),
        ["Space"] = ("Space", 0x20, false, false),
        ["Backspace"] = ("Backspace", 0x08, false, false),
        ["Delete"] = ("Delete", 0x2E, true, false),
        ["Insert"] = ("Insert", 0x2D, true, true),
        ["Home"] = ("Home", 0x24, true, true),
        ["End"] = ("End", 0x23, true, true),
        ["PageUp"] = ("PageUp", 0x21, true, true),
        ["PageDown"] = ("PageDown", 0x22, true, true),
        ["Left"] = ("Left", 0x25, true, true),
        ["Up"] = ("Up", 0x26, true, true),
        ["Right"] = ("Right", 0x27, true, true),
        ["Down"] = ("Down", 0x28, true, true),
        ["PrintScreen"] = ("PrintScreen", 0x2C, true, true),
    };

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

    /// <param name="errorKey">"deck.keysInvalid" or "deck.keysWouldType".</param>
    public static bool TryParse(string? text, out KeyCombo? combo, out string? errorKey)
    {
        combo = null;
        errorKey = "deck.keysInvalid";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;

        var mods = HotkeyModifiers.None;
        (string Name, ushort Vk, bool Extended, bool SafeAlone)? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var mod = raw.ToLowerInvariant() switch
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
            if (key is not null || (key = Key_(raw)) is null) return false;
        }
        if (key is null) return false;

        var strong = (mods & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0;
        if (!strong && !key.Value.SafeAlone)
        {
            errorKey = "deck.keysWouldType";
            return false;
        }
        combo = new KeyCombo(mods, key.Value.Name, key.Value.Vk, key.Value.Extended);
        errorKey = null;
        return true;
    }

    private static (string Name, ushort Vk, bool Extended, bool SafeAlone)? Key_(string raw)
    {
        if (Named.TryGetValue(raw, out var named)) return named;
        var k = raw.ToUpperInvariant();
        if (k.Length == 1 && (k[0] is >= 'A' and <= 'Z' || k[0] is >= '0' and <= '9')) return (k, k[0], false, false);
        if (k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24) return ($"F{f}", (ushort)(0x70 + f - 1), false, true);
        if (k.Length == 4 && k.StartsWith("NUM") && k[3] is >= '0' and <= '9') return ($"Num{k[3]}", (ushort)(0x60 + (k[3] - '0')), false, false);
        return null;
    }
}
