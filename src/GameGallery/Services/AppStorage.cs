using System.Text.Json;
using System.Text.Json.Serialization;
using GameGallery.Models;

namespace GameGallery.Services;

/// <summary>
/// 应用的持久化存储。
///
/// 默认放在 %LOCALAPPDATA%\GameGallery；如果那里不可写（受限环境、只读配置目录等），
/// 自动回退到程序目录下的 GameGalleryData，实现免安装的可移植模式。
/// 也可以用环境变量 GAMEGALLERY_DATA_DIR 显式指定。
/// 写入采用“先写临时文件再替换”，避免异常退出留下半个文件。
/// </summary>
public static class AppStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string RootDirectory { get; } = ResolveRootDirectory();

    public static string ThumbnailDirectory { get; } = Path.Combine(RootDirectory, "thumbnails");

    public static string SettingsFilePath { get; } = Path.Combine(RootDirectory, "settings.json");

    public static string FavoritesFilePath { get; } = Path.Combine(RootDirectory, "favorites.json");

    public static string LogFilePath { get; } = Path.Combine(RootDirectory, "startup.log");

    /// <summary>true 表示没能使用 %LOCALAPPDATA%，数据放在了程序目录旁边。</summary>
    public static bool IsPortable { get; private set; }

    private static string ResolveRootDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("GAMEGALLERY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath) && IsUsable(overridePath))
            return overridePath;

        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameGallery");

        if (IsUsable(roaming)) return roaming;

        IsPortable = true;
        return Path.Combine(AppContext.BaseDirectory, "GameGalleryData");
    }

    private static bool IsUsable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static void EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(ThumbnailDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存储不可用时不应让应用直接崩掉，后续操作各自降级。
        }
    }

    public static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 损坏的配置文件不应让应用无法启动。
            TryQuarantine(path);
            return null;
        }
    }

    public static bool WriteJson<T>(string path, T value)
    {
        try
        {
            EnsureCreated();
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, null, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
                {
                    // FAT/exFAT/网络盘上 File.Replace 不可用，退化成“复制 + 删除”。
                    File.Copy(temp, path, overwrite: true);
                    File.Delete(temp);
                }
            }
            else
            {
                File.Move(temp, path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // 保存失败不应影响正在进行的浏览会话，但要让调用方知道。
            return false;
        }
    }

    private static void TryQuarantine(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class SettingsService
{
    private AppSettings _current = new();

    public AppSettings Current => _current;

    public void Load() => _current = AppStorage.ReadJson<AppSettings>(AppStorage.SettingsFilePath) ?? new AppSettings();

    /// <summary>保存设置；返回 false 表示写盘失败（设置仍留在内存里）。</summary>
    public bool Save() => AppStorage.WriteJson(AppStorage.SettingsFilePath, _current);
}

/// <summary>
/// 收藏集合。以规范化后的完整路径为键，独立于缩略图缓存。
/// 内存里的集合是唯一事实来源：批量修改时用 Set/Remove 再统一 Flush，
/// 避免一次多选就写出成百上千次文件。
/// </summary>
public sealed class FavoritesService
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    public void Load()
    {
        _paths.Clear();
        _dirty = false;

        var stored = AppStorage.ReadJson<List<string>>(AppStorage.FavoritesFilePath);
        if (stored is null) return;

        foreach (var p in stored)
        {
            if (!string.IsNullOrWhiteSpace(p))
                _paths.Add(Normalize(p));
        }
    }

    public bool Contains(string path) => _paths.Contains(Normalize(path));

    /// <summary>设置收藏状态但先不写盘（配合 Flush 做批量操作）。</summary>
    public bool Set(string path, bool isFavorite)
    {
        var key = Normalize(path);
        var changed = isFavorite ? _paths.Add(key) : _paths.Remove(key);
        if (changed) _dirty = true;
        return changed;
    }

    public bool Toggle(string path)
    {
        var key = Normalize(path);
        if (_paths.Remove(key))
        {
            _dirty = true;
            Flush();
            return false;
        }

        _paths.Add(key);
        _dirty = true;
        Flush();
        return true;
    }

    public void Remove(string path)
    {
        if (_paths.Remove(Normalize(path)))
        {
            _dirty = true;
            Flush();
        }
    }

    public IReadOnlyCollection<string> All => _paths;

    /// <summary>把内存里的收藏写回磁盘；返回 false 表示写盘失败。</summary>
    public bool Flush()
    {
        if (!_dirty) return true;

        var ok = AppStorage.WriteJson(
            AppStorage.FavoritesFilePath,
            _paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());

        if (ok) _dirty = false;
        return ok;
    }
}
