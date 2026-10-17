using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BilibiliLiveAssistant.App;

// An explicit, bounded asset store. The HTTP server never accepts a filesystem path.
internal static partial class ObsThemeAssets
{
    internal const int MaxFileBytes = 8 * 1024 * 1024;
    private const long MaxTotalBytes = 32 * 1024 * 1024;
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilibiliLiveAssistant", "obs-assets");
    [GeneratedRegex("^[0-9a-f]{64}\\.(png|ttf|otf|woff2)$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetName();
    internal static IReadOnlyList<string> List() => Directory.Exists(Root)
        ? Directory.EnumerateFiles(Root).Select(Path.GetFileName).OfType<string>().Where(n => AssetName().IsMatch(n)).Order().Take(64).ToArray() : [];
    internal static async Task<string> ImportAsync(string source)
    {
        var info = new FileInfo(source);
        if (info.Length is <= 0 or > 4 * 1024 * 1024) throw new IOException("素材需要小于 4 MiB。");
        var bytes = await File.ReadAllBytesAsync(source);
        if (bytes.Length > 4 * 1024 * 1024) throw new IOException("素材超出大小限制。");
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg")
        {
            bytes = await Task.Run(() =>
            {
                using var input = new MemoryStream(bytes, false);
                using var image = System.Drawing.Image.FromStream(input, false, true);
                if (image.Width > 2048 || image.Height > 2048 || (long)image.Width * image.Height > 4 * 1024 * 1024)
                    throw new IOException("图片最大为 2048 × 2048 像素。");
                using var output = new MemoryStream(); image.Save(output, System.Drawing.Imaging.ImageFormat.Png); return output.ToArray();
            });
            extension = ".png";
        }
        else
        {
            var valid = bytes.Length >= 4 && extension switch
            {
                ".ttf" => bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 1, 0, 0 }),
                ".otf" => bytes.AsSpan(0, 4).SequenceEqual("OTTO"u8),
                ".woff2" => bytes.AsSpan(0, 4).SequenceEqual("wOF2"u8),
                _ => false,
            };
            if (!valid) throw new IOException("只支持 PNG、JPEG 和有效的 TTF／OTF／WOFF2 字体。");
        }
        if (bytes.Length > MaxFileBytes) throw new IOException("处理后的素材超出 8 MiB 限制。");
        Directory.CreateDirectory(Root);
        var name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension;
        var target = Path.Combine(Root, name);
        if (File.Exists(target)) return name;
        var files = new DirectoryInfo(Root).EnumerateFiles().ToArray();
        if (files.Length >= 64 || files.Sum(f => f.Length) + bytes.Length > MaxTotalBytes)
            throw new IOException("素材已达到 64 项或 32 MiB 上限，请先在素材目录中清理不用的文件。");
        await using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true);
        try { await file.WriteAsync(bytes); }
        catch { file.Close(); File.Delete(target); throw; }
        return name;
    }
    internal static async Task<(byte[] Bytes, string Type)?> ReadAsync(string name, CancellationToken cancellation)
    {
        if (!AssetName().IsMatch(name)) return null;
        var path = Path.Combine(Root, name);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (file.Length is <= 0 or > MaxFileBytes) return null;
        var bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, cancellation);
        return (bytes, Path.GetExtension(name) switch { ".png" => "image/png", ".ttf" => "font/ttf", ".otf" => "font/otf", _ => "font/woff2" });
    }
    internal static string DirectoryPath { get { Directory.CreateDirectory(Root); return Root; } }
}
