using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Reflection;

namespace PrivacyScreen;

/// <summary>内置默认狗头的释放，以及用户指示图的独立、不可变文件管理。</summary>
internal static class IndicatorAssets
{
    internal const string DefaultName = "indicator-default.png";
    private const string ResourceName = "PrivacyScreen.Resources.indicator-default.png";
    internal static string DefaultPath => Path.Combine(AppConfig.Root, DefaultName);

    internal static string EnsureDefault()
    {
        Directory.CreateDirectory(AppConfig.Root);
        string path = DefaultPath;
        if (File.Exists(path)) return path;
        using Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("未找到内嵌的默认狗头资源：" + ResourceName);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static bool IsValidUserName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        name.StartsWith("indicator-user-", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(name).Equals(name, StringComparison.Ordinal) &&
        name.Length == "indicator-user-".Length + 64 + ".png".Length &&
        name.Substring("indicator-user-".Length, 64).All(Uri.IsHexDigit);

    internal static string ResolveImage(AppConfig config, out string source)
    {
        if (IsValidUserName(config.IndicatorImageFile))
        {
            string custom = Path.Combine(AppConfig.Root, config.IndicatorImageFile);
            if (File.Exists(custom))
            {
                source = "user";
                return custom;
            }
            Log.Write("Indicator custom image missing; using default: " + config.IndicatorImageFile);
        }
        else if (!string.IsNullOrEmpty(config.IndicatorImageFile))
            Log.Write("Indicator custom image path rejected; using embedded default");
        source = "default";
        return EnsureDefault();
    }

    internal static string StageSelectedImage(string path, string stageDirectory)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            throw new InvalidDataException("仅支持 PNG、JPG、JPEG、BMP 图片。");
        if (new FileInfo(path).Length > 25_000_000)
            throw new InvalidDataException("单张图片最大为 25 MB。");
        using (var bitmap = Image.FromFile(path))
            ValidateSize(bitmap);
        Directory.CreateDirectory(stageDirectory);
        string stage = Path.Combine(stageDirectory, Guid.NewGuid().ToString("N") + extension);
        File.Copy(path, stage);
        return stage;
    }

    // 单独的指示图库，不加入屏保的 ImportedImages。重编码为 PNG；原 JPG/BMP 仍是不透明的方形。
    // 内容寻址的文件名避免在 config.json 保存失败时覆盖原有自定义图片。
    internal static string StoreUser(string stagedPath)
    {
        Directory.CreateDirectory(AppConfig.Root);
        string tmp = Path.Combine(AppConfig.Root, ".indicator-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var original = Image.FromFile(stagedPath))
            {
                ValidateSize(original);
                int side = Math.Min(1024, Math.Max(original.Width, original.Height));
                using var square = new Bitmap(side, side, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(square))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    var ext = Path.GetExtension(stagedPath).ToLowerInvariant();
                    if (ext is ".jpg" or ".jpeg" or ".bmp")
                    {
                        int crop = Math.Min(original.Width, original.Height);
                        var src = new Rectangle((original.Width - crop) / 2, (original.Height - crop) / 2, crop, crop);
                        g.DrawImage(original, new Rectangle(0, 0, side, side), src, GraphicsUnit.Pixel);
                    }
                    else
                    {
                        float factor = Math.Min((float)side / original.Width, (float)side / original.Height);
                        float w = original.Width * factor, h = original.Height * factor;
                        g.DrawImage(original, new RectangleF((side - w) / 2, (side - h) / 2, w, h));
                    }
                }
                square.Save(tmp, ImageFormat.Png);
            }
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tmp))).ToLowerInvariant();
            string fileName = "indicator-user-" + hash + ".png";
            string target = Path.Combine(AppConfig.Root, fileName);
            if (!File.Exists(target)) File.Move(tmp, target);
            return fileName;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static void ValidateSize(Image img)
    {
        if (img.Width <= 0 || img.Height <= 0 || img.Width > 8192 || img.Height > 8192 ||
            (long)img.Width * img.Height > 40_000_000)
            throw new InvalidDataException("图片分辨率过大；最长边不超过 8192，像素总数不超过 4000 万。");
    }
}
