using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace Soundbord.Models;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<HotkeyModifiers>))]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// Eine global registrierte Tastenkombination. Bewusst über den Win32-VirtualKey
/// definiert und nicht über Avalonias <c>Key</c>: der Low-Level-Hook liefert VKs,
/// und nur so lassen sich Layout-korrekte Namen von Windows erfragen.
/// </summary>
public sealed class Hotkey : IEquatable<Hotkey>
{
    public int VirtualKey { get; set; }

    public HotkeyModifiers Modifiers { get; set; }

    [JsonIgnore]
    public bool IsEmpty => VirtualKey == 0;

    [JsonIgnore]
    public string DisplayName => IsEmpty ? "—" : KeyNames.Format(this);

    public bool Equals(Hotkey? other) =>
        other is not null && other.VirtualKey == VirtualKey && other.Modifiers == Modifiers;

    public override bool Equals(object? obj) => Equals(obj as Hotkey);

    public override int GetHashCode() => HashCode.Combine(VirtualKey, (int)Modifiers);

    public Hotkey Clone() => new() { VirtualKey = VirtualKey, Modifiers = Modifiers };
}

/// <summary>
/// Übersetzt VirtualKeys in Anzeigenamen. Fragt Windows über <c>GetKeyNameText</c>,
/// damit die Namen zum tatsächlichen Tastaturlayout passen (Y/Z, Umlaute, ...).
/// </summary>
internal static class KeyNames
{
    private const uint MAPVK_VK_TO_VSC = 0;

    // Tasten, deren Scancode das Extended-Bit braucht, sonst liefert Windows den
    // falschen Namen (z.B. "Num 4" statt "Links").
    private static readonly HashSet<int> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp/PgDn/End/Home/Pfeile
        0x2C, 0x2D, 0x2E,                               // Druck/Einfg/Entf
        0x90, 0x6F, 0xA3, 0xA5,                         // NumLock, Num /, RStrg, RAlt
    ];

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetKeyNameTextW")]
    private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static string Format(Hotkey hotkey)
    {
        var parts = new List<string>(4);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Strg");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Umschalt");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(hotkey.VirtualKey));
        return string.Join(" + ", parts);
    }

    public static string KeyName(int virtualKey)
    {
        if (virtualKey == 0) return "—";

        try
        {
            var scanCode = MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC);
            if (scanCode != 0)
            {
                var lParam = (int)(scanCode << 16);
                if (ExtendedKeys.Contains(virtualKey)) lParam |= 1 << 24;

                var buffer = new StringBuilder(64);
                if (GetKeyNameText(lParam, buffer, buffer.Capacity) > 0)
                {
                    var name = buffer.ToString().Trim();
                    if (name.Length > 0) return Capitalize(name);
                }
            }
        }
        catch (Exception)
        {
            // Fällt unten auf die Tabelle zurück.
        }

        return Fallback(virtualKey);
    }

    private static string Capitalize(string value) =>
        value.Length > 1 && value.ToUpperInvariant() == value
            ? value[0] + value[1..].ToLowerInvariant()
            : value;

    private static string Fallback(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),                 // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),                 // A-Z
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),                     // F1-F24
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),                  // Numpad 0-9
        0x08 => "Rücktaste",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => "Leertaste",
        _ => $"VK 0x{vk:X2}",
    };
}
