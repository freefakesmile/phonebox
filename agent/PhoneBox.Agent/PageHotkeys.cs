namespace PhoneBox.Agent;

/// <summary>
/// Global keyboard shortcuts that flip the phone's pages (Settings › General › Features). The agent doesn't know which
/// page is on screen, so it presses the phone's volume keys over USB (adb), which the panel turns into previous / next
/// page; the phone's own volume is not changed. Parsing lives here; the Windows hotkey window is in PageHotkeyWindow.
/// </summary>
public static class PageHotkeys
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    public const int KeyVolumeUp = 24, KeyVolumeDown = 25; // Android key codes: up = previous page, down = next

    /// <summary>"Ctrl+Alt+Right" → modifiers and a virtual-key code; null for something that can't be a hotkey.</summary>
    public static (uint Mods, uint Vk)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0, vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.ToLowerInvariant();
            switch (t)
            {
                case "ctrl" or "control": mods |= ModControl; continue;
                case "alt": mods |= ModAlt; continue;
                case "shift": mods |= ModShift; continue;
                case "win" or "windows" or "meta": mods |= ModWin; continue;
            }
            if (vk != 0) return null; // two keys
            vk = Key(t);
            if (vk == 0) return null;
        }
        // a bare key would take it away from every other program
        return vk != 0 && (mods & (ModControl | ModAlt | ModWin)) != 0 ? (mods, vk) : null;
    }

    private static uint Key(string t)
    {
        switch (t)
        {
            case "left": return 0x25;
            case "up": return 0x26;
            case "right": return 0x27;
            case "down": return 0x28;
            case "pageup" or "pgup": return 0x21;
            case "pagedown" or "pgdn": return 0x22;
            case "end": return 0x23;
            case "home": return 0x24;
            case "insert" or "ins": return 0x2D;
            case "space": return 0x20;
            case "[": return 0xDB;
            case "]": return 0xDD;
            case ",": return 0xBC;
            case ".": return 0xBE;
        }
        if (t.Length == 1 && t[0] is >= 'a' and <= 'z') return (uint)char.ToUpperInvariant(t[0]);
        if (t.Length == 1 && t[0] is >= '0' and <= '9') return t[0];
        if (t.Length is 2 or 3 && t[0] == 'f' && int.TryParse(t.AsSpan(1), out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        if (t.StartsWith("num") && t.Length == 4 && t[3] is >= '0' and <= '9') return (uint)(0x60 + t[3] - '0');
        return 0;
    }
}
