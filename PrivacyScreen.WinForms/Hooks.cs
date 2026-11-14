using System.Runtime.InteropServices;

namespace PrivacyScreen;

/// <summary>
/// 专用消息循环线程：避免主 UI 加载图片、绘制和密码哈希时令低层钩子超时。
/// 注入事件放行；非注入事件吞掉，并仅把物理键盘事件异步通知 UI。
/// </summary>
internal sealed class InputHooks : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly nint _receiver;
    private readonly Native.HookProc _keyboardProc;
    private readonly Native.HookProc _mouseProc;
    private Exception? _initError;
    private nint _keyboardHook;
    private nint _mouseHook;
    private uint _threadId;
    private volatile bool _running = true;

    internal long PhysicalKeyCount;
    internal long InjectedKeyCount;
    internal long PhysicalMouseCount;
    internal long InjectedMouseCount;

    internal InputHooks(nint messageWindow)
    {
        _receiver = messageWindow;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _thread = new Thread(ThreadBody)
        {
            IsBackground = true,
            Name = "PrivacyScreen.LowLevelHooks"
        };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            Dispose();
            throw new TimeoutException("输入钩子线程启动超时。");
        }
        if (_initError is not null)
        {
            Dispose();
            throw new InvalidOperationException("安装键鼠钩子失败：" + _initError.Message, _initError);
        }
    }

    private void ThreadBody()
    {
        try
        {
            _threadId = Native.GetCurrentThreadId();
            nint module = Native.GetModuleHandle(null);
            _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
            if (_keyboardHook == 0)
                throw new InvalidOperationException("WH_KEYBOARD_LL，错误码 " + Marshal.GetLastWin32Error());
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
            if (_mouseHook == 0)
                throw new InvalidOperationException("WH_MOUSE_LL，错误码 " + Marshal.GetLastWin32Error());
            _ready.Set();
            while (Native.GetMessage(out var message, 0, 0, 0) > 0)
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }
        catch (Exception e)
        {
            _initError = e;
            Log.Write("Hook thread: " + e);
            Native.PostMessage(_receiver, Native.WM_APP + 4, 0, 0);
        }
        finally
        {
            _running = false;
            if (_mouseHook != 0) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; }
            if (_keyboardHook != 0) { Native.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = 0; }
            _ready.Set();
        }
    }

    private nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code < 0 || !_running)
            return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        try
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & Native.LLKHF_INJECTED) != 0)
            {
                Interlocked.Increment(ref InjectedKeyCount);
                return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }
            Interlocked.Increment(ref PhysicalKeyCount);
            int message = (int)wParam;
            bool down = message == Native.WM_KEYDOWN || message == Native.WM_SYSKEYDOWN;
            // vkCode: 1..254；scanCode: 0..255 常见值。用消息参数仅传元信息，不传文本/密码。
            nint packed = (nint)(((ulong)data.scanCode << 1) | (down ? 1UL : 0UL));
            Native.PostMessage(_receiver, Native.WM_APP + 3, (nint)data.vkCode, packed);
            return 1;
        }
        catch { return 1; }
    }

    private nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code < 0 || !_running)
            return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
        try
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if ((data.flags & Native.LLMHF_INJECTED) != 0)
            {
                Interlocked.Increment(ref InjectedMouseCount);
                return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
            }
            Interlocked.Increment(ref PhysicalMouseCount);
            return 1;
        }
        catch { return 1; }
    }

    public void Dispose()
    {
        _running = false; // 即使释放钩子失败，回调也立即改为放行。
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, 0, 0);
        if (_thread.IsAlive && !_thread.Join(2000))
        {
            // 正常退出进程亦会自动撤销钩子；这里额外尝试本进程显式卸载。
            if (_keyboardHook != 0) Native.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
        }
        if (!_thread.IsAlive) _ready.Dispose();
    }
}
