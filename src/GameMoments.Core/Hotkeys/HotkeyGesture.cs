using System.Text;

namespace GameMoments.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// 全局快捷键（修饰键 + 虚拟键码）。解析、格式化与校验不依赖 Win32，便于单元测试。
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Escape"] = 0x1B,
        ["Esc"] = 0x1B,
        ["Backspace"] = 0x08,
        ["Insert"] = 0x2D,
        ["Ins"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Del"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PgUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["PgDn"] = 0x22,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
        ["PrintScreen"] = 0x2C,
        ["PrtSc"] = 0x2C,
        ["Pause"] = 0x13,
        ["NumPad0"] = 0x60,
        ["NumPad1"] = 0x61,
        ["NumPad2"] = 0x62,
        ["NumPad3"] = 0x63,
        ["NumPad4"] = 0x64,
        ["NumPad5"] = 0x65,
        ["NumPad6"] = 0x66,
        ["NumPad7"] = 0x67,
        ["NumPad8"] = 0x68,
        ["NumPad9"] = 0x69,
        ["Multiply"] = 0x6A,
        ["Add"] = 0x6B,
        ["Subtract"] = 0x6D,
        ["Decimal"] = 0x6E,
        ["Divide"] = 0x6F,
        ["Semicolon"] = 0xBA,
        ["Plus"] = 0xBB,
        ["Comma"] = 0xBC,
        ["Minus"] = 0xBD,
        ["Period"] = 0xBE,
        ["Slash"] = 0xBF,
        ["Tilde"] = 0xC0,
        ["BracketLeft"] = 0xDB,
        ["Backslash"] = 0xDC,
        ["BracketRight"] = 0xDD,
        ["Quote"] = 0xDE,
    };

    public bool IsValid => Modifiers != HotkeyModifiers.None && VirtualKey != 0;

    public string KeyDisplayName => DescribeKey(VirtualKey);

    public override string ToString()
    {
        var builder = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) builder.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) builder.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) builder.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) builder.Append("Win+");
        builder.Append(KeyDisplayName);
        return builder.ToString();
    }

    public static HotkeyGesture? Parse(string? text)
    {
        return TryParse(text, out var gesture, out _) ? gesture : null;
    }

    public static bool TryParse(string? text, out HotkeyGesture? gesture, out string error)
    {
        gesture = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "快捷键不能为空";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "快捷键格式无效";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        var virtualKey = 0u;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "win":
                case "windows":
                case "meta":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            if (virtualKey != 0)
            {
                error = "只允许一个主按键";
                return false;
            }

            if (!TryParseKey(part, out virtualKey))
            {
                error = $"无法识别的按键“{part}”";
                return false;
            }
        }

        if (virtualKey == 0)
        {
            error = "缺少主按键";
            return false;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            error = "必须包含至少一个修饰键（Ctrl / Alt / Shift / Win）";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey);
        return true;
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                return true;
            }
            if (ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }
        }

        if (char.ToUpperInvariant(token[0]) == 'F' && int.TryParse(token.AsSpan(1), out var functionKey) && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            return true;
        }

        if (NamedKeys.TryGetValue(token, out virtualKey))
        {
            return true;
        }

        return false;
    }

    private static string DescribeKey(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z') return ((char)virtualKey).ToString();
        if (virtualKey is >= '0' and <= '9') return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87) return $"F{virtualKey - 0x70 + 1}";

        foreach (var (name, code) in NamedKeys)
        {
            if (code == virtualKey) return name;
        }

        return $"VK_{virtualKey:X2}";
    }

    public uint NativeModifiers()
    {
        uint value = 0;
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) value |= 0x0001;
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) value |= 0x0002;
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) value |= 0x0004;
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) value |= 0x0008;
        return value;
    }
}

