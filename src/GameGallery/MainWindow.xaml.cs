using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using GameGallery.Models;
using GameGallery.Services;
using GameGallery.Views;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace GameGallery;

public sealed partial class MainWindow : Window
{
    /// <summary>导航栏图标边长（展开与折叠都用同一个尺寸）。</summary>
    private const double NavIconSize = 28;

    private readonly SettingsService _settings = new();
    private readonly FavoritesService _favorites = new();
    private readonly ThumbnailService _thumbnails = new();
    private readonly LibraryService _library;

    /// <summary>当前列表中照片，直接作为 GridView 的数据源以便增量更新。</summary>
    private readonly ObservableCollection<PhotoItem> _visibleItems = new();

    /// <summary>每个图块正在进行的缩略图加载，图块被回收时用来取消。</summary>
    private readonly Dictionary<Image, CancellationTokenSource> _thumbnailTokens = new();

    private readonly List<string> _tabKeys = new();

    /// <summary>
    /// 导航项里可切换的"文字"部分。折叠/展开只调整可见性，绝不重建导航项：
    /// 重建会让 NavigationView 重新播放一次选中指示条动画，而且展开时能否恢复文字
    /// 取决于重建那一刻 DisplayMode 是否已更新（竞态），会永久只剩图标。
    /// </summary>
    private readonly List<(NavigationViewItem Item, StackPanel Row, TextBlock Text)> _navTextRows = new();

    private List<PhotoItem> _allItems = new();

    private string _currentTabKey = "all";
    private string _searchText = string.Empty;

    private bool _suppressTabChange;
    private bool _suppressSelectionSync;
    private bool _isScanning;
    private bool _rescanRequested;
    private bool _loaded;
    private bool _dialogOpen;

    /// <summary>正在检查更新（防止连点）。</summary>
    private bool _checkingUpdate;

    /// <summary>检查更新得到的发布页地址。</summary>
    private string? _releaseUrl;

    /// <summary>检查更新发现的新版本号（去掉 v 前缀），用于「下载并安装」。</summary>
    private string? _pendingVersion;

    /// <summary>Release 上的原始标签（可能是 v0.1.2），拼下载地址时要用它。</summary>
    private string? _pendingTag;

    /// <summary>右键菜单作用的那一张照片（弹出菜单里拿不到 DataContext，必须在这里记下来）。</summary>
    private PhotoItem? _contextItem;

    /// <summary>
    /// 导航栏当前是否处于"收起"状态，以及这个值是否已经应用过。
    /// 刻意不用 NavigationView 自带的开合状态去算：PaneOpened/PaneClosed/DisplayModeChanged
    /// 触发时 IsPaneOpen 还是旧值，据此计算会错一拍（实测展开后文字要 ~1.2s 才出现）。
    /// 改由布局驱动（见 OnNavLayoutUpdated），布局完成时属性一定已经生效。
    /// </summary>
    private bool _navCompactApplied;
    private bool _navCompactValid;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;

    public MainWindow()
    {
        InitializeComponent();

        // Slider 的取值范围必须在赋值 Value 之前设定，且 Minimum 不能大于当前 Maximum。
        SizeSlider.Maximum = 420;
        SizeSlider.Minimum = 120;

        // 增量更新用的集合直接作为网格数据源，这样刷新时不会丢失滚动位置。
        PhotoGrid.ItemsSource = _visibleItems;

        Title = "游戏截图图库";

        _library = new LibraryService(_settings, _favorites);
        Viewer.Thumbnails = _thumbnails;

        ConfigureWindow();
        ApplyWindowIcon();

        _settings.Load();
        _favorites.Load();

        _currentTabKey = string.IsNullOrWhiteSpace(_settings.Current.LastGameKey) ? "all" : _settings.Current.LastGameKey;

        ApplySettingsToUi();
        VersionText.Text = UpdateService.CurrentVersion;
        HookViewerEvents();

        // 导航栏开合只由布局驱动同步（不重建导航项）：见 OnNavLayoutUpdated。
        Nav.LayoutUpdated += OnNavLayoutUpdated;
        _library.FolderContentChanged += OnFolderContentChanged;
        RootGrid.Loaded += OnRootGridLoaded;
        Closed += OnWindowClosed;
    }

    // ------------------------------------------------------------------
    // 属性（供 x:Bind 使用）
    // ------------------------------------------------------------------

    public ObservableCollection<FolderSummary> FolderSummaries { get; } = new();

    public ObservableCollection<string> Diagnostics { get; } = new();

    // ------------------------------------------------------------------
    // 启动
    // ------------------------------------------------------------------

    /// <summary>
    /// 设置窗口图标（标题栏、任务栏按钮、Alt+Tab 都用它）。
    /// 未打包的 WinUI 3 应用不会自动继承 EXE 内嵌的图标资源，必须显式设置。
    /// 更新日志窗口也走同一套，实现在 WindowIcon 里。
    /// </summary>
    private void ApplyWindowIcon() => WindowIcon.Apply(AppWindow);

