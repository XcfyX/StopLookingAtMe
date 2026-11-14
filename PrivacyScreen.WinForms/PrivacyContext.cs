using System.Windows.Forms;
using System.Drawing;
using System.Text;

namespace PrivacyScreen;

internal sealed class PrivacyContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly MessageWindow _window;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly ToolStripMenuItem _restoreAudioItem;
    private readonly System.Windows.Forms.Timer _diagnostics;
    private readonly List<ShieldForm> _shields = new();
    private readonly HashSet<uint> _physicalDown = new();
    private readonly StringBuilder _entered = new();
    private InputHooks? _hooks;
    private IndicatorWindow? _indicator;
    private FullscreenGuard? _fullscreenGuard;
    private Hotkey _unlockKey;
    private bool _locked;
    private bool _prompt;
    private bool _checking;
    private bool _caps;
    private string _promptMessage = "";
    private bool _disposed;
    private bool _audioLockStarted;

    internal PrivacyContext()
    {
        // Recover a prior crash journal BEFORE ordinary application initialization.
        // This recovery is intentional even if the current audio-mute setting is off.
        AudioMuteManager.RestorePending("startup");
        _config = AppConfig.Load();
        try { IndicatorAssets.EnsureDefault(); }
        catch (Exception e) { Log.Write("Indicator default extraction: " + e.Message); }
        if (!_config.HasPassword)
        {
            using var first = new SettingsForm(_config, requirePassword: true);
            if (first.ShowDialog() != DialogResult.OK) throw new OperationCanceledException();
        }
        _unlockKey = Hotkey.Parse(_config.UnlockHotkey);
        _caps = (Native.GetKeyState(0x14) & 1) != 0;
        _window = new MessageWindow(StartLock, VerifyRequested, VerificationFinished, OnPhysicalKey,
            HookThreadFailed);

        _menu = new ContextMenuStrip();
        _startItem = new ToolStripMenuItem("开启隐私屏保", null, (_, _) => StartLock());
        _settingsItem = new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings());
        _restoreAudioItem = new ToolStripMenuItem("恢复扬声器（未完成的静音恢复）", null, (_, _) => RestoreAudioManually());
        _exitItem = new ToolStripMenuItem("退出程序", null, (_, _) => ExitApp());
        _menu.Items.AddRange(new ToolStripItem[] { _startItem, _settingsItem,
            _restoreAudioItem, new ToolStripSeparator(), new ToolStripMenuItem("锁定期间只能通过物理密码解锁") { Enabled = false },
            new ToolStripSeparator(), _exitItem });
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "PrivacyScreen · 已就绪",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _tray.DoubleClick += (_, _) => { if (!_locked) StartLock(); };
        TryRegisterStartHotkey();
        UpdateMenu();
        _diagnostics = new System.Windows.Forms.Timer { Interval = 10_000 };
        _diagnostics.Tick += (_, _) =>
        {
            if (!_locked || _hooks is null) return;
            Log.Write($"Input counters: physical key={Interlocked.Read(ref _hooks.PhysicalKeyCount)}, " +
                $"injected key={Interlocked.Read(ref _hooks.InjectedKeyCount)}, " +
                $"physical mouse={Interlocked.Read(ref _hooks.PhysicalMouseCount)}, " +
                $"injected mouse={Interlocked.Read(ref _hooks.InjectedMouseCount)}");
        };
        _diagnostics.Start();
        Log.Write("Application ready; config: " + AppConfig.ConfigFile);
    }

    private void TryRegisterStartHotkey()
    {
        try { _window.Register(Hotkey.Parse(_config.StartHotkey)); }
        catch (Exception e)
        {
            Log.Write("Hotkey: " + e);
            MessageBox.Show("全局开启热键不可用，但仍可使用托盘图标：\n" + e.Message,
                "PrivacyScreen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenSettings()
    {
        if (_locked) return;
        using var dlg = new SettingsForm(_config);
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            _unlockKey = Hotkey.Parse(_config.UnlockHotkey);
            TryRegisterStartHotkey();
        }
    }

    private void StartLock()
    {
        if (_locked || _disposed) return;
        if (!_config.HasPassword)
        {
            MessageBox.Show("请先在设置中创建解锁密码。");
            return;
        }
        try
        {
            // 先枚举各显示器与导入图片列表；图片实际加载不会阻塞专用钩子线程。
            foreach (Screen screen in Screen.AllScreens)
                _shields.Add(new ShieldForm(screen, _config));

            _caps = (Native.GetKeyState(0x14) & 1) != 0;
            _locked = true;
            // 真正的钩子运行在独立消息线程中，不因 UI 绘画/密码校验超时。
            _hooks = new InputHooks(_window.Handle);
            Native.ReleaseModifiers();
            foreach (var shield in _shields)
            {
                shield.Show(); // ShowWithoutActivation + WS_EX_NOACTIVATE
                shield.ProtectAndStart(); // 每个 HWND 均单独调用并检查 Win32 返回值
            }
            _prompt = false;
            _checking = false;
            _physicalDown.Clear();
            _tray.Text = "PrivacyScreen · 正在保护";
            UpdateMenu();
            Log.Write($"Locked: monitors={_shields.Count}, hotkey={_unlockKey.Display}");
            // 只有全部屏保窗口完成 WDA 校验且钩子安装成功后，才显示远程可见指示窗。
            ShowIndicatorAfterSuccessfulLock();
            // Auxiliary feature: it runs only after lock success, and never throws through StartLock.
            if (_config.MuteInternalSpeakersOnLock)
            {
                _audioLockStarted = true;
                AudioMuteManager.MuteForLock();
            }
            else Log.Write("Audio: enabled=false, endpointsChanged=0");
            StartGuardAfterSuccessfulLock();
        }
        catch (Exception e)
        {
            Log.Write("Lock failed: " + e);
            StopLock();
            MessageBox.Show("屏保开启失败，已撤销钩子并恢复桌面。\n\n" + e.Message +
                "\n\n日志：" + Path.Combine(AppConfig.Root, "app.log"),
                "PrivacyScreen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartGuardAfterSuccessfulLock()
    {
        if (!_config.FullscreenGuardEnabled)
        {
            Log.Write("Guard: enabled=false, no z-order watchdog started");
            return;
        }
        try
        {
            _fullscreenGuard = new FullscreenGuard(_shields, () => _indicator);
            _fullscreenGuard.Start();
        }
        catch (Exception ex)
        {
            // The watchdog is optional; failure must never revoke the established lock.
            Log.Write("Guard: start degraded, lock remains active: " + ex);
            try { _fullscreenGuard?.Dispose(); }
            catch (Exception disposeEx) { Log.Write("Guard: cleanup degraded: " + disposeEx); }
            _fullscreenGuard = null;
        }
    }

    private void ShowIndicatorAfterSuccessfulLock()
    {
        string position = _config.IndicatorPosition == "LeftTop" ? "LeftTop" : "RightTop";
        string expectedSource = string.IsNullOrEmpty(_config.IndicatorImageFile) ? "default" : "user";
        if (!_config.ShowLockIndicator)
        {
            Log.Write($"Indicator: enabled=false, position={position}, source={expectedSource}, bounds=none, state=disabled");
            return;
        }
        try
        {
            var indicator = new IndicatorWindow(_config);
            _indicator = indicator; // 失败路径 StopLock() 仍可关闭已创建的窗口。
            indicator.ShowIndicator();
            Log.Write($"Indicator: enabled=true, position={indicator.PositionName}, source={indicator.Source}, " +
                $"bounds={indicator.Bounds}, state=shown, WDA=NONE");
        }
        catch (Exception ex)
        {
            Log.Write($"Indicator: enabled=true, position={position}, source={expectedSource}, " +
                $"bounds={_indicator?.Bounds.ToString() ?? "none"}, state=failed, error={ex}");
            // 可选指示功能失败不能解除本来已生效的屏保保护。
            try { _indicator?.Close(); _indicator?.Dispose(); }
            catch (Exception closeEx) { Log.Write("Indicator dispose after error: " + closeEx.Message); }
            _indicator = null;
        }
    }

    private void HookThreadFailed()
    {
        if (!_locked) return;
        Log.Write("Hook thread exited unexpectedly; protection removed fail-open");
        StopLock();
        MessageBox.Show("低层输入钩子意外退出，屏保已关闭以防无法解锁。请检查 app.log。",
            "PrivacyScreen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void OnPhysicalKey(uint vk, uint scan, bool down)
    {
        if (!_locked) return;
        bool firstDown = false;
        if (down) firstDown = _physicalDown.Add(vk);
        else _physicalDown.Remove(vk);
        if (!down || !firstDown) return;
        if (vk == 0x14) _caps = !_caps;

        if (!_prompt)
        {
            bool ctrl = Held(0x11, 0xA2, 0xA3);
            bool alt = Held(0x12, 0xA4, 0xA5);
            bool shift = Held(0x10, 0xA0, 0xA1);
            bool win = Held(0x5B, 0x5C);
            if (_unlockKey.Matches(vk, ctrl, alt, shift, win))
            {
                _prompt = true;
                _checking = false;
                _promptMessage = "";
                _entered.Clear();
                UpdatePasswordVisual();
                Log.Write("Local unlock prompt opened");
            }
            return;
        }

        if (_checking) return;
        if (vk == 0x1B) // Escape
        {
            _prompt = false;
            _entered.Clear();
            _promptMessage = "";
        }
        else if (vk == 0x08) // Backspace
        {
            if (_entered.Length > 0) _entered.Length--;
            _promptMessage = "";
        }
        else if (vk == 0x0D) // Return
        {
            if (_entered.Length > 0)
            {
                _checking = true;
                _window.PostVerify();
            }
        }
        else if (_entered.Length < 64 && !Held(0x11, 0xA2, 0xA3) &&
                 !Held(0x12, 0xA4, 0xA5) && !Held(0x5B, 0x5C))
        {
            string translated = TranslatePhysicalKey(vk, scan);
            if (translated.Length == 1 && translated[0] >= 0x20 && translated[0] <= 0x7e)
            {
                _entered.Append(translated);
                _promptMessage = "";
            }
        }
        UpdatePasswordVisual();
    }

    private bool Held(params uint[] keys) => keys.Any(_physicalDown.Contains);

    private string TranslatePhysicalKey(uint vk, uint scan)
    {
        try
        {
            var state = new byte[256];
            if (Held(0x10, 0xA0, 0xA1)) state[0x10] = 0x80;
            if (_caps) state[0x14] = 0x01;
            uint targetThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
            nint layout = Native.GetKeyboardLayout(targetThread);
            var sb = new StringBuilder(8);
            // bit 2=不更新键盘死键状态（Windows 10 1607+）。
            int count = Native.ToUnicodeEx(vk, scan, state, sb, sb.Capacity, 0x04, layout);
            return count == 1 ? sb.ToString(0, 1) : "";
        }
        catch { return ""; }
    }

    private void UpdatePasswordVisual()
    {
        foreach (var shield in _shields)
            shield.UpdatePrompt(_prompt, _entered.Length, _checking, _promptMessage);
    }

    private void VerifyRequested()
    {
        if (!_locked || !_prompt || !_checking) return;
        string candidate = _entered.ToString();
        _entered.Clear();
        UpdatePasswordVisual();
        _ = Task.Run(() => PasswordCrypto.Check(_config, candidate))
            .ContinueWith(t =>
            {
                if (!_disposed) _window.PostVerifyComplete(t.Status == TaskStatus.RanToCompletion && t.Result);
            }, TaskScheduler.Default);
    }

    private void VerificationFinished(bool valid)
    {
        if (!_locked || !_checking) return;
        if (valid) { Log.Write("Unlocked with local password"); StopLock(); }
        else
        {
            _checking = false;
            _entered.Clear();
            _promptMessage = "密码错误，请重试（Esc 取消）";
            UpdatePasswordVisual();
            Log.Write("Local password verification failed");
        }
    }

    private void StopLock()
    {
        // First stop event hooks and timer before changing the shield HWNDs.
        try { _fullscreenGuard?.Dispose(); }
        catch (Exception e) { Log.Write("Guard: stop degraded: " + e); }
        _fullscreenGuard = null;
        // 先令锁定状态结束，再卸载所有钩子，最后拆除屏保窗口。
        _locked = false;
        // 解锁/失败回滚时优先撤销远程可见图，避免状态错误残留。
        if (_indicator is not null)
        {
            try { _indicator.Close(); _indicator.Dispose(); }
            catch (Exception e) { Log.Write("Dispose indicator: " + e); }
            _indicator = null;
        }
        _checking = false;
        _prompt = false;
        _physicalDown.Clear();
        _entered.Clear();
        try { _hooks?.Dispose(); }
        catch (Exception e) { Log.Write("Dispose hooks: " + e); }
        _hooks = null;
        foreach (var form in _shields)
        {
            try { form.CloseSafely(); form.Dispose(); }
            catch (Exception e) { Log.Write("Dispose form: " + e); }
        }
        _shields.Clear();
        if (_audioLockStarted)
        {
            AudioMuteManager.RestorePending("unlock/rollback");
            _audioLockStarted = false;
        }
        _tray.Text = "PrivacyScreen · 已就绪";
        UpdateMenu();
    }

    private void UpdateMenu()
    {
        _startItem.Enabled = !_locked;
        _settingsItem.Enabled = !_locked;
        _exitItem.Enabled = !_locked; // 远程/普通托盘操作不能无密码直接解除。
        _restoreAudioItem.Visible = !_locked && AudioMuteManager.HasPending;
        _restoreAudioItem.Enabled = !_locked && AudioMuteManager.HasPending;
    }

    private void RestoreAudioManually()
    {
        if (_locked) return;
        AudioMuteManager.RestorePending("tray/manual");
        UpdateMenu();
        if (AudioMuteManager.HasPending)
            MessageBox.Show("部分扬声器尚未恢复（可能已断开）。请检查 app.log，设备重新连接后可以再试。",
                "PrivacyScreen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ExitApp()
    {
        if (_locked) return;
        Log.Write("Application exits normally");
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _disposed = true;
        StopLock();
        _diagnostics.Stop();
        _diagnostics.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _window.Dispose();
        base.ExitThreadCore();
    }
}
