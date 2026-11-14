using System.Security.Cryptography;

namespace PrivacyScreen;

/// <summary>图片库的持久化条目：按列表先后轮播；StoredName 必须是本程序生成的库文件名。</summary>
public sealed class ImportedImage
{
    public string StoredName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

internal static class ImageLibrary
{
    internal static readonly string DirectoryPath = Path.Combine(AppConfig.Root, "images");
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".bmp" };

    internal static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    // 使用内容 SHA256 作为存储名：同名但内容不同不会覆盖，内容相同即使文件名不同也不会重复导入。
    internal static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static bool TryResolve(ImportedImage entry, out string path)
    {
        path = "";
        string filename = entry.StoredName ?? "";
        if (filename.Length != 68 && filename.Length != 69) return false; // 64 hex + .png/.jpg/.bmp/.jpeg
        if (!IsSupported(filename) || Path.GetFileName(filename) != filename) return false;
        string prefix = Path.GetFileNameWithoutExtension(filename);
        if (prefix.Length != 64 || !prefix.All(Uri.IsHexDigit)) return false;
        path = Path.Combine(DirectoryPath, filename);
        return true;
    }

    internal static List<string> PlaylistPaths(AppConfig config)
    {
        // 已保存新版设置后，即使列表为空，也不再退回旧目录（清空图片必须真的生效）。
        if (config.ImageLibraryVersion >= 1 || config.ImportedImages.Count > 0)
        {
            var paths = new List<string>();
            foreach (ImportedImage entry in config.ImportedImages)
            {
                if (TryResolve(entry, out string path)) paths.Add(path);
                else Log.Write("Ignored unsafe image library entry: " + entry.StoredName);
            }
            return paths;
        }
        return LegacyPaths(config.ImageFolder);
    }

    internal static List<string> LegacyPaths(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new();
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSupported).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Log.Write("Legacy image directory: " + ex.Message);
            return new();
        }
    }

    /// <summary>将图片写入独立库目录。先复制临时文件再原子重命名；禁止覆盖已有内容。</summary>
    internal static ImportedImage EnsureStored(string source, string originalName, string knownHash)
    {
        if (!IsSupported(source)) throw new InvalidDataException("不支持的图片扩展名：" + source);
        Directory.CreateDirectory(DirectoryPath);
        string hash = HashFile(source);
        if (!string.IsNullOrEmpty(knownHash) && !hash.Equals(knownHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("图片在导入期间发生变化，请重新导入：" + originalName);

        // 即使扩展名不同，只要库内已有相同内容，就复用该文件。
        string? existing = Directory.EnumerateFiles(DirectoryPath, hash + ".*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(p => IsSupported(p) && Path.GetFileNameWithoutExtension(p).Equals(hash, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return new ImportedImage { StoredName = Path.GetFileName(existing), OriginalName = originalName, Sha256 = hash };

        string final = Path.Combine(DirectoryPath, hash + Path.GetExtension(source).ToLowerInvariant());
        string temp = Path.Combine(DirectoryPath, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(source, temp, overwrite: false);
            if (!HashFile(temp).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("复制后图片校验失败：" + originalName);
            try { File.Move(temp, final, overwrite: false); }
            catch (IOException) when (File.Exists(final)) { /* 其他导入已放入同内容文件 */ }
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) { Log.Write("Temp image cleanup: " + e.Message); } }
        return new ImportedImage { StoredName = Path.GetFileName(final), OriginalName = originalName, Sha256 = hash };
    }

    /// <summary>只清理本程序生成的内容寻址文件，绝不删除用户旧目录或其他路径。</summary>
    internal static void PruneUnreferenced(IReadOnlyCollection<ImportedImage> keep)
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return;
            var names = new HashSet<string>(keep.Select(x => x.StoredName), StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(DirectoryPath))
            {
                var probe = new ImportedImage { StoredName = Path.GetFileName(path) };
                if (TryResolve(probe, out _) && !names.Contains(probe.StoredName))
                {
                    try { File.Delete(path); }
                    catch (Exception ex) { Log.Write("Remove unused image: " + ex.Message); }
                }
            }
        }
        catch (Exception ex) { Log.Write("Prune images: " + ex.Message); }
    }
}
