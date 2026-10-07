using System.Text;

namespace Echodeck.Core.Hotkeys;

/// <summary>Modifier flags. Values match Win32 RegisterHotKey's MOD_* constants.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>
/// A global keyboard shortcut: modifiers + one Win32 virtual-key code, stored in settings as
/// text such as "Ctrl+NumPad1" or "F8".
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) sb.Append("Win+");
        sb.Append(VirtualKeys.NameOf(VirtualKey));
        return sb.ToString();
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; continue;
                case "alt": modifiers |= HotkeyModifiers.Alt; continue;
                case "shift": modifiers |= HotkeyModifiers.Shift; continue;
                case "win" or "windows": modifiers |= HotkeyModifiers.Windows; continue;
            }
            if (key is not null) return false;                 // two non-modifier keys
            if (!VirtualKeys.TryParse(raw, out int vk)) return false;
            key = vk;
        }
        if (key is null) return false;
        gesture = new HotkeyGesture(modifiers, key.Value);
        return true;
    }

    public static HotkeyGesture? ParseOrNull(string? text) => TryParse(text, out var g) ? g : null;

    /// <summary>
    /// Null if usable as a global hotkey, otherwise why not. A global hotkey swallows the key in
    /// every app, so plain letters, digits and Space need Ctrl, Alt or Win (Shift alone isn't
    /// enough), or you could no longer type them in chat.
    /// </summary>
    public string? ValidationError
    {
        get
        {
            if (VirtualKeys.IsModifierKey(VirtualKey)) return "Pick a key, not just a modifier.";
            bool strongModifier = (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0;
            if (VirtualKeys.IsTypingKey(VirtualKey) && !strongModifier)
                return $"{VirtualKeys.NameOf(VirtualKey)} on its own would stop you typing it everywhere — add Ctrl or Alt.";
            if (VirtualKey == VirtualKeys.Escape || VirtualKey == VirtualKeys.Tab || VirtualKey == VirtualKeys.Enter)
                return $"{VirtualKeys.NameOf(VirtualKey)} can't be used as a global hotkey.";
            return null;
        }
    }
}

/// <summary>Win32 virtual-key codes with human-friendly names.</summary>
public static class VirtualKeys
{
    public const int Enter = 0x0D, Escape = 0x1B, Tab = 0x09, Space = 0x20;

    private static readonly Dictionary<int, string> Names = BuildNames();
    private static readonly Dictionary<string, int> Codes =
        Names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<int, string> BuildNames()
    {
        var n = new Dictionary<int, string>
        {
            [0x08] = "Backspace", [Tab] = "Tab", [Enter] = "Enter", [0x13] = "Pause", [0x14] = "CapsLock",
            [Escape] = "Esc", [Space] = "Space", [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End",
            [0x24] = "Home", [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
            [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete", [0x91] = "ScrollLock", [0x90] = "NumLock",
            [0x6A] = "NumPad*", [0x6B] = "NumPad+", [0x6D] = "NumPad-", [0x6E] = "NumPad.", [0x6F] = "NumPad/",
            [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`",
            [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
            [0xB0] = "MediaNext", [0xB1] = "MediaPrevious", [0xB2] = "MediaStop", [0xB3] = "MediaPlayPause",
        };
        for (int i = 0; i < 26; i++) n[0x41 + i] = ((char)('A' + i)).ToString();
        for (int i = 0; i <= 9; i++) n[0x30 + i] = i.ToString();
        for (int i = 0; i <= 9; i++) n[0x60 + i] = $"NumPad{i}";
        for (int i = 1; i <= 24; i++) n[0x6F + i] = $"F{i}";
        return n;
    }

    public static string NameOf(int vk) => Names.TryGetValue(vk, out var name) ? name : $"Key{vk:X2}";

    public static bool TryParse(string name, out int vk)
    {
        if (Codes.TryGetValue(name, out vk)) return true;
        if (name.StartsWith("Key", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name[3..], System.Globalization.NumberStyles.HexNumber, null, out vk)) return true;
        vk = 0;
        return false;
    }

    /// <summary>Shift/Ctrl/Alt/Win in all their left/right variants.</summary>
    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>Keys you type text with (letters, digits, punctuation, Space).</summary>
    public static bool IsTypingKey(int vk) =>
        vk == Space || vk is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDE);
}
