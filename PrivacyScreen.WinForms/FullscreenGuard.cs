using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace PrivacyScreen;

/// <summary>
/// Lock-only, non-activating z-order watchdog. WinEvent callbacks and timer ticks
/// only coalesce a request; all enumeration and SetWindowPos calls run on the
/// existing WinForms UI thread. Never operates on foreign HWNDs.
/// </summary>
internal sealed class FullscreenGuard : IDisposable
{
    private const int MinimumIntersection = 100; // Both width AND height, in screen pixels.
    private const int PollMilliseconds = 350;
    private const int MaxEnumeratedWindows = 8192;
    private const uint ReassertFlags = Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE;
    private static readonly TimeSpan LogThrottle = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> EmergencyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Taskmgr", "ProcessHacker", "SystemInformer", "procexp", "procexp64", "procexp64a",
        "LogonUI", "consent"
    };

    private readonly IReadOnlyList<ShieldForm> _shields;
    private readonly Func<IndicatorWindow?> _indicator;
    private readonly Control _dispatcher;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly GuardApi.WinEventProc _callback;
    private readonly List<nint> _eventHooks = new();
    private GCHandle _callbackRoot;
    private volatile bool _active;
    private volatile bool _disposed;
    private int _queued;
    private DateTime _lastReassertLogUtc = DateTime.MinValue;
    private DateTime _lastFailureLogUtc = DateTime.MinValue;
    private DateTime _lastEmergencyLogUtc = DateTime.MinValue;

    internal FullscreenGuard(IReadOnlyList<ShieldForm> shields, Func<IndicatorWindow?> indicator)
    {
        _shields = shields;
        _indicator = indicator;
        // This control's HWND is created on the running UI thread. It is neither
        // shown nor activated, and BeginInvoke marshals all checks to that thread.
        _dispatcher = new Control();
        _ = _dispatcher.Handle;
        _timer = new System.Windows.Forms.Timer { Interval = PollMilliseconds };
        _timer.Tick += OnTimerTick;
        _callback = OnWinEvent;
    }

    internal void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FullscreenGuard));
        if (_active) return;
        _active = true;
        // Root the managed delegate while native WinEvent hooks hold its address.
        _callbackRoot = GCHandle.Alloc(_callback);
        foreach (uint ev in new[] { GuardApi.EVENT_SYSTEM_FOREGROUND, GuardApi.EVENT_OBJECT_SHOW,
                                    GuardApi.EVENT_OBJECT_REORDER })
        {
            try
            {
                nint hook = GuardApi.SetWinEventHook(ev, ev, nint.Zero, _callback, 0, 0,
                    GuardApi.WINEVENT_OUTOFCONTEXT | GuardApi.WINEVENT_SKIPOWNPROCESS);
                if (hook == nint.Zero)
                    Log.Write($"Guard: WinEvent registration failed event=0x{ev:X}, Win32={Marshal.GetLastWin32Error()}, timer remains enabled");
                else _eventHooks.Add(hook);
            }
            catch (Exception ex)
            {
                Log.Write($"Guard: WinEvent registration failed event=0x{ev:X}: {ex.Message}, timer remains enabled");
            }
        }
        _timer.Start();
        Log.Write($"Guard: started mode=WinEvent+timer, events=FOREGROUND/SHOW/REORDER, " +
            $"hooks={_eventHooks.Count}/3, interval={PollMilliseconds}ms, shields={_shields.Count}");
        QueueCheck();
    }

    private void OnTimerTick(object? sender, EventArgs e) => QueueCheck();

    // WinEvent delivery is often on this UI thread, but never perform window
    // enumeration/reordering inside the native callback (reentrancy risk).
    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime)
    {
        if (!_active || _disposed || hwnd == nint.Zero) return;
        if (idObject != GuardApi.OBJID_WINDOW || idChild != 0) return;
        QueueCheck();
    }

    private void QueueCheck()
    {
        if (!_active || _disposed) return;
        if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
        try
        {
            _dispatcher.BeginInvoke((Action)(() =>
            {
                try { if (_active && !_disposed) CheckOnUiThread(); }
                finally { Interlocked.Exchange(ref _queued, 0); }
            }));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _queued, 0);
            LogFailure("dispatch failed: " + ex.Message);
        }
    }

    private void CheckOnUiThread()
    {
        try
        {
            var shields = _shields.Where(s => !s.IsDisposed && s.IsHandleCreated && s.Visible)
                .Select(s => new Shield(s.Handle, s.Bounds)).ToArray();
            if (shields.Length == 0) return;

            ScanResult result = FindObstruction(shields);
            if (result.Emergency is { } emergency)
            {
                if (DateTime.UtcNow - _lastEmergencyLogUtc >= LogThrottle)
                {
                    _lastEmergencyLogUtc = DateTime.UtcNow;
                    Log.Write($"Guard: emergency window respected, no reassert, process={emergency.Process}, " +
                        $"title=\"{Sanitize(emergency.Title)}\", hwnd=0x{emergency.Handle.ToInt64():X}");
                }
                return;
            }
            if (result.Obstruction is not { } offender) return;

            // Reassert ALL shields in multiscreen order, then the indicator last.
            // Password UI is drawn on each ShieldForm, not a separate HWND.
            // SWP_NOACTIVATE preserves the remote user's game/window focus.
            int raised = 0;
            foreach (Shield shield in shields)
            {
                if (!Native.SetWindowPos(shield.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, ReassertFlags))
                    throw new InvalidOperationException($"SetWindowPos shield hwnd=0x{shield.Handle.ToInt64():X} Win32={Marshal.GetLastWin32Error()}");
                raised++;
            }
            bool indicatorRaised = false;
            IndicatorWindow? indicator = _indicator();
            if (indicator is { IsDisposed: false, IsHandleCreated: true, Visible: true })
            {
                if (!Native.SetWindowPos(indicator.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, ReassertFlags))
                    throw new InvalidOperationException($"SetWindowPos indicator hwnd=0x{indicator.Handle.ToInt64():X} Win32={Marshal.GetLastWin32Error()}");
                indicatorRaised = true;
            }
            if (DateTime.UtcNow - _lastReassertLogUtc >= LogThrottle)
            {
                _lastReassertLogUtc = DateTime.UtcNow;
                Log.Write($"Guard: reasserted shields={raised}, indicator={indicatorRaised}, " +
                    $"foreignPid={offender.Pid}, process={offender.Process}, " +
                    $"title=\"{Sanitize(offender.Title)}\", hwnd=0x{offender.Handle.ToInt64():X}, " +
                    $"overlap={offender.Overlap.Width}x{offender.Overlap.Height}");
            }
        }
        catch (Exception ex)
        {
            // Watchdog errors cannot terminate or roll back an established lock.
            LogFailure("check degraded: " + ex);
        }
    }

    private static ScanResult FindObstruction(Shield[] shields)
    {
        var ordered = new List<nint>(256);
        bool complete = GuardApi.EnumWindows((hwnd, _) =>
        {
            if (ordered.Count >= MaxEnumeratedWindows) return false;
            ordered.Add(hwnd);
            return true;
        }, nint.Zero);
        if (!complete)
            throw new InvalidOperationException($"EnumWindows failed or exceeded {MaxEnumeratedWindows} windows (Win32 {Marshal.GetLastWin32Error()})");

        var z = new Dictionary<nint, int>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++) z[ordered[i]] = i;
        foreach (var shield in shields)
            if (!z.ContainsKey(shield.Handle))
                throw new InvalidOperationException($"shield hwnd=0x{shield.Handle.ToInt64():X} absent from EnumWindows");

        // An emergency recovery tool in front must remain usable. If it is
        // foreground, don't change our z-order, regardless of other offenders.
        nint foreground = GuardApi.GetForegroundWindow();
        if (foreground != nint.Zero)
        {
            GuardApi.GetWindowThreadProcessId(foreground, out uint foregroundPid);
            if (foregroundPid != (uint)Environment.ProcessId)
            {
                string frontName = ProcessName(foregroundPid);
                if (EmergencyTools.Contains(frontName))
                    return new ScanResult(null, new Foreign(foreground, foregroundPid, frontName,
                        WindowTitle(foreground), Rectangle.Empty));
            }
        }

        int maxShieldIndex = shields.Max(s => z[s.Handle]);
        Foreign? obstruction = null;
        uint ownPid = (uint)Environment.ProcessId;
        for (int i = 0; i < maxShieldIndex; i++)
        {
            nint hwnd = ordered[i];
            if (shields.Any(s => s.Handle == hwnd)) continue;
            GuardApi.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == ownPid) continue; // ALL process-local windows are friendly.
            if (!GuardApi.IsWindowVisible(hwnd) || GuardApi.IsIconic(hwnd)) continue;
            if (GuardApi.DwmGetWindowAttribute(hwnd, GuardApi.DWMWA_CLOAKED,
                out int cloaked, sizeof(int)) >= 0 && cloaked != 0) continue;
            if (!GuardApi.GetWindowRect(hwnd, out var raw)) continue;
            var bounds = Rectangle.FromLTRB(raw.Left, raw.Top, raw.Right, raw.Bottom);
            if (bounds.Width < MinimumIntersection || bounds.Height < MinimumIntersection) continue;

            Rectangle intersection = Rectangle.Empty;
            foreach (Shield shield in shields)
            {
                if (i >= z[shield.Handle]) continue; // Not above this shield.
                Rectangle overlap = Rectangle.Intersect(bounds, shield.Bounds);
                if (overlap.Width >= MinimumIntersection && overlap.Height >= MinimumIntersection)
                {
                    intersection = overlap;
                    break;
                }
            }
            if (intersection.IsEmpty) continue;
            string name = ProcessName(pid);
            var candidate = new Foreign(hwnd, pid, name, WindowTitle(hwnd), intersection);
            if (EmergencyTools.Contains(name))
                return new ScanResult(null, candidate);
            obstruction ??= candidate;
        }
        return new ScanResult(obstruction, null);
    }

    private static string ProcessName(uint pid)
    {
        try { using var p = Process.GetProcessById(checked((int)pid)); return p.ProcessName; }
        catch { return "unknown"; }
    }

    private static string WindowTitle(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        GuardApi.GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string Sanitize(string title)
    {
        string safe = title.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
        return safe.Length <= 120 ? safe : safe[..120] + "…";
    }

    private void LogFailure(string detail)
    {
        if (DateTime.UtcNow - _lastFailureLogUtc < LogThrottle) return;
        _lastFailureLogUtc = DateTime.UtcNow;
        Log.Write("Guard: " + detail);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _active = false;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        foreach (nint hook in _eventHooks)
        {
            try
            {
                if (!GuardApi.UnhookWinEvent(hook))
                    Log.Write($"Guard: unhook failed hwnd=0x{hook.ToInt64():X}, Win32={Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex) { Log.Write("Guard: unhook degraded: " + ex.Message); }
        }
        _eventHooks.Clear();
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
        _timer.Dispose();
        _dispatcher.Dispose();
        Interlocked.Exchange(ref _queued, 0);
    }

    private readonly record struct Shield(nint Handle, Rectangle Bounds);
    private readonly record struct Foreign(nint Handle, uint Pid, string Process, string Title, Rectangle Overlap);
    private readonly record struct ScanResult(Foreign? Obstruction, Foreign? Emergency);
}

/// <summary>Win32 APIs used only by FullscreenGuard. The v1.3.2 Native.cs remains untouched.</summary>
internal static class GuardApi
{
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    internal const uint EVENT_OBJECT_SHOW = 0x8002;
    internal const uint EVENT_OBJECT_REORDER = 0x8004;
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    internal const int OBJID_WINDOW = 0;
    internal const uint DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { internal int Left, Top, Right, Bottom; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out RECT rectangle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint hwnd, StringBuilder buffer, int capacity);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmGetWindowAttribute(nint hwnd, uint attribute,
        out int attributeValue, int attributeSize);
}
