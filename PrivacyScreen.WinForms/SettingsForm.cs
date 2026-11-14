using System.Drawing;
using System.Windows.Forms;

namespace PrivacyScreen;

internal sealed class SettingsForm : Form
{
    private sealed class Candidate
    {
        public ImportedImage? Existing { get; init; }
        public required string SourcePath { get; init; }
        public required string OriginalName { get; init; }
        public string Sha256 { get; init; } = "";
        public bool Legacy { get; init; }
    }

    private readonly AppConfig _config;
    private readonly bool _requirePassword;
    private readonly List<Candidate> _candidates = new();
    private readonly string _stageDirectory = Path.Combine(AppConfig.Root,
        ".import-staging", Guid.NewGuid().ToString("N"));
    private readonly ListView _imageList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        MultiSelect = true, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable
    };
    private readonly Label _imageSummary = new() { Dock = DockStyle.Fill, ForeColor = Color.DimGray, AutoSize = true };
    private readonly Button _remove = new() { Text = "移除所选", Width = 94, Height = 29 };
    private readonly Button _clear = new() { Text = "清空全部", Width = 92, Height = 29 };
    private readonly TextBox _start = new() { Dock = DockStyle.Fill };
    private readonly TextBox _unlock = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _interval = new() { Minimum = 5, Maximum = 3600, Dock = DockStyle.Left, Width = 120 };
    private readonly TextBox _message = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _autostart = new() { Text = "登录 Windows 时启动", AutoSize = true };
    private readonly CheckBox _muteSpeakers = new()
        { Text = "锁定时静音内置扬声器（解锁后恢复原静音状态）", AutoSize = true };
    private readonly CheckBox _fullscreenGuardEnabled = new()
        { Text = "启用无边框全屏 Z 序保护（WinEvent + 350ms 定时兜底）", AutoSize = true };
    private readonly TextBox _newPassword = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _confirmPassword = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox _indicatorEnabled = new()
        { Text = "锁定时显示状态指示图（UU 远程画面可见）", AutoSize = true };
    private readonly ComboBox _indicatorPosition = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _indicatorImageName = new()
        { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(450, 0) };
    private string? _indicatorStaged;
    private string? _indicatorSelectedName;
    private bool _useDefaultIndicator;


    internal SettingsForm(AppConfig config, bool requirePassword = false)
    {
        _config = config;
        _requirePassword = requirePassword || !config.HasPassword;
        Text = _requirePassword ? "PrivacyScreen · 首次设置" : "PrivacyScreen · 设置";
        Font = new Font("Microsoft YaHei UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 682);
        ShowInTaskbar = true;

        _start.Text = config.StartHotkey;
        _unlock.Text = config.UnlockHotkey;
        _interval.Value = Math.Clamp(config.SlideSeconds, 5, 3600);
        _message.Text = config.Message;
        _autostart.Checked = config.AutoStart;
        _muteSpeakers.Checked = config.MuteInternalSpeakersOnLock;
        _fullscreenGuardEnabled.Checked = config.FullscreenGuardEnabled;
        _indicatorEnabled.Checked = config.ShowLockIndicator;
        _indicatorPosition.Items.AddRange(new object[] { "右上角 · 从右侧探出", "左上角 · 从左侧探出（水平镜像）" });
        _indicatorPosition.SelectedIndex = config.IndicatorPosition == "LeftTop" ? 1 : 0;
        _useDefaultIndicator = string.IsNullOrEmpty(config.IndicatorImageFile);
        UpdateIndicatorImageLabel();
        InitializeCandidates();

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18, 18, 18, 0),
            ColumnCount = 2, RowCount = 9
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 238));
        for (int i = 1; i < 8; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var picturePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        picturePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        picturePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        picturePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        var import = new Button { Text = "导入图片…", Width = 108, Height = 29 };
        import.Click += (_, _) => ImportImages();
        _remove.Click += (_, _) => RemoveSelection();
        _clear.Click += (_, _) => ClearAll();
        _imageList.SelectedIndexChanged += (_, _) => UpdateButtons();
        actions.Controls.Add(import);
        actions.Controls.Add(_remove);
        actions.Controls.Add(_clear);
        picturePanel.Controls.Add(actions, 0, 0);
        _imageList.Columns.Add("顺序", 56);
        _imageList.Columns.Add("图片名称", 350);
        _imageList.Columns.Add("状态", 142);
        picturePanel.Controls.Add(_imageList, 0, 1);
        picturePanel.Controls.Add(_imageSummary, 0, 2);
        AddRow(grid, 0, "屏保图片库", picturePanel);
        AddRow(grid, 1, "开启快捷键", _start);
        AddRow(grid, 2, "解锁快捷键", _unlock);
        AddRow(grid, 3, "轮播间隔（秒）", _interval);
        AddRow(grid, 4, "渐变背景文字", _message);
        AddRow(grid, 5, "开机自启", _autostart);
        AddRow(grid, 6, _requirePassword ? "设置解锁密码" : "修改解锁密码", _newPassword);
        AddRow(grid, 7, "重复输入密码", _confirmPassword);

        var note = new Label
        {
            Text = _requirePassword
                ? "首次必须设置解锁密码（8–64 位 ASCII）。图片仅在点击保存后正式加入私有图片库。"
                : "密码留空即保持原密码。图片按列表顺序轮播；有图片时不叠加自定义文字。",
            Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        grid.Controls.Add(note, 1, 8);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 6, 16, 8), Height = 55
        };
        var save = new Button { Text = "保存", Width = 100, Height = 32 };
        var cancel = new Button { Text = "取消", Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveChanges();
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var screenTab = new TabPage("屏保设置");
        screenTab.Controls.Add(grid);
        var indicatorTab = new TabPage("状态指示图");
        indicatorTab.Controls.Add(BuildIndicatorSettings());
        tabs.TabPages.Add(screenTab);
        tabs.TabPages.Add(indicatorTab);
        var audioTab = new TabPage("扬声器");
        audioTab.Controls.Add(BuildAudioSettings());
        tabs.TabPages.Add(audioTab);
        var guardTab = new TabPage("全屏兼容");
        guardTab.Controls.Add(BuildGuardSettings());
        tabs.TabPages.Add(guardTab);
        Controls.Add(tabs);
        Controls.Add(footer);
        AcceptButton = save;
        CancelButton = cancel;
        FormClosed += (_, _) => CleanupStaging();
        RefreshImageList();
    }

    private Control BuildGuardSettings()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(24, 25, 24, 10)
        };
        panel.Controls.Add(_fullscreenGuardEnabled);
        panel.Controls.Add(new Label
        {
            Width = 660, Height = 110, ForeColor = Color.DimGray,
            Text = "锁定成功后监听前景变化、窗口显示、Z 序重排事件，且每 350ms 兜底检查。\n" +
                "仅当外部可见窗口实际遮挡盾窗至少约 100×100 像素时，才重新置顶本程序的盾窗和狗头；" +
                "不会激活窗口或改变游戏的位置、尺寸。\n" +
                "对任务管理器等紧急恢复工具让行。真独占全屏不保证覆盖，建议改用无边框全屏。"
        });
        return panel;
    }

    private Control BuildAudioSettings()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(24, 25, 24, 10)
        };
        panel.Controls.Add(_muteSpeakers);
        panel.Controls.Add(new Label
        {
            Width = 640, Height = 100, ForeColor = Color.DimGray,
            Text = "仅在屏保已成功锁定后尝试 Core Audio 静音；解锁或下次启动恢复原状态。\n" +
                "先识别 Speakers 端点和物理内部连接，证据不足时仅考虑默认扬声器端点。\n" +
                "不会操作 HDMI/DP、USB、蓝牙等已识别的外部输出；实际机型兼容性请核对 app.log。"
        });
        return panel;
    }

    private Control BuildIndicatorSettings()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 6,
            Padding = new Padding(25, 28, 25, 10)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));

        panel.Controls.Add(new Label
        {
            Text = "仅在成功锁定后显示", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        panel.Controls.Add(_indicatorEnabled, 1, 0);
        AddRow(panel, 1, "探头位置", _indicatorPosition);

        var changeButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty
        };
        var change = new Button { Text = "更换指示图…", Width = 125, Height = 32 };
        var restore = new Button { Text = "恢复默认狗头", Width = 132, Height = 32 };
        change.Click += (_, _) => ChooseIndicatorImage();
        restore.Click += (_, _) =>
        {
            _indicatorStaged = null;
            _indicatorSelectedName = null;
            _useDefaultIndicator = true;
            UpdateIndicatorImageLabel();
        };
        changeButtons.Controls.Add(change);
        changeButtons.Controls.Add(restore);
        AddRow(panel, 2, "单独更换图片", changeButtons);
        AddRow(panel, 3, "当前图片", _indicatorImageName);

        var note = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray,
            Text = "默认 104 px（100% DPI），随主屏 DPI 缩放；28% 从左右屏幕边缘裁出，顶部贴齐。" +
                "只在主显示器显示，不显示文字和边框。"
        };
        panel.Controls.Add(note, 1, 4);
        var formatNote = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray,
            Text = "优先选择透明底 PNG；也支持 JPG、JPEG、BMP。" +
                "不透明图片将居中裁成正方形显示。图片只在点击保存时加入私有指示图库，" +
                "与屏保轮播图片列表相互独立。"
        };
        panel.Controls.Add(formatNote, 1, 5);
        return panel;
    }

    private void ChooseIndicatorImage()
    {
        using var picker = new OpenFileDialog
        {
            Title = "选择远程状态指示图",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|透明 PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _indicatorStaged = IndicatorAssets.StageSelectedImage(picker.FileName, _stageDirectory);
            _indicatorSelectedName = Path.GetFileName(picker.FileName);
            _useDefaultIndicator = false;
            UpdateIndicatorImageLabel();
        }
        catch (Exception ex)
        {
            Log.Write("Choose indicator image: " + ex);
            MessageBox.Show(this, ex.Message, "指示图导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateIndicatorImageLabel()
    {
        if (_useDefaultIndicator)
            _indicatorImageName.Text = "内置默认：中央最大狗头（真透明 PNG）";
        else if (_indicatorStaged is not null)
            _indicatorImageName.Text = "待保存：" + _indicatorSelectedName;
        else
            _indicatorImageName.Text = IndicatorAssets.IsValidUserName(_config.IndicatorImageFile)
                ? "已保存自定义：" + _config.IndicatorImageFile
                : "配置图片无效，锁定时自动回退到默认狗头";
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control control)
    {
        grid.Controls.Add(new Label
        {
            Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        grid.Controls.Add(control, 1, row);
    }

    private void InitializeCandidates()
    {
        foreach (ImportedImage item in _config.ImportedImages)
        {
            if (!ImageLibrary.TryResolve(item, out string path))
            {
                Log.Write("Settings ignored unsafe library entry: " + item.StoredName);
                continue;
            }
            string hash = item.Sha256;
            if (string.IsNullOrEmpty(hash) && File.Exists(path))
            {
                try { hash = ImageLibrary.HashFile(path); }
                catch (Exception ex) { Log.Write("Existing image hash: " + ex.Message); }
            }
            _candidates.Add(new Candidate
            {
                Existing = item, SourcePath = path,
                OriginalName = string.IsNullOrWhiteSpace(item.OriginalName) ? item.StoredName : item.OriginalName,
                Sha256 = hash
            });
        }
        if (_config.ImageLibraryVersion == 0 && _config.ImportedImages.Count == 0)
        {
            // 旧版配置直接兼容；首次打开新版设置时预填旧图片，保存才复制到新库。
            foreach (string source in ImageLibrary.LegacyPaths(_config.ImageFolder))
            {
                string hash = "";
                try { hash = ImageLibrary.HashFile(source); }
                catch (Exception ex) { Log.Write("Legacy image hash: " + ex.Message); }
                if (!string.IsNullOrEmpty(hash) && _candidates.Any(c => c.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _candidates.Add(new Candidate
                {
                    SourcePath = source, OriginalName = Path.GetFileName(source), Sha256 = hash, Legacy = true
                });
            }
        }
    }

    private void ImportImages()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "导入屏保图片（可多选）",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        int added = 0, duplicates = 0;
        var failures = new List<string>();
        foreach (string selected in dialog.FileNames)
        {
            try
            {
                if (!ImageLibrary.IsSupported(selected)) throw new InvalidDataException("不支持的图片格式");
                string hash = ImageLibrary.HashFile(selected);
                if (_candidates.Any(x => x.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                { duplicates++; continue; }
                Directory.CreateDirectory(_stageDirectory);
                string staged = Path.Combine(_stageDirectory,
                    Guid.NewGuid().ToString("N") + Path.GetExtension(selected).ToLowerInvariant());
                File.Copy(selected, staged);
                if (!ImageLibrary.HashFile(staged).Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(staged);
                    throw new IOException("图片复制校验失败");
                }
                _candidates.Add(new Candidate
                {
                    SourcePath = staged, OriginalName = Path.GetFileName(selected), Sha256 = hash
                });
                added++;
            }
            catch (Exception ex)
            {
                Log.Write("Import image: " + selected + ": " + ex.Message);
                failures.Add(Path.GetFileName(selected) + "：" + ex.Message);
            }
        }
        RefreshImageList();
        if (duplicates > 0 || failures.Count > 0)
        {
            string message = $"新加入 {added} 张；跳过重复内容 {duplicates} 张。";
            if (failures.Count > 0) message += "\n失败文件：\n" + string.Join("\n", failures.Take(8));
            MessageBox.Show(this, message, "图片导入结果", MessageBoxButtons.OK,
                failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }

    private void RemoveSelection()
    {
        foreach (int index in _imageList.SelectedIndices.Cast<int>().OrderByDescending(x => x))
            _candidates.RemoveAt(index);
        RefreshImageList();
    }

    private void ClearAll()
    {
        if (_candidates.Count == 0) return;
        if (MessageBox.Show(this, "确定清空图片列表吗？保存后会删除图片库中不再使用的图片。\n不会删除旧文件夹的原始图片。",
                "清空图片库", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _candidates.Clear();
        RefreshImageList();
    }

    private void RefreshImageList()
    {
        _imageList.BeginUpdate();
        try
        {
            _imageList.Items.Clear();
            for (int i = 0; i < _candidates.Count; i++)
            {
                Candidate candidate = _candidates[i];
                string state = candidate.Legacy ? "旧目录待迁移" : candidate.Existing is null ? "新导入待保存" : "已导入";
                var item = new ListViewItem((i + 1).ToString());
                item.SubItems.Add(candidate.OriginalName);
                item.SubItems.Add(state);
                _imageList.Items.Add(item);
            }
        }
        finally { _imageList.EndUpdate(); }
        int legacy = _candidates.Count(x => x.Legacy);
        _imageSummary.Text = $"共 {_candidates.Count} 张 · 按列表顺序轮播" +
            (legacy > 0 ? $" · 旧目录 {legacy} 张将在保存时迁入新库" : "");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _remove.Enabled = _imageList.SelectedItems.Count > 0;
        _clear.Enabled = _candidates.Count > 0;
    }

    private void SaveChanges()
    {
        try
        {
            string password = _newPassword.Text;
            if (_requirePassword || password.Length > 0 || _confirmPassword.Text.Length > 0)
            {
                if (!PasswordCrypto.Allowed(password))
                    throw new ArgumentException("密码必须是 8–64 位 ASCII 可打印字符（英文、数字或符号）。");
                if (!string.Equals(password, _confirmPassword.Text, StringComparison.Ordinal))
                    throw new ArgumentException("两次输入的密码不一致。");
            }
            Hotkey start = Hotkey.Parse(_start.Text);
            Hotkey unlock = Hotkey.Parse(_unlock.Text);
            if (start.Modifiers == unlock.Modifiers && start.VirtualKey == unlock.VirtualKey)
                throw new ArgumentException("开启和解锁快捷键不能相同。");

            var next = _config.Copy(); // 先保留原 PBKDF2 salt/hash/iterations，再覆盖用户实际改过的字段。
            var imported = new List<ImportedImage>();
            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Candidate c in _candidates)
            {
                ImportedImage entry;
                if (c.Existing is not null)
                {
                    if (!ImageLibrary.TryResolve(c.Existing, out string path) || !File.Exists(path))
                        throw new FileNotFoundException("图片库文件不存在，请从列表移除后重试：" + c.OriginalName);
                    entry = new ImportedImage
                    {
                        StoredName = c.Existing.StoredName, OriginalName = c.OriginalName,
                        Sha256 = c.Sha256
                    };
                    if (string.IsNullOrEmpty(entry.Sha256)) entry.Sha256 = ImageLibrary.HashFile(path);
                }
                else
                    entry = ImageLibrary.EnsureStored(c.SourcePath, c.OriginalName, c.Sha256);
                if (hashes.Add(entry.Sha256)) imported.Add(entry);
            }
            next.ImportedImages = imported;
            next.ImageLibraryVersion = 1;
            // 不重写 ImageFolder：留存原值供查找旧版素材；新版播放不再读取它。
            next.StartHotkey = _start.Text.Trim();
            next.UnlockHotkey = _unlock.Text.Trim();
            next.SlideSeconds = (int)_interval.Value;
            next.Message = _message.Text.Trim();
            next.AutoStart = _autostart.Checked;
            next.MuteInternalSpeakersOnLock = _muteSpeakers.Checked;
            next.FullscreenGuardEnabled = _fullscreenGuardEnabled.Checked;
            next.ShowLockIndicator = _indicatorEnabled.Checked;
            next.IndicatorPosition = _indicatorPosition.SelectedIndex == 1 ? "LeftTop" : "RightTop";
            next.IndicatorImageFile = _useDefaultIndicator ? "" :
                _indicatorStaged is not null ? IndicatorAssets.StoreUser(_indicatorStaged) : _config.IndicatorImageFile;
            if (password.Length != 0) PasswordCrypto.Set(next, password);
            next.Save();
            _config.CopyFrom(next);
            ImageLibrary.PruneUnreferenced(_config.ImportedImages);

            try { AppConfig.SetAutoStart(_config.AutoStart); }
            catch (Exception ex)
            {
                Log.Write("Autostart configuration: " + ex);
                MessageBox.Show(this, "配置已保存，但开机自启设置失败：\n" + ex.Message,
                    "PrivacyScreen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Log.Write("Settings save: " + ex);
            MessageBox.Show(this, ex.Message, "设置未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CleanupStaging()
    {
        try
        {
            if (Directory.Exists(_stageDirectory)) Directory.Delete(_stageDirectory, recursive: true);
        }
        catch (Exception ex) { Log.Write("Import staging cleanup: " + ex.Message); }
    }
}