    private void ConfigureWindow()
    {
        try
        {
            AppWindow.Resize(new SizeInt32(1440, 920));

            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (area is not null)
            {
                var x = area.WorkArea.X + (area.WorkArea.Width - 1440) / 2;
                var y = area.WorkArea.Y + (area.WorkArea.Height - 920) / 2;
                AppWindow.Move(new PointInt32(Math.Max(0, x), Math.Max(0, y)));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
        }

        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
                RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TypeLoadException)
        {
        }
    }

    private void ApplySettingsToUi()
    {
        SizeSlider.Value = Math.Clamp(_settings.Current.ThumbnailSize, SizeSlider.Minimum, SizeSlider.Maximum);
        ThemeBox.SelectedIndex = _settings.Current.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        SortBox.SelectedIndex = (int)_settings.Current.Sort;
        WatchToggle.IsOn = _settings.Current.WatchFolders;
        ApplyTheme();
    }

    private async void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            ApplyTileSize();
            await RefreshAsync();
            App.Log($"首次扫描完成：{_allItems.Count} 张截图，{_library.Folders.Count} 个文件夹");
        }
        catch (Exception ex)
        {
            App.Log("首次扫描失败：" + ex);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _saveTimer?.Stop();
        _library.Dispose();

        foreach (var cts in _thumbnailTokens.Values) cts.Dispose();
        _thumbnailTokens.Clear();

        _settings.Save();
    }

    // ------------------------------------------------------------------
    // 扫描与刷新
    // ------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_isScanning)
        {
            _rescanRequested = true;
            return;
        }

        _isScanning = true;
        BusyRing.IsActive = true;

        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            await _library.ScanAsync(progress);

            _allItems = _library.Items.ToList();

            // 收藏以内存里的集合为准；这里不再从磁盘重读，否则一次保存失败
            // 会被下一次自动刷新悄悄回滚。
            foreach (var item in _allItems)
                item.IsFavorite = _favorites.Contains(item.FilePath);

            RebuildTabs();
            ApplyFilter();
            UpdateFolderSummaries();
            UpdateDiagnostics();
            ResyncViewerAfterRefresh();
            _library.StartWatching();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _isScanning = false;
            BusyRing.IsActive = false;
        }

        if (_rescanRequested)
        {
            _rescanRequested = false;
            await RefreshAsync();
        }
    }

    /// <summary>
    /// 刷新会重排 _visibleItems，而查看器保存的是“正在显示的那张照片”。
    /// 这里把它的下标重新对齐；照片已经不在当前筛选结果里（被删掉或换了标签页）就关掉查看器。
    /// 画面上显示的图和 Viewer.Current 必须始终是同一张，
    /// 否则按删除/收藏会作用到用户没看到的那张截图上。
    /// </summary>
    private void ResyncViewerAfterRefresh()
    {
        if (Viewer.Visibility != Visibility.Visible) return;

        var current = Viewer.Current;
        var resolved = ResolveVisibleByPath(current);

        if (resolved is null)
        {
            // 这张照片已经不在当前筛选结果里了（被删掉，或者换了标签页）。
            CloseViewer();
            return;
        }

        var index = _visibleItems.IndexOf(resolved);
        if (index < 0)
        {
            CloseViewer();
            return;
        }

        Viewer.SetItems(_visibleItems, index);
    }

    /// <summary>
    /// 每次扫描都会重新构造 PhotoItem，所以跨刷新定位照片必须按路径，不能按对象身份。
    /// </summary>
    private PhotoItem? ResolveVisibleByPath(PhotoItem? item)
    {
        if (item is null) return null;

        foreach (var candidate in _visibleItems)
        {
            if (string.Equals(candidate.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private void RebuildTabs()
    {
        _suppressTabChange = true;

        try
        {
            Nav.MenuItems.Clear();
            _tabKeys.Clear();
            _navTextRows.Clear();

            void Add(string key, string text, string glyph)
            {
                var item = new NavigationViewItem { Tag = key };
                AutomationProperties.SetName(item, text);
                ToolTipService.SetToolTip(item, text);

                var game = GameCatalog.ByKey(key);
                var source = game is null ? null : GameIconService.CreateImageSource(game);

                if (game is not null && source is null)
                    App.Log($"未找到 {game.DisplayName} 的图标文件（{game.BizPrefix}），已回退到字体图标");

                // 图标尺寸在展开/折叠下完全一致，折叠时只是隐藏文字。
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                if (source is not null)
                {
                    row.Children.Add(new Image
                    {
                        Source = source,
                        Width = NavIconSize,
                        Height = NavIconSize,
                        Stretch = Stretch.Uniform,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                else
                {
                    row.Children.Add(new FontIcon
                    {
                        Glyph = glyph,
                        FontSize = NavIconSize - 6,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }

                var label = new TextBlock
                {
                    Text = text,
                    FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(label);
                _navTextRows.Add((item, row, label));

                item.Content = row;

                Nav.MenuItems.Add(item);
                _tabKeys.Add(key);
            }

            Add("all", $"全部 {_allItems.Count}", "\uE8B9");

            foreach (var game in GameCatalog.All)
            {
                if (!_library.Folders.Any(f => f.GameKey == game.Key)) continue;
                var count = _allItems.Count(i => i.GameKey == game.Key);
                Add(game.Key, $"{game.DisplayName} {count}", game.Glyph);
            }

            if (_library.Folders.Any(f => f.GameKey == GameLocator.UnknownGame.Key))
            {
                var other = _allItems.Count(i => i.GameKey == GameLocator.UnknownGame.Key);
                Add(GameLocator.UnknownGame.Key, $"其他截图 {other}", GameLocator.UnknownGame.Glyph);
            }

            var favoriteCount = _allItems.Count(i => i.IsFavorite);
            Add("favorites", $"收藏 {favoriteCount}", "\uE735");

            if (!_tabKeys.Contains(_currentTabKey))
                _currentTabKey = "all";

            Nav.SelectedItem = Nav.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, _currentTabKey, StringComparison.Ordinal))
                ?? Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();

            ApplyNavDensity();
        }
        finally
        {
            _suppressTabChange = false;
        }
    }

    /// <summary>
    /// 按窗格开合切换导航项的文字。图标尺寸在两种状态下完全一致，收起时只隐藏文字。
    /// 不重建导航项、不改显示模式，所以不会出现指示条重播或文字晚一拍的现象。
    /// </summary>
    private void ApplyNavDensity()
    {
        var compact = !Nav.IsPaneOpen;

        _navCompactApplied = compact;
        _navCompactValid = true;

        foreach (var (_, row, text) in _navTextRows)
        {
            row.Spacing = compact ? 0 : 14;
            row.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// 状态同步的可靠来源。PaneOpened/PaneClosed/DisplayModeChanged 触发时 IsPaneOpen 还是旧值，
    /// 按它算必然错一拍（实测展开后文字要 ~1.2s 才出现，看起来就是"刷新了一下"）。
    /// 布局完成时属性一定已经生效，所以改由布局驱动；只在状态真的变了时才动手，避免布局自激。
    /// </summary>
    private void OnNavLayoutUpdated(object? sender, object e)
    {
        if (!_navCompactValid || (!Nav.IsPaneOpen) != _navCompactApplied)
        {
            ApplyNavDensity();
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressTabChange) return;
        if (args.SelectedItem is not NavigationViewItem { Tag: string key }) return;
        if (string.Equals(key, _currentTabKey, StringComparison.Ordinal)) return;

        _currentTabKey = key;
        _settings.Current.LastGameKey = key;
        ScheduleSave();

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<PhotoItem> query = _allItems;

        if (_currentTabKey == "favorites")
        {
            query = query.Where(i => i.IsFavorite);
        }
        else if (_currentTabKey != "all")
        {
            query = query.Where(i => string.Equals(i.GameKey, _currentTabKey, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            query = query.Where(i => i.SearchKey.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
        }

        var sorted = _settings.Current.Sort switch
        {
            SortMode.OldestFirst => query.OrderBy(i => i.CapturedAt).ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase),
            SortMode.NameAscending => query.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase),
            SortMode.SizeDescending => query.OrderByDescending(i => i.SizeBytes),
            _ => query.OrderByDescending(i => i.CapturedAt).ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase),
        };

        SyncVisible(sorted.ToList());
        UpdateStatus();
        UpdateEmptyState();
    }

    /// <summary>
    /// 增量同步 ObservableCollection，避免每次刷新都重置 ItemsSource 而丢失滚动位置。
    /// </summary>
    private void SyncVisible(IReadOnlyList<PhotoItem> target)
    {
        var wanted = new HashSet<PhotoItem>(target);

        for (var i = _visibleItems.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(_visibleItems[i]))
                _visibleItems.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            var item = target[i];
            if (i < _visibleItems.Count && ReferenceEquals(_visibleItems[i], item)) continue;

            var existing = _visibleItems.IndexOf(item);
            if (existing >= 0) _visibleItems.Move(existing, i);
            else _visibleItems.Insert(i, item);
        }
    }

    private void UpdateFolderSummaries()
    {
        FolderSummaries.Clear();

        foreach (var folder in _library.Folders)
        {
            var count = _allItems.Count(i => string.Equals(i.FolderPath, folder.ScreenshotPath, StringComparison.OrdinalIgnoreCase));

            FolderSummaries.Add(new FolderSummary
            {
                GameKey = folder.GameKey,
                GameName = folder.GameName,
                Path = folder.ScreenshotPath,
                SourceText = folder.SourceText,
                IsManual = folder.Source == DiscoverySource.Manual,
                Exists = folder.Exists,
                Count = count,
            });
        }
    }

    private void UpdateDiagnostics()
    {
        Diagnostics.Clear();

        foreach (var line in _library.Diagnostics)
            Diagnostics.Add(line);

        if (_library.Folders.Count == 0)
        {
            Diagnostics.Add("没有定位到任何游戏安装目录。");
            Diagnostics.Add("说明：原神 / 星穹铁道 / 绝区零 由 HoYoPlay 启动器安装，安装目录只写在启动器自己的状态库里，");
            Diagnostics.Add("常规注册表项（HKCU\\Software\\miHoYo\\<游戏名>）里只有画质与账号设置，并没有安装路径。");
            Diagnostics.Add("因此本工具依次尝试：HoYoPlay 状态库 → 卸载注册表项 → 磁盘特征扫描 → 你手动添加的文件夹。");
        }
    }

    private void UpdateStatus()
    {
        var selected = PhotoGrid.SelectedItems.OfType<PhotoItem>().ToList();
        CountText.Text = $"{_visibleItems.Count} 张";

        if (selected.Count > 0)
        {
            var bytes = selected.Sum(i => i.SizeBytes);
            StatusText.Text = $"已选 {selected.Count} 张 · {FormatSize(bytes)}";
        }
        else if (_library.Folders.Count == 0)
        {
            StatusText.Text = "未找到游戏截图文件夹，请在“设置”中查看定位详情或手动添加。";
        }
        else
        {
            var folders = string.Join("   ", _library.Folders.Select(f => $"{f.GameName} → {f.ScreenshotPath}"));
            StatusText.Text = $"共 {_allItems.Count} 张 · {folders}";
        }

        UpdateActionButtons(selected);
    }

    private void UpdateActionButtons(IReadOnlyList<PhotoItem> selected)
    {
        var has = selected.Count > 0;
        FavoriteActionButton.IsEnabled = has;
        CopyActionButton.IsEnabled = has;
        RevealActionButton.IsEnabled = has;
        OpenActionButton.IsEnabled = has;
        DeleteActionButton.IsEnabled = has;
        SelectAllButton.IsEnabled = _visibleItems.Count > 0;

        var allFavorite = has && selected.All(i => i.IsFavorite);
        if (FavoriteActionButton.Content is FontIcon icon)
            icon.Glyph = allFavorite ? "\uE735" : "\uE734";
    }

    private void UpdateEmptyState()
    {
        if (_visibleItems.Count > 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyState.Visibility = Visibility.Visible;

        if (_library.Folders.Count == 0)
        {
            EmptyStateText.Text =
                "没有找到任何游戏截图文件夹。\n\n" +
                "请确认原神 / 崩坏：星穹铁道 / 绝区零 已经安装，\n" +
                "然后在右上角“设置”里点“添加”手动指定截图文件夹。";
        }
        else if (!string.IsNullOrWhiteSpace(_searchText))
        {
            EmptyStateText.Text = $"没有匹配“{_searchText}”的截图。";
        }
        else
        {
            EmptyStateText.Text = "这个分类下还没有截图。\n\n在游戏里按截图快捷键，回到这里就会自动出现。";
        }
    }

    private async void OnFolderContentChanged(object? sender, LibraryChangedEventArgs e)
    {
        // FileSystemWatcher 的事件在线程池线程上触发。
        // 目前是整库重扫（对几千张的规模仍然很快），所以把触发来源写进日志便于排查。
        DispatcherQueue.TryEnqueue(async () =>
        {
            App.Log($"截图目录有变化，重新扫描：{e.FolderPath}");
            await RefreshAsync();
        });

        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 缩略图
    // ------------------------------------------------------------------

    private void ApplyTileSize()
    {
        if (PhotoGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;

        var size = Math.Clamp(_settings.Current.ThumbnailSize, 96, 512);
        panel.ItemWidth = size;
        panel.ItemHeight = size + 26;
    }

    private void OnThumbnailSizeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _settings.Current.ThumbnailSize = e.NewValue;
        ApplyTileSize();
        ScheduleSave();
    }

    /// <summary>
    /// 虚拟化网格的缩略图加载入口。用 ContainerContentChanging 而不是 Loaded 事件，
    /// 因为容器会被回收复用，Loaded 不会重新触发。
    /// </summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var image = FindTileImage(args.ItemContainer?.ContentTemplateRoot);
        if (image is null) return;

        if (args.InRecycleQueue)
        {
            // 图块被回收了：取消还在排队的缩略图生成，别为大图库白做几千次解码。
            CancelThumbnailLoad(image);
            image.Tag = null;
            image.Source = null;
            return;
        }

        if (args.Phase == 0)
        {
            CancelThumbnailLoad(image);
            image.Tag = null;
            image.Source = null;

            // 延迟到容器稳定后再解码，快速滚动时不会为看不见的图白白生成缩略图。
            args.RegisterUpdateCallback(1, OnContainerContentChanging);
            args.Handled = true;
            return;
        }

        if (args.Phase == 1 && args.Item is PhotoItem item)
        {
            args.Handled = true;
            LoadThumbnailAsync(image, item);
        }
    }

    private void CancelThumbnailLoad(Image image)
    {
        if (!_thumbnailTokens.Remove(image, out var cts)) return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async void LoadThumbnailAsync(Image image, PhotoItem item)
    {
        image.Tag = item;

        var cts = new CancellationTokenSource();
        _thumbnailTokens[image] = cts;

        try
        {
            var raster = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            var decodeWidth = ThumbnailService.BucketSize(_settings.Current.ThumbnailSize, raster);

            var bitmap = await _thumbnails.GetThumbnailAsync(item, decodeWidth, cts.Token);

            // 容器可能已经被回收并绑定到别的照片上了。
            if (cts.IsCancellationRequested || !ReferenceEquals(image.Tag, item)) return;

            image.Source = bitmap;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or COMException or OperationCanceledException)
        {
            // 图块被回收、图片损坏、解码组件报错，都属于正常的降级场景：留空即可。
            App.Log($"缩略图加载失败（{item.FileName}）：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (_thumbnailTokens.TryGetValue(image, out var current) && ReferenceEquals(current, cts))
                _thumbnailTokens.Remove(image);
            cts.Dispose();
        }
    }

    private static Image? FindTileImage(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is Image image && image.Name == "TileImage") return image;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindTileImage(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 网格交互
    // ------------------------------------------------------------------

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();

        if (_suppressSelectionSync) return;
        if (Viewer.Visibility != Visibility.Visible) return;

        // 用户直接点网格时同步查看器。
        if (FirstSelected is PhotoItem item)
        {
            var index = _visibleItems.IndexOf(item);
            if (index >= 0 && !ReferenceEquals(Viewer.Current, item))
                Viewer.SetItems(_visibleItems, index);
        }
    }

    private void OnGridDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FirstSelected is not PhotoItem item) return;

        var index = _visibleItems.IndexOf(item);
        if (index < 0) return;

        OpenViewer(index);
    }

    /// <summary>
    /// GridViewItem 会把 Enter 当成“调用”自己消费掉，导致 RootGrid 上的 Enter 快捷键收不到，
    /// 所以在隧道阶段（PreviewKeyDown）先接住。
    /// </summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        if (Viewer.Visibility == Visibility.Visible) return;
        if (FirstSelected is not PhotoItem item) return;

        var index = _visibleItems.IndexOf(item);
        if (index < 0) return;

        e.Handled = true;
        OpenViewer(index);
    }

    private void OpenViewer(int index)
    {
        if (_visibleItems.Count == 0) return;

        // 放大动画从这张缩略图的位置和尺寸开始。
        // 这里只记下元素：矩形要等查看器可见、布局完成之后再算，否则坐标是错的。
        Viewer.EntranceSourceElement = FindTileImageFor(_visibleItems[index]);

        Viewer.SetItems(_visibleItems, index);
        Viewer.Visibility = Visibility.Visible;
        Viewer.Focus(FocusState.Programmatic);
        Viewer.PlayOpenAnimation();
    }

    /// <summary>找到某个照片当前对应的缩略图图片元素（可能因为虚拟化而不存在）。</summary>
    private Image? FindTileImageFor(PhotoItem item)
    {
        var container = PhotoGrid.ContainerFromItem(item) as GridViewItem;
        var image = FindTileImage(container?.ContentTemplateRoot);
        return image is not null && image.ActualWidth > 1 ? image : null;
    }

    /// <summary>右键点图块时，先把这一张选中，菜单里的操作才有明确的作用对象。</summary>
    private void OnTileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = ResolveContextItem(sender) ?? ResolveContextItem(e.OriginalSource);
        if (item is null) return;

        _contextItem = item;

        if (!PhotoGrid.SelectedItems.Contains(item))
            SelectSingle(item);

        UpdateStatus();
    }

    /// <summary>
    /// 取右键菜单的作用对象。
    /// ContextFlyout 的内容不在可视树里，拿不到 DataContext，
    /// 所以以右键那一刻记下的 _contextItem 为准，DataContext 只作为补充。
    /// </summary>
    private PhotoItem? ResolveContextItem(object? sender)
    {
        // 模板根 Grid 上的 Tag 是 x:Bind 绑定的数据项（DataContext 在这里是空的）。
        if (sender is FrameworkElement { Tag: PhotoItem tagged }) return tagged;

        if (sender is FrameworkElement { DataContext: PhotoItem fromContext }) return fromContext;

        // 兜底：从命中的元素往上找 GridViewItem，再向控件反查数据项。
        var node = sender as DependencyObject;
        while (node is not null)
        {
            if (node is GridViewItem container)
                return PhotoGrid.ItemFromContainer(container) as PhotoItem;

            node = VisualTreeHelper.GetParent(node);
        }

        return _contextItem;
    }

    /// <summary>右键菜单的作用对象：优先用多选结果，否则只用被右键的那一张。</summary>
    private IReadOnlyList<PhotoItem> ContextTargets(PhotoItem fallback)
    {
        var selected = SelectedItems;
        return selected.Contains(fallback) ? selected : new[] { fallback };
    }

    private void OnTileRevealClick(object sender, RoutedEventArgs e)
    {
        var item = ResolveContextItem(sender);
        if (item is null) return;

        var targets = ContextTargets(item);
        if (targets.Count > 0) ShellInterop.RevealInExplorer(targets[0].FilePath);
    }

    private void OnTileCopyClick(object sender, RoutedEventArgs e)
    {
        var item = ResolveContextItem(sender);
        if (item is null) return;
        _ = CopyAsync(ContextTargets(item));
    }

    private void CloseViewer() => _ = CloseViewerAsync();

    private async Task CloseViewerAsync()
    {
        var current = ResolveVisibleByPath(Viewer.Current);

        // 先把缩略图滚进视野并等它实例化，才能拿到准确的终点矩形。
        Image? tileImage = null;
        if (current is not null)
        {
            _suppressSelectionSync = true;
            try
            {
                SelectSingle(current);
                PhotoGrid.ScrollIntoView(current, ScrollIntoViewAlignment.Default);
            }
            finally
            {
                _suppressSelectionSync = false;
            }

            UpdateStatus();
            tileImage = await WaitForTileImageAsync(current);
        }

        var target = tileImage is null ? null : Viewer.GetSourceRectFor(tileImage);

        if (target is null)
        {
            FinishClose(current);
            return;
        }

        // 动画结束后才真正隐藏查看器
        Viewer.PlayExitAnimation(target.Value, () => FinishClose(current));
    }

    private void FinishClose(PhotoItem? current)
    {
        Viewer.Close();
        UpdateStatus();
        PhotoGrid.Focus(FocusState.Programmatic);
    }

    /// <summary>等虚拟化网格把缩略图容器实例化出来，再把动画接上去。</summary>
    private async Task<Image?> WaitForTileImageAsync(PhotoItem item)
    {
        for (var i = 0; i < 12; i++)
        {
            var image = FindTileImageFor(item);
            if (image is not null) return image;
            await Task.Delay(30);
        }

        return null;
    }

    private void HookViewerEvents()
    {
        Viewer.CloseRequested += (_, _) => _ = CloseViewerAsync();

        Viewer.FavoriteToggleRequested += async (_, _) =>
        {
            var item = Viewer.Current;
            if (item is null) return;
            await ToggleFavoriteAsync(new[] { item });
            Viewer.RefreshChrome();
        };

        Viewer.CopyRequested += async (_, _) =>
        {
            var item = Viewer.Current;
            if (item is null) return;
            await CopyAsync(new[] { item });
        };

        Viewer.RevealRequested += (_, _) =>
        {
            var item = Viewer.Current;
            if (item is not null) ShellInterop.RevealInExplorer(item.FilePath);
        };

        Viewer.DeleteRequested += async (_, _) => await DeleteFromViewerAsync();

        Viewer.CurrentChanged += (_, _) => SyncGridSelectionToViewer();
    }

    private void SyncGridSelectionToViewer()
    {
        var item = ResolveVisibleByPath(Viewer.Current);
        if (item is null) return;

        _suppressSelectionSync = true;
        try
        {
            SelectSingle(item);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        UpdateStatus();
    }

    // ------------------------------------------------------------------
    // 选择与操作
    // ------------------------------------------------------------------

    private IReadOnlyList<PhotoItem> SelectedItems => PhotoGrid.SelectedItems.OfType<PhotoItem>().ToList();

    /// <summary>
    /// Extended 选择模式下 GridView.SelectedItem 并不可靠（单选模式下才保证有值），
    /// 因此统一从 SelectedItems 取第一个。
    /// </summary>
    private PhotoItem? FirstSelected => PhotoGrid.SelectedItems.OfType<PhotoItem>().FirstOrDefault();

    private void SelectSingle(PhotoItem item)
    {
        PhotoGrid.SelectedItems.Clear();
        PhotoGrid.SelectedItems.Add(item);
    }

    private async Task ToggleFavoriteAsync(IReadOnlyList<PhotoItem> items)
    {
        if (items.Count == 0) return;

        // 只要并非全部已收藏，就整体设为收藏；否则整体取消。
        var makeFavorite = !items.All(i => i.IsFavorite);

        foreach (var item in items)
        {
            if (item.IsFavorite == makeFavorite) continue;
            _favorites.Set(item.FilePath, makeFavorite);
            item.IsFavorite = makeFavorite;
        }

        // 一次性落盘，避免多选一千张就写一千次文件。
        if (!_favorites.Flush())
            StatusText.Text = "收藏已更新，但写入收藏文件失败（设置目录不可写）。";

        // 收藏状态变化后“收藏”标签页的数量与内容都需要更新。
        RebuildTabs();
        ApplyFilter();

        await Task.CompletedTask;
    }

    private async Task CopyAsync(IReadOnlyList<PhotoItem> items)
    {
        if (items.Count == 0) return;

        var ok = await ShellInterop.CopyToClipboardAsync(items.Select(i => i.FilePath).ToList());
        StatusText.Text = ok
            ? $"已复制 {items.Count} 张到剪贴板"
            : "复制失败";
    }

    private async Task DeleteFromViewerAsync()
    {
        var item = Viewer.Current;
        if (item is null) return;

        // 先把要删的路径定下来。确认对话框是模态的，但后台的自动刷新
        // 仍然可能在这期间重排列表，所以确认之后必须按这份快照删。
        var targetPath = item.FilePath;
        var index = _visibleItems.IndexOf(item);

        var confirmed = await ConfirmAsync(
            $"要把这张截图移到回收站吗？\n\n{item.FileName}",
            "移到回收站");

        if (!confirmed)
        {
            Viewer.Focus(FocusState.Programmatic);
            return;
        }

        var deleted = ShellInterop.DeleteToRecycleBin(new[] { targetPath });
        if (deleted.Count == 0)
        {
            await ShowMessageAsync("删除失败", "无法把文件移到回收站，可能已被占用或权限不足。");
            Viewer.Focus(FocusState.Programmatic);
            return;
        }

        RemoveItemsFromLibrary(deleted);

        if (_visibleItems.Count == 0)
        {
            CloseViewer();
            return;
        }

        // 删除后列表可能已经变了，按当前位置重新对齐。
        var next = index < 0 ? 0 : Math.Clamp(index, 0, _visibleItems.Count - 1);
        Viewer.SetItems(_visibleItems, next);
        Viewer.Focus(FocusState.Programmatic);
    }

    private async Task DeleteSelectionAsync()
    {
        var items = SelectedItems;
        if (items.Count == 0) return;

        // 快照：确认对话框期间自动刷新可能改变选中项，
        // 绝不能确认之后再重新读一次 SelectedItems，否则会删掉用户没确认的文件。
        var targets = items.Select(i => i.FilePath).ToList();

        var preview = items.Count == 1
            ? items[0].FileName
            : $"{items.Count} 张截图";

        var confirmed = await ConfirmAsync(
            $"要把 {preview} 移到回收站吗？\n\n删除后可以从回收站还原。",
            "移到回收站");

        if (!confirmed) return;

        var deleted = ShellInterop.DeleteToRecycleBin(targets);
        if (deleted.Count == 0)
        {
            await ShowMessageAsync("删除失败", "无法把文件移到回收站，可能已被占用或权限不足。");
            return;
        }

        RemoveItemsFromLibrary(deleted);
    }

    private void RemoveItemsFromLibrary(IReadOnlyList<string> deletedPaths)
    {
        var set = new HashSet<string>(deletedPaths, StringComparer.OrdinalIgnoreCase);

        _allItems.RemoveAll(i => set.Contains(i.FilePath));

        foreach (var path in deletedPaths)
            _favorites.Remove(path);

        RebuildTabs();
        ApplyFilter();
        UpdateFolderSummaries();
    }

    // ------------------------------------------------------------------
    // 工具栏事件
    // ------------------------------------------------------------------

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput &&
            args.Reason != AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
        {
            return;
        }

        _searchText = sender.Text?.Trim() ?? string.Empty;
        ApplyFilter();
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        StatusText.Text = $"扫描完成 · 共 {_allItems.Count} 张";
    }

    private void OnRescanAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = RefreshAsync();
    }

    private void OnGridViewSizeChanged(object sender, SizeChangedEventArgs e) => ApplyTileSize();

    private void OnToggleFavoriteClick(object sender, RoutedEventArgs e) => _ = ToggleFavoriteAsync(SelectedItems);

    private void OnFavoriteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ToggleFavoriteAsync(SelectedItems);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => _ = CopyAsync(SelectedItems);

    private void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = CopyAsync(SelectedItems);
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        var first = SelectedItems.FirstOrDefault();
        if (first is not null) ShellInterop.RevealInExplorer(first.FilePath);
    }

    private void OnRevealAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OnRevealClick(sender, new RoutedEventArgs());
    }

    private void OnOpenWithDefaultAppClick(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems;
        if (items.Count == 0) return;

        // 超过 6 张时只打开第一张，避免一次弹出几十个窗口。
        foreach (var item in items.Take(6))
            ShellInterop.OpenWithDefaultApp(item.FilePath);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => _ = DeleteSelectionAsync();

    private void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = DeleteSelectionAsync();
    }

    private void OnOpenViewerAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (FirstSelected is not PhotoItem item) return;
        var index = _visibleItems.IndexOf(item);
        if (index >= 0) OpenViewer(index);
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => PhotoGrid.SelectAll();

    private void OnSelectAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        PhotoGrid.SelectAll();
    }

    private void OnFocusSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------------
    // 设置面板
    // ------------------------------------------------------------------

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;

        _settings.Current.Theme = ThemeBox.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "system",
        };

        ApplyTheme();
        ScheduleSave();
    }

    private void ApplyTheme()
    {
        var theme = _settings.Current.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        if (Content is FrameworkElement root)
            root.RequestedTheme = theme;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;

        _settings.Current.Sort = (SortMode)Math.Clamp(SortBox.SelectedIndex, 0, 3);
        ApplyFilter();
        ScheduleSave();
    }

    private async void OnWatchToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;

        _settings.Current.WatchFolders = WatchToggle.IsOn;
        ScheduleSave();

        if (WatchToggle.IsOn) _library.StartWatching();
        else _library.StopWatching();

        await Task.CompletedTask;
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var picked = await FolderPickerHelper.PickFolderAsync(this);
        if (string.IsNullOrWhiteSpace(picked)) return;

        if (_settings.Current.CustomFolders.Any(f => string.Equals(f.Path, picked, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowMessageAsync("已经添加过了", picked);
            return;
        }

        _settings.Current.CustomFolders.Add(new CustomFolder { Path = picked });
        _settings.Save();

        await RefreshAsync();
    }

    private async void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;

        _settings.Current.CustomFolders.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();

        await RefreshAsync();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
            ShellInterop.OpenFolder(path);
    }

    private async void OnClearThumbnailCacheClick(object sender, RoutedEventArgs e)
    {
        var freed = ThumbnailService.ClearCache();
        await ShowMessageAsync("已清理缩略图缓存", $"释放了 {FormatSize(freed)}。\n下次浏览时会重新生成。");
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        AppStorage.EnsureCreated();
        ShellInterop.OpenFolder(AppStorage.RootDirectory);
    }

    // ------------------------------------------------------------------
    // 检查更新 / 更新程序
    // ------------------------------------------------------------------

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;

        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "正在检查…";

        try
        {
            var result = await UpdateService.CheckAsync();

            _releaseUrl = result.Url;
            _pendingVersion = null;
            _pendingTag = null;
            UpdateStatusText.Text = result.Message;

            switch (result.Outcome)
            {
                case UpdateCheckOutcome.UpdateAvailable:
                    _pendingVersion = result.LatestVersion;
                    _pendingTag = result.LatestTag;
                    InstallUpdateButton.Content = $"下载并安装 {result.LatestVersion}";
                    InstallUpdateButton.Visibility = Visibility.Visible;
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    break;

                case UpdateCheckOutcome.Unknown:
                    // 没查出来的时候至少给一个手动出口
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    break;
            }
        }
        catch (Exception ex)
        {
            App.Log("检查更新失败：" + ex);
            UpdateStatusText.Text = $"检查更新失败：{ex.Message}";
            _releaseUrl = UpdateService.ReleasesPageUrl;
            OpenReleaseButton.Visibility = Visibility.Visible;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            _checkingUpdate = false;
        }
    }

    private void OnInstallUpdateClick(object sender, RoutedEventArgs e) => _ = InstallUpdateAsync();

    /// <summary>
    /// 下载官方安装程序 → 核对 hashes 分支里的 SHA-256 → 交给独立脚本静默安装，本程序退出。
    ///
    /// 点这个按钮本身就是确认动作，所以不再弹确认框；只有在程序目录不可写（装了也覆盖不了）
    /// 时才退回"手动下载"。哈希不匹配时安装包会被删掉并且不安装（UpdateService 里做的）。
    /// </summary>
    private async Task InstallUpdateAsync()
    {
        var version = _pendingVersion;
        if (version is null || _checkingUpdate) return;

        if (!UpdateService.CanWriteProgramDirectory())
        {
            UpdateStatusText.Text =
                $"当前程序目录不可写（{AppContext.BaseDirectory}），装了也覆盖不了，请用「打开发布页」手动下载 {version}。";
            OpenReleaseButton.Visibility = Visibility.Visible;
            return;
        }

        _checkingUpdate = true;
        InstallUpdateButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<double>(fraction =>
            {
                UpdateProgress.Value = fraction * 100;
                UpdateStatusText.Text = $"正在下载 {version}… {fraction * 100:F0}%";
            });

            var path = await UpdateService.DownloadInstallerAsync(version, _pendingTag, progress);

            var installed = UpdateService.GetInstalledLocation();
            UpdateStatusText.Text = installed is null
                ? $"校验通过，正在安装回原目录（{AppContext.BaseDirectory}）…"
                : $"校验通过，正在安装到 {installed}…";

            if (!UpdateService.StartInstallerScript(path))
            {
                UpdateStatusText.Text = $"没能启动安装脚本，安装包已下载到：{path}";
                return;
            }

            App.Log($"更新：{version} 交给独立脚本安装，本程序退出");
            Close();
        }
        catch (UpdateVerificationException ex)
        {
            // 文件已经被删掉了，这里只报告
            App.Log("更新：哈希校验未通过 — " + ex.Message);
            UpdateStatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            App.Log("更新失败：" + ex);
            UpdateStatusText.Text = $"更新失败：{ex.Message}";
        }
        finally
        {
            _checkingUpdate = false;
            InstallUpdateButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnOpenReleaseClick(object sender, RoutedEventArgs e)
        => await LaunchUrlAsync(_releaseUrl ?? UpdateService.ReleasesPageUrl);

    private void OnOpenChangelogClick(object sender, RoutedEventArgs e) => ChangelogWindow.ShowWindow();

    private async Task LaunchUrlAsync(string url)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri(url)))
                UpdateStatusText.Text = $"系统没有打开浏览器，地址：{url}";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"打开链接失败：{ex.Message}（地址：{url}）";
        }
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer?.Stop();
                _settings.Save();
            };
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// 同一个 XamlRoot 同时只允许一个 ContentDialog。
    /// 这些调用点大多是 async void（快捷键是“发射后不管”的），
    /// 连按两次 Delete 就会让第二个 ShowAsync 抛异常，进而杀掉进程，
    /// 所以这里做一次串行化保护。
    /// </summary>
    private async Task<bool> ConfirmAsync(string message, string primaryText)
    {
        if (_dialogOpen) return false;

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "确认",
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = primaryText,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            App.Log("显示确认对话框失败：" + ex);
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (_dialogOpen) return;

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "好",
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log("显示提示对话框失败：" + ex);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.##} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB",
    };
}
