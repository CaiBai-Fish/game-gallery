using GameGallery.Models;

namespace GameGallery.Services;

public sealed class LibraryChangedEventArgs : EventArgs
{
    public required string FolderPath { get; init; }
}

/// <summary>
/// 负责把“截图文件夹”变成“照片列表”，并在文件夹发生变化时通知界面刷新。
/// </summary>
public sealed class LibraryService : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif",
    };

    private readonly SettingsService _settings;
    private readonly FavoritesService _favorites;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _debounceLock = new();
    private readonly Dictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public LibraryService(SettingsService settings, FavoritesService favorites)
    {
        _settings = settings;
        _favorites = favorites;
    }

    public IReadOnlyList<LocatedFolder> Folders { get; private set; } = Array.Empty<LocatedFolder>();

    public IReadOnlyList<PhotoItem> Items { get; private set; } = Array.Empty<PhotoItem>();

    /// <summary>定位/扫描过程中的诊断信息，显示在设置面板里。</summary>
    public IReadOnlyList<string> Diagnostics { get; private set; } = Array.Empty<string>();

    public event EventHandler<LibraryChangedEventArgs>? FolderContentChanged;

    public async Task ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var diagnostics = new List<string>();

        var folders = await Task.Run(
            () => GameLocator.Locate(_settings.Current.CustomFolders, GameCatalog.All, progress, ct),
            ct).ConfigureAwait(true);

        Folders = folders;

        var items = new List<PhotoItem>();
        var index = 0;

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Report($"正在读取 {folder.GameName} 截图（{index}/{folders.Count}）…");

            if (!folder.Exists)
            {
                diagnostics.Add($"{folder.GameName}：已识别安装目录，但截图文件夹尚不存在（{folder.ScreenshotPath}）");
                continue;
            }

            List<PhotoItem> found;
            try
            {
                found = await Task.Run(() => EnumerateFolder(folder), ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                diagnostics.Add($"{folder.GameName}：无法读取 {folder.ScreenshotPath}（{ex.GetType().Name}）");
                continue;
            }

            items.AddRange(found);
            diagnostics.Add($"{folder.GameName}：{found.Count} 张 — {folder.ScreenshotPath}（来源：{folder.SourceText}）");
        }

        foreach (var item in items)
            item.IsFavorite = _favorites.Contains(item.FilePath);

        Items = items;
        Diagnostics = diagnostics;
    }

    private static List<PhotoItem> EnumerateFolder(LocatedFolder folder)
    {
        var result = new List<PhotoItem>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System,
        };

        foreach (var path in Directory.EnumerateFiles(folder.ScreenshotPath, "*", options))
        {
            var extension = Path.GetExtension(path);
            if (!SupportedExtensions.Contains(extension)) continue;

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            result.Add(new PhotoItem(
                filePath: info.FullName,
                fileName: info.Name,
                gameKey: folder.GameKey,
                gameName: folder.GameName,
                folderPath: folder.ScreenshotPath,
                sizeBytes: info.Length,
                capturedAt: info.LastWriteTime));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 文件夹监听：截完图立即出现在图库里
    // ------------------------------------------------------------------

    public void StartWatching()
    {
        StopWatching();
        if (!_settings.Current.WatchFolders) return;

        foreach (var folder in Folders)
        {
            if (!folder.Exists) continue;

            try
            {
                var watcher = new FileSystemWatcher(folder.ScreenshotPath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };

                watcher.Created += OnFolderTouched;
                watcher.Deleted += OnFolderTouched;
                watcher.Renamed += OnFolderTouched;
                watcher.Error += (_, _) => { /* 缓冲区溢出等情形下由手动刷新兜底 */ };

                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 某些目录（网络盘/受限目录）无法监听，忽略即可。
            }
        }
    }

    public void StopWatching()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }

        _watchers.Clear();

        lock (_debounceLock)
        {
            foreach (var timer in _debounceTimers.Values) timer.Dispose();
            _debounceTimers.Clear();
        }
    }

    private void OnFolderTouched(object sender, FileSystemEventArgs e)
    {
        var extension = Path.GetExtension(e.FullPath);
        if (!string.IsNullOrEmpty(extension) && !SupportedExtensions.Contains(extension)) return;

        var folder = (sender as FileSystemWatcher)?.Path;
        if (string.IsNullOrEmpty(folder)) return;

        // 防抖：一次截图可能触发多个事件，也可能瞬间写入多张。
        lock (_debounceLock)
        {
            if (_debounceTimers.TryGetValue(folder, out var existing))
            {
                existing.Change(TimeSpan.FromSeconds(1.2), Timeout.InfiniteTimeSpan);
                return;
            }

            var timer = new Timer(
                _ =>
                {
                    lock (_debounceLock)
                    {
                        if (_debounceTimers.Remove(folder, out var t)) t.Dispose();
                    }

                    FolderContentChanged?.Invoke(this, new LibraryChangedEventArgs { FolderPath = folder });
                },
                null,
                TimeSpan.FromSeconds(1.2),
                Timeout.InfiniteTimeSpan);

            _debounceTimers[folder] = timer;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatching();
    }
}
