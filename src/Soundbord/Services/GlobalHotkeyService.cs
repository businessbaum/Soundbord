using System.Runtime.InteropServices;
using Soundbord.Models;

namespace Soundbord.Services;

/// <summary>
/// Globale Tastenkürzel über einen Low-Level-Tastatur-Hook.
/// </summary>
/// <remarks>
/// Warum ein Hook und nicht <c>RegisterHotKey</c>: RegisterHotKey belegt die Taste
/// systemweit exklusiv. Wer als Hotkey "F1" oder gar "B" wählt, könnte diese Taste
/// danach nirgends mehr normal benutzen. Der Hook hört nur mit und leitet die Taste
/// standardmäßig durch – blockieren ist eine bewusste Einstellung.
///
/// Grenze der Technik: Läuft die Zielanwendung mit höheren Rechten (Spiel als
/// Administrator), erreichen uns deren Tastendrücke nicht. Dann muss auch das
/// Soundbord als Administrator laufen.
/// </remarks>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_ESCAPE = 0x1B;

    private readonly HashSet<int> _pressed = [];
    private readonly Dictionary<Hotkey, Action> _bindings = new();
    private readonly object _gate = new();

    // Das Delegate muss am Leben bleiben, sonst sammelt der GC den Callback ein
    // und Windows ruft in freigegebenen Speicher – ein Klassiker.
    private readonly LowLevelKeyboardProc _proc;

    private IntPtr _hook = IntPtr.Zero;
    private bool _capturing;
    private bool _disposed;

    public GlobalHotkeyService()
    {
        _proc = HookCallback;
    }

    /// <summary>Global aktiv? Beim Aufnehmen eines neuen Kürzels weiterhin lauschen.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Belegte Tasten für andere Programme schlucken.</summary>
    public bool SuppressMatched { get; set; }

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>Beim Aufnehmen gedrückte Kombination. Null = abgebrochen.</summary>
    public event Action<Hotkey?>? CaptureCompleted;

    /// <summary>
    /// Muss aus einem Thread mit Nachrichtenschleife aufgerufen werden – bei uns
    /// der UI-Thread. Ohne Nachrichtenschleife ruft Windows den Hook nie auf.
    /// </summary>
    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;

        var module = GetModuleHandle(null);
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, module, 0);

        if (_hook != IntPtr.Zero) SeedModifierState();
        return _hook != IntPtr.Zero;
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Ersetzt sämtliche Belegungen in einem Rutsch.</summary>
    public void SetBindings(IEnumerable<(Hotkey Hotkey, Action Action)> bindings)
    {
        lock (_gate)
        {
            _bindings.Clear();
            foreach (var (hotkey, action) in bindings)
            {
                if (hotkey.IsEmpty) continue;
                _bindings[hotkey.Clone()] = action; // spätere Duplikate gewinnen
            }
        }
    }

    /// <summary>Liefert die Kombination, mit der ein Kürzel bereits belegt ist.</summary>
    public bool IsBound(Hotkey hotkey)
    {
        lock (_gate) return !hotkey.IsEmpty && _bindings.ContainsKey(hotkey);
    }

    public void BeginCapture()
    {
        lock (_gate) _capturing = true;
    }

    public void CancelCapture()
    {
        bool was;
        lock (_gate)
        {
            was = _capturing;
            _capturing = false;
        }

        if (was) CaptureCompleted?.Invoke(null);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != HC_ACTION) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var message = (int)wParam;
        var vk = Marshal.ReadInt32(lParam);

        var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isUp = message is WM_KEYUP or WM_SYSKEYUP;

        if (!isDown && !isUp) return CallNextHookEx(_hook, nCode, wParam, lParam);

        bool isRepeat;
        lock (_gate)
        {
            if (isUp)
            {
                _pressed.Remove(vk);
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            isRepeat = !_pressed.Add(vk);
        }

        if (isRepeat) return CallNextHookEx(_hook, nCode, wParam, lParam);
        if (IsModifier(vk)) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var hotkey = new Hotkey { VirtualKey = vk, Modifiers = CurrentModifiers() };

        // --- Aufnahmemodus -------------------------------------------------
        bool capturing;
        lock (_gate) capturing = _capturing;

        if (capturing)
        {
            lock (_gate) _capturing = false;
            var result = vk == VK_ESCAPE ? null : hotkey;

            // Nur benachrichtigen – die eigentliche Arbeit passiert außerhalb des Hooks.
            CaptureCompleted?.Invoke(result);
            return 1; // Diese Taste gehört uns, sie darf nicht durchrutschen.
        }

        // --- normale Auslösung ---------------------------------------------
        if (!Enabled) return CallNextHookEx(_hook, nCode, wParam, lParam);

        Action? action;
        lock (_gate) _bindings.TryGetValue(hotkey, out action);

        if (action is null) return CallNextHookEx(_hook, nCode, wParam, lParam);

        // action() reiht nur ein und kehrt sofort zurück (siehe SerialWorkQueue).
        action();

        return SuppressMatched ? 1 : CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;

        if (_pressed.Contains(0x11) || _pressed.Contains(0xA2) || _pressed.Contains(0xA3))
            modifiers |= HotkeyModifiers.Control;
        if (_pressed.Contains(0x12) || _pressed.Contains(0xA4) || _pressed.Contains(0xA5))
            modifiers |= HotkeyModifiers.Alt;
        if (_pressed.Contains(0x10) || _pressed.Contains(0xA0) || _pressed.Contains(0xA1))
            modifiers |= HotkeyModifiers.Shift;
        if (_pressed.Contains(0x5B) || _pressed.Contains(0x5C))
            modifiers |= HotkeyModifiers.Win;

        return modifiers;
    }

    /// <summary>
    /// Modifikatoren, die schon vor dem Einhängen gedrückt waren, nachtragen –
    /// sonst wäre der Zustand bis zum nächsten Loslassen falsch.
    /// </summary>
    private void SeedModifierState()
    {
        foreach (var vk in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                lock (_gate) _pressed.Add(vk);
            }
        }
    }

    private static bool IsModifier(int vk) => vk is
        0x10 or 0x11 or 0x12 or
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or
        0x5B or 0x5C;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }

    // ================= Win32 =================

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
