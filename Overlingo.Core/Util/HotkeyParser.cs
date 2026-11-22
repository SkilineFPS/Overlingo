namespace Overlingo.Core;

/// <summary>Převod zkratky zapsané textem („Ctrl+Shift+F8“) na modifikátory a virtuální kód klávesy.</summary>
public static class HotkeyParser
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= Ctrl; continue;
                case "alt": modifiers |= Alt; continue;
                case "shift": modifiers |= Shift; continue;
                case "win": modifiers |= Win; continue;
            }
            if (vk != 0) return false;
            var k = KeyToVk(raw);
            if (k == 0) return false;
            vk = k;
        }
        return vk != 0;
    }

    static uint KeyToVk(string k)
    {
        if (k.Length == 1)
        {
            char c = char.ToUpperInvariant(k[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if (k.Length is 2 or 3 && (k[0] == 'F' || k[0] == 'f') && int.TryParse(k[1..], out var n) && n is >= 1 and <= 24) return (uint)(0x70 + n - 1);
        return k.ToLowerInvariant() switch
        {
            "pause" => 0x13, "insert" => 0x2D, "delete" => 0x2E, "home" => 0x24, "end" => 0x23, "pageup" => 0x21, "pagedown" => 0x22,
            "space" => 0x20, "tab" => 0x09, "scrolllock" => 0x91, "numpad0" => 0x60, "numpad1" => 0x61, "numpad2" => 0x62, "numpad3" => 0x63,
            "numpad4" => 0x64, "numpad5" => 0x65, "numpad6" => 0x66, "numpad7" => 0x67, "numpad8" => 0x68, "numpad9" => 0x69,
            _ => 0
        };
    }

    public static string VkToName(uint vk)
    {
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        return vk switch
        {
            0x13 => "Pause", 0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
            0x20 => "Space", 0x09 => "Tab", 0x91 => "ScrollLock",
            >= 0x60 and <= 0x69 => "Numpad" + (vk - 0x60),
            _ => ""
        };
    }

    public static string Format(uint modifiers, uint vk)
    {
        var n = VkToName(vk); if (n == "") return "";
        var parts = new List<string>();
        if ((modifiers & Ctrl) != 0) parts.Add("Ctrl");
        if ((modifiers & Alt) != 0) parts.Add("Alt");
        if ((modifiers & Shift) != 0) parts.Add("Shift");
        if ((modifiers & Win) != 0) parts.Add("Win");
        parts.Add(n);
        return string.Join("+", parts);
    }
}
