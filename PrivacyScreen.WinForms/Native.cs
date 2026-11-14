using System.Runtime.InteropServices;
using System.Text;

namespace PrivacyScreen;

internal static class Native
{
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_LAYERED = 0x00080000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const uint LWA_ALPHA = 0x00000002;
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;
    internal const int WM_HOTKEY = 0x0312;
    internal const uint WM_APP = 0x8000;
    internal const uint LLKHF_INJECTED = 0x10;
    internal const uint LLMHF_INJECTED = 0x01;
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal static readonly nint HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }
    internal delegate nint HookProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int idHook, HookProc callback, nint hModule, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint hwnd, uint msg, nint wparam, nint lparam);
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")]
    internal static extern nint GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] state,
        [Out] StringBuilder output, int capacity, uint flags, nint keyboardLayout);
    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte vk, byte scan, uint flags, nuint extraInfo);

    // v1.2 指示窗：原生逐像素 Alpha，使用 PArgb 32 位 DIB。绝不调用 WDA 排除。
    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDC,
        ref POINT destPosition, ref SIZE size, nint sourceDC, ref POINT sourcePosition,
        uint colorKey, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateDIBSection(nint dc, ref BITMAPINFO info, uint usage,
        out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint SelectObject(nint dc, nint objectHandle);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint objectHandle);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint dc);

    // 清掉触发“开启”热键时可能遗留给底层应用的 Ctrl/Alt 等按下状态。
    internal static void ReleaseModifiers()
    {
        foreach (byte vk in new byte[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            keybd_event(vk, 0, KEYEVENTF_KEYUP, 0);
    }

    internal const uint WM_QUIT = 0x0012;
    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }
    [DllImport("user32.dll")]
    internal static extern int GetMessage(out MSG message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(ref MSG message);
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);
}
