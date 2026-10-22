using System.Windows.Input;
using System.Windows.Interop;
using Everything.App.Native;

namespace Everything.App.Services;

/// <summary>Sistem genelinde çalışan klavye kısayolu (RegisterHotKey).</summary>
internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4556; // "EV"
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private bool _registered;

    public HotkeyService()
    {
        // Görünmez, yalnızca mesaj alan pencere.
        _source = new HwndSource(new HwndSourceParameters("EverythingHotkey") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        _source.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    /// <summary>"Ctrl+Shift+F" gibi bir kısayolu kaydeder. Başarısızsa (başka program kullanıyorsa) false.</summary>
    public bool Register(string? text)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(text) || !TryParse(text, out var modifiers, out var key))
            return string.IsNullOrWhiteSpace(text);

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = Win32.RegisterHotKey(_source.Handle, HotkeyId, ToNative(modifiers) | ModNoRepeat, vk);
        if (!_registered)
            AppPaths.Log($"Kısayol kaydedilemedi: {text}");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
            Win32.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }

    public static bool TryParse(string text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out key))
                        return false;
                    break;
            }
        }
        return key != Key.None && modifiers != ModifierKeys.None;
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private static uint ToNative(ModifierKeys modifiers) =>
        (modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0)
        | (modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0)
        | (modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0)
        | (modifiers.HasFlag(ModifierKeys.Windows) ? ModWin : 0);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }
}
