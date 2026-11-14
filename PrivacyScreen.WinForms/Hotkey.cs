using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PrivacyScreen;

internal readonly record struct Hotkey(uint Modifiers, uint VirtualKey, string Display)
{
    internal static Hotkey Parse(string input)
    {
        uint mods = 0;
        uint key = 0;
        var parts = input.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new FormatException("快捷键格式例如 Ctrl+Alt+S 或 Ctrl+Alt+F12。");
        foreach (string p in parts)
        {
            string t = p.ToUpperInvariant();
            switch (t)
            {
                case "CTRL": case "CONTROL": mods |= Native.MOD_CONTROL; break;
                case "ALT": mods |= Native.MOD_ALT; break;
                case "SHIFT": mods |= Native.MOD_SHIFT; break;
                case "WIN": case "WINDOWS": mods |= Native.MOD_WIN; break;
                default:
                    if (key != 0) throw new FormatException("只能指定一个主键。");
                    if (t.Length == 1 && ((t[0] >= 'A' && t[0] <= 'Z') ||
                        (t[0] >= '0' && t[0] <= '9')))
                        key = t[0];
                    else if (t.StartsWith('F') && int.TryParse(t.AsSpan(1), out int f) && f >= 1 && f <= 12)
                        key = (uint)(0x70 + f - 1);
                    else throw new FormatException("支持 A-Z、0-9、F1-F12 作为主键。");
                    break;
            }
        }
        if (mods == 0 || key == 0) throw new FormatException("快捷键必须包含修饰键和主键。");
        // 热键的 Ctrl+Alt 组合用于本地解锁；Win 键也支持但可能被 OS 截获。
        return new Hotkey(mods, key, input);
    }

    internal bool Matches(uint key, bool ctrl, bool alt, bool shift, bool win)
    {
        uint got = (ctrl ? Native.MOD_CONTROL : 0)
            | (alt ? Native.MOD_ALT : 0)
            | (shift ? Native.MOD_SHIFT : 0)
            | (win ? Native.MOD_WIN : 0);
        return key == VirtualKey && got == Modifiers;
    }
}

internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int HOTKEY_ID = 100;
    private readonly Action _onStart;
    private readonly Action _onVerify;
    private readonly Action<bool> _onVerifyComplete;
    private readonly Action<uint, uint, bool> _onPhysicalKey;
    private readonly Action _onHookThreadFailed;
    private bool _registered;

    internal MessageWindow(Action onStart, Action onVerify, Action<bool> onVerifyComplete,
        Action<uint, uint, bool> onPhysicalKey, Action onHookThreadFailed)
    {
        _onStart = onStart;
        _onVerify = onVerify;
        _onVerifyComplete = onVerifyComplete;
        _onPhysicalKey = onPhysicalKey;
        _onHookThreadFailed = onHookThreadFailed;
        CreateHandle(new CreateParams { Caption = "PrivacyScreen.MessageWindow", Width = 0, Height = 0 });
    }

    internal void Register(Hotkey hotkey)
    {
        if (_registered) { Native.UnregisterHotKey(Handle, HOTKEY_ID); _registered = false; }
        if (!Native.RegisterHotKey(Handle, HOTKEY_ID,
            hotkey.Modifiers | Native.MOD_NOREPEAT, hotkey.VirtualKey))
            throw new InvalidOperationException($"注册启动热键 {hotkey.Display} 失败，Win32 错误 {Marshal.GetLastWin32Error()}。可能已被占用。");
        _registered = true;
    }

    internal void PostVerify() => Native.PostMessage(Handle, Native.WM_APP + 1, 0, 0);
    internal void PostVerifyComplete(bool valid) =>
        Native.PostMessage(Handle, Native.WM_APP + 2, valid ? 1 : 0, 0);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam == HOTKEY_ID) _onStart();
        else if (m.Msg == Native.WM_APP + 1) _onVerify();
        else if (m.Msg == Native.WM_APP + 2) _onVerifyComplete(m.WParam != 0);
        else if (m.Msg == Native.WM_APP + 3)
            _onPhysicalKey(unchecked((uint)m.WParam.ToInt64()),
                unchecked((uint)((ulong)m.LParam.ToInt64() >> 1)),
                (((ulong)m.LParam.ToInt64()) & 1UL) != 0);
        else if (m.Msg == Native.WM_APP + 4) _onHookThreadFailed();
        else base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            if (_registered) Native.UnregisterHotKey(Handle, HOTKEY_ID);
            DestroyHandle();
        }
    }
}
