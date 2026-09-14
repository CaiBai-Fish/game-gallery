namespace GameGallery.Models;

/// <summary>用户手动添加的截图文件夹。</summary>
public sealed class CustomFolder
{
    public string Path { get; set; } = string.Empty;

    /// <summary>为空表示自动识别游戏。</summary>
    public string GameKey { get; set; } = string.Empty;
}

public enum SortMode
{
    NewestFirst = 0,
    OldestFirst = 1,
    NameAscending = 2,
    SizeDescending = 3,
}

public sealed class AppSettings
{
    public List<CustomFolder> CustomFolders { get; set; } = new();

    /// <summary>缩略图边长（像素）。</summary>
    public double ThumbnailSize { get; set; } = 220;

    public SortMode Sort { get; set; } = SortMode.NewestFirst;

    /// <summary>system | light | dark</summary>
    public string Theme { get; set; } = "system";

    public string LastGameKey { get; set; } = "all";

    /// <summary>是否监听截图文件夹变化并自动刷新。</summary>
    public bool WatchFolders { get; set; } = true;

    public AppSettings Clone() => new()
    {
        CustomFolders = CustomFolders.Select(f => new CustomFolder { Path = f.Path, GameKey = f.GameKey }).ToList(),
        ThumbnailSize = ThumbnailSize,
        Sort = Sort,
        Theme = Theme,
        LastGameKey = LastGameKey,
        WatchFolders = WatchFolders,
    };
}

/// <summary>
/// 设置面板里展示的一行“截图文件夹”。
/// 刻意使用普通可读写属性（而非 required/init）：XAML 类型信息生成器需要无参构造与可写属性。
/// </summary>
public sealed class FolderSummary
{
    public string GameKey { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public bool IsManual { get; set; }
    public bool Exists { get; set; }
    public int Count { get; set; }

    public string CountText => Exists ? $"{Count} 张" : "文件夹不存在";

    public string DetailText => Exists
        ? $"{SourceText} · {Count} 张"
        : $"{SourceText} · 文件夹不存在（等游戏第一次截图后会自动出现）";
}
