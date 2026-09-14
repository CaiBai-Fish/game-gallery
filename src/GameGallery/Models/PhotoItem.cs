using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GameGallery.Models;

/// <summary>
/// 一张截图。不可变部分在扫描时一次性确定，仅收藏状态可变。
/// </summary>
public sealed class PhotoItem : INotifyPropertyChanged
{
    private bool _isFavorite;
    private string? _thumbnailPath;

    public PhotoItem(
        string filePath,
        string fileName,
        string gameKey,
        string gameName,
        string folderPath,
        long sizeBytes,
        DateTime capturedAt)
    {
        FilePath = filePath;
        FileName = fileName;
        GameKey = gameKey;
        GameName = gameName;
        FolderPath = folderPath;
        SizeBytes = sizeBytes;
        CapturedAt = capturedAt;
        SearchKey = fileName.ToLowerInvariant();
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string GameKey { get; }
    public string GameName { get; }
    public string FolderPath { get; }
    public long SizeBytes { get; }
    public DateTime CapturedAt { get; }

    /// <summary>预计算的小写文件名，用于搜索时避免重复分配。</summary>
    public string SearchKey { get; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteGlyph));
        }
    }

    /// <summary>Segoe Fluent Icons: E735 = 实心星, E734 = 空心星。</summary>
    public string FavoriteGlyph => _isFavorite ? "\uE735" : "\uE734";

    /// <summary>由缩略图服务填充，供查看器直接复用已生成的缩略图（避免重复解码）。</summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (_thumbnailPath == value) return;
            _thumbnailPath = value;
            OnPropertyChanged();
        }
    }

    public string DateText => CapturedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string ShortDateText => CapturedAt.ToString("yyyy-MM-dd");

    public string SizeText => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _ => $"{SizeBytes / (1024.0 * 1024.0):0.##} MB",
    };

    public string ToolTipText => $"{FileName}\n{GameName} · {DateText} · {SizeText}\n{FilePath}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
