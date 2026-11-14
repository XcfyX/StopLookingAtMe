using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PrivacyScreen;

public sealed class AppConfig
{
    // 只供 v1 配置兼容读取；保存新版设置后由 ImportedImages 接管。
    public string ImageFolder { get; set; } = "";
    public int ImageLibraryVersion { get; set; } = 0;
    public List<ImportedImage> ImportedImages { get; set; } = new();
    public string StartHotkey { get; set; } = "Ctrl+Alt+S";
    public string UnlockHotkey { get; set; } = "Ctrl+Alt+U";
    public int SlideSeconds { get; set; } = 30;
    public string Message { get; set; } = "稍事休息 · 马上回来";
    public bool AutoStart { get; set; }
    // v1.2 独立远程可见指示窗配置；老配置缺少这些键时自动使用下列默认值。
    public bool ShowLockIndicator { get; set; } = true;
    public string IndicatorPosition { get; set; } = "RightTop"; // RightTop / LeftTop
    public string IndicatorImageFile { get; set; } = ""; // 空=内嵌默认；仅允许 indicator-user-<sha256>.png
    public bool MuteInternalSpeakersOnLock { get; set; } = true; // v1.3 缺省开启
    public bool FullscreenGuardEnabled { get; set; } = true; // v1.4 缺省开启，旧 config 可直接读取
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public int PasswordIterations { get; set; } = 310_000;

    [JsonIgnore] public bool HasPassword =>
        !string.IsNullOrWhiteSpace(PasswordSalt) && !string.IsNullOrWhiteSpace(PasswordHash);

    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivacyScreen");
    public static readonly string ConfigFile = Path.Combine(Root, "config.json");

    public static AppConfig Load()
    {
        Directory.CreateDirectory(Root);
        if (!File.Exists(ConfigFile)) return new AppConfig();
        var result = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFile))
            ?? throw new InvalidDataException("配置文件为空。");
        result.ImportedImages ??= new();
        result.IndicatorPosition = result.IndicatorPosition == "LeftTop" ? "LeftTop" : "RightTop";
        result.IndicatorImageFile ??= "";
        result.SlideSeconds = Math.Clamp(result.SlideSeconds, 5, 3600);
        result.PasswordIterations = Math.Clamp(result.PasswordIterations, 210_000, 2_000_000);
        return result;
    }

    public void Save()
    {
        Directory.CreateDirectory(Root);
        var file = ConfigFile + ".tmp";
        File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file, ConfigFile, true);
    }

    // 设置界面先在副本上操作，保存成功才刷新运行中的配置实例；保留全部密码字段。
    public AppConfig Copy() => new()
    {
        ImageFolder = ImageFolder,
        ImageLibraryVersion = ImageLibraryVersion,
        ImportedImages = ImportedImages.Select(x => new ImportedImage
        {
            StoredName = x.StoredName, OriginalName = x.OriginalName, Sha256 = x.Sha256
        }).ToList(),
        StartHotkey = StartHotkey,
        UnlockHotkey = UnlockHotkey,
        SlideSeconds = SlideSeconds,
        Message = Message,
        AutoStart = AutoStart,
        ShowLockIndicator = ShowLockIndicator,
        IndicatorPosition = IndicatorPosition,
        IndicatorImageFile = IndicatorImageFile,
        MuteInternalSpeakersOnLock = MuteInternalSpeakersOnLock,
        FullscreenGuardEnabled = FullscreenGuardEnabled,
        PasswordSalt = PasswordSalt,
        PasswordHash = PasswordHash,
        PasswordIterations = PasswordIterations
    };

    public void CopyFrom(AppConfig source)
    {
        ImageFolder = source.ImageFolder;
        ImageLibraryVersion = source.ImageLibraryVersion;
        ImportedImages = source.ImportedImages;
        StartHotkey = source.StartHotkey;
        UnlockHotkey = source.UnlockHotkey;
        SlideSeconds = source.SlideSeconds;
        Message = source.Message;
        AutoStart = source.AutoStart;
        ShowLockIndicator = source.ShowLockIndicator;
        IndicatorPosition = source.IndicatorPosition;
        IndicatorImageFile = source.IndicatorImageFile;
        MuteInternalSpeakersOnLock = source.MuteInternalSpeakersOnLock;
        FullscreenGuardEnabled = source.FullscreenGuardEnabled;
        PasswordSalt = source.PasswordSalt;
        PasswordHash = source.PasswordHash;
        PasswordIterations = source.PasswordIterations;
    }

    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动注册表项。");
        if (!enabled) { key.DeleteValue("PrivacyScreen", false); return; }
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("未找到可执行文件路径。");
        if (Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("使用 dotnet xxx.dll 运行时不支持自启动，请先发布为 exe。");
        key.SetValue("PrivacyScreen", "\"" + exe + "\"", RegistryValueKind.String);
    }
}

internal static class PasswordCrypto
{
    internal static bool Allowed(string text) => text.Length >= 8 && text.Length <= 64 &&
        text.All(c => c >= 0x20 && c <= 0x7e);

    internal static void Set(AppConfig config, string password)
    {
        if (!Allowed(password)) throw new ArgumentException("密码须为 8–64 位 ASCII 可打印字符（英文、数字、符号）。");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt,
            config.PasswordIterations, HashAlgorithmName.SHA256, 32);
        config.PasswordSalt = Convert.ToBase64String(salt);
        config.PasswordHash = Convert.ToBase64String(hash);
        CryptographicOperations.ZeroMemory(hash);
    }

    internal static bool Check(AppConfig config, string password)
    {
        try
        {
            byte[] salt = Convert.FromBase64String(config.PasswordSalt);
            byte[] expected = Convert.FromBase64String(config.PasswordHash);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt,
                config.PasswordIterations, HashAlgorithmName.SHA256, expected.Length);
            bool valid = CryptographicOperations.FixedTimeEquals(actual, expected);
            CryptographicOperations.ZeroMemory(actual);
            return valid;
        }
        catch (FormatException) { return false; }
    }
}

internal static class Log
{
    internal static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Root);
            File.AppendAllText(Path.Combine(AppConfig.Root, "app.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch { /* 日志系统不应导致主程序崩溃 */ }
    }
}
