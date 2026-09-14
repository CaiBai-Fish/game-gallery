using GameGallery.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GameGallery.Services;

/// <summary>
/// 游戏图标。
///
/// 图标不随程序分发（那是米哈游的美术资源），而是直接复用 HoYoPlay 启动器
/// 已经下载到本机的图标文件：%APPDATA%\miHoYo\HYP\&lt;版本&gt;\ico\&lt;gameBiz&gt;.ico
/// 找不到时回退到内置字体图标，因此图标缺失不会影响功能。
/// </summary>
public static class GameIconService
{
    private static readonly Dictionary<string, string?> PathCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static readonly string[] IconBases =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "miHoYo"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cognosphere"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "miHoYo"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cognosphere"),
    };

    /// <summary>
    /// 取得某个游戏的图标元素；找不到图标文件时返回 null，调用方应回退到字体图标。
    /// 必须在 UI 线程上调用。
    /// </summary>
    public static IconElement? CreateIcon(GameDefinition game)
    {
        // 诊断开关：设 GAMEGALLERY_NO_ICONS=1 可强制回退到字体图标，
        // 便于排查某个 .ico 损坏或渲染异常的问题。
        if (Environment.GetEnvironmentVariable("GAMEGALLERY_NO_ICONS") == "1")
            return null;

        var path = FindIconPath(game.BizPrefix);
        if (path is null) return null;

        try
        {
            return new ImageIcon
            {
                Source = new BitmapImage(new Uri(path)),
                Width = 60,
                Height = 60,
                // 图标字形本身底部留白偏多，视觉上会显得偏高，这里往下压一点
                Margin = new Microsoft.UI.Xaml.Thickness(0, 5, 0, 0),
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            };
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or IOException)
        {
            return null;
        }
    }

    private static List<string>? _allIcons;

    /// <summary>扫描一次所有可用的 .ico（整个进程只做一次，避免每次重建标签页都遍历目录）。</summary>
    private static List<string> GetAllIcons()
    {
        lock (Gate)
        {
            if (_allIcons is not null) return _allIcons;
        }

        var found = new List<string>();

        foreach (var baseDir in IconBases)
        {
            var hypDir = Path.Combine(baseDir, "HYP");
            if (!Directory.Exists(hypDir)) continue;

            string[] icoDirs;
            try
            {
                // 覆盖 HYP\<版本>\ico 与 HYP\beta\standalone\<版本>\<游戏>\<哈希>\ico
                icoDirs = Directory.GetDirectories(hypDir, "ico", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var dir in icoDirs)
            {
                try
                {
                    found.AddRange(Directory.GetFiles(dir, "*.ico"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        lock (Gate)
        {
            _allIcons = found;
        }

        return found;
    }

    /// <summary>返回实际使用的图标路径，找不到返回 null。</summary>
    /// <summary>
    /// 直接给出图标的 ImageSource，供普通 Image 元素使用。
    /// 不用 ImageIcon 是因为它不认 Width/Height（大小由图标槽位决定），
    /// 想把图标做大只能自己放一个 Image。
    /// </summary>
    public static Microsoft.UI.Xaml.Media.ImageSource? CreateImageSource(GameDefinition game)
    {
        var path = FindIconPath(game.BizPrefix);
        if (path is null) return null;

        try
        {
            return new BitmapImage(new Uri(path));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or IOException)
        {
            return null;
        }
    }
    public static string? FindIconPath(string bizPrefix)
    {
        if (string.IsNullOrWhiteSpace(bizPrefix)) return null;

        lock (Gate)
        {
            if (PathCache.TryGetValue(bizPrefix, out var cached)) return cached;
        }

        // 优先国服图标，其次任意同前缀的图标。
        var match = GetAllIcons()
            .Where(p => Path.GetFileName(p).StartsWith(bizPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => Path.GetFileName(p).Contains("_cn", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        lock (Gate)
        {
            PathCache[bizPrefix] = match;
        }

        return match;
    }
}
