using GameGallery.Models;
using GameGallery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI.Core;

namespace GameGallery.Views;

/// <summary>
/// 全屏大图查看器。
///
/// 几个刻意的设计决定：
///
///  1. 缩放比例以「适应窗口」为 100%：图片默认自适应铺满，角标显示 100%；
///     放大到 200% 就是在适应尺寸的基础上再放大一倍。
///
///  2. 位置和大小全部走布局（Canvas.Left/Top + Width/Height），**完全不用渲染变换**。
///     之前用 CompositeTransform 同时承担「适应缩放」和「用户缩放」，
///     一旦哪一步算错或没来得及提交，比例就会被叠加两次（典型症状是
///     "只画出视口的 74%"）。走布局后元素的实际矩形就等于屏幕上看到的矩形，
///     共享元素过渡的终点也天然正确。
///
///  3. 滚动缩放 / 拖拽平移自己实现，没用 ScrollViewer.ZoomMode：
///     ScrollViewer 会先消费滚轮事件做垂直滚动，"滚轮缩放"和滚动会互相打架。
/// </summary>
public sealed partial class PhotoViewer : UserControl
{
    /// <summary>解码长边上限，避免超大图占满内存。</summary>
    private const int MaxDecodeEdge = 3840;

    /// <summary>最多缓存的已解码图片张数。</summary>
    private const int CacheCapacity = 3;

    /// <summary>相对「适应窗口」的缩放范围。</summary>
    private const double MinZoom = 0.1;
    private const double MaxZoom = 12.0;

    /// <summary>双击时在「适应窗口」和这个倍数之间切换。</summary>
    private const double DoubleTapZoom = 2.0;

    private const int MaxFitRetries = 8;

    /// <summary>同时在跑的预解码上限（每张 4K 图的解码结果是几十 MB）。</summary>
    private const int MaxConcurrentPrefetch = 2;

    private readonly Dictionary<string, SoftwareBitmap> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly HashSet<string> _prefetching = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<PhotoItem> _items = Array.Empty<PhotoItem>();
    private int _index = -1;

    /// <summary>
    /// 当前显示的照片本身，而不是"下标指向的那张"。
    /// _items 是主窗口那份会随自动刷新重排的活集合，靠下标推导会指到别的照片上。
    /// </summary>
    private PhotoItem? _current;

    /// <summary>导航代号，用来丢弃已经过期的异步解码结果。</summary>
    private int _loadGeneration;

    // --- 几何状态（单位都是 DIP）---
    /// <summary>图片原始尺寸换算成 DIP（= 像素 / 显示器缩放）。</summary>
    private double _nativeW;
    private double _nativeH;

    /// <summary>「适应窗口」时图片的显示尺寸。</summary>
    private double _fitW;
    private double _fitH;

    /// <summary>相对适应尺寸的缩放倍数，1.0 就是角标上的 100%。</summary>
    private double _zoom = 1.0;

    /// <summary>图片左上角相对视口的位置。</summary>
    private double _posX;
    private double _posY;

    private bool _isPanning;
    private Point _panOrigin;
    private double _panOriginX;
    private double _panOriginY;

    private bool _isFitZoom = true;
    private int _fitRetries;

    /// <summary>入场动画还等着图片上屏。</summary>
    private bool _entrancePending;

    public PhotoViewer()
    {
        InitializeComponent();
        Viewport.Clip = new RectangleGeometry();
    }


    /// <summary>由主窗口注入，用于在解码大图时显示缩略图占位。</summary>
    public ThumbnailService? Thumbnails { get; set; }


    /// <summary>关闭时作为过渡起点的元素（当前显示的图片）。</summary>
    public FrameworkElement? AnimatedImage => ViewerImage;

    public PhotoItem? Current => _current;

    public event EventHandler? CloseRequested;
    public event EventHandler? FavoriteToggleRequested;
    public event EventHandler? CopyRequested;
    public event EventHandler? RevealRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? CurrentChanged;

    // ------------------------------------------------------------------
    // 对外 API
    // ------------------------------------------------------------------

    public void SetItems(IReadOnlyList<PhotoItem> items, int index)
    {
        _items = items;

        if (_items.Count == 0)
        {
            _index = -1;
            _current = null;
            Close();
            return;
        }

        ShowAt(index);
    }

    public void ShowAt(int index)
    {
        if (_items.Count == 0) return;
        _ = ShowAtAsync(index);
    }

    public void Close()
    {
        _loadGeneration++;
        _index = -1;
        _current = null;
        _entrancePending = false;
        ViewerImage.Source = null;
        PlaceholderImage.Source = null;
        LoadingRing.IsActive = false;
        // 复位成透明：下次打开时查看器是"透明地"出现，
        // 然后背景才跟着放大动画一起淡入。若复位成 1，
        // 下次打开会先亮出一层不透明背景，动画开始又跳回透明 —— 那一下就是闪烁。
        Backdrop.Opacity = 0;
        TopBar.Opacity = 0;
        ClearDecodedCache();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>收藏状态被外部修改后，同步图标。</summary>
    public void RefreshChrome()
    {
        var item = Current;
        if (item is null) return;
        FavoriteIcon.Glyph = item.IsFavorite ? "\uE735" : "\uE734";
    }

    // ------------------------------------------------------------------
    // 加载与显示
    // ------------------------------------------------------------------

    private async Task ShowAtAsync(int index)
    {
        if (_items.Count == 0) return;

        _index = ((index % _items.Count) + _items.Count) % _items.Count;
        var item = _items[_index];
        _current = item;
        var generation = ++_loadGeneration;

        FileNameText.Text = item.FileName;
        MetaText.Text = $"{item.GameName} · {item.DateText} · {item.SizeText} · {_index + 1}/{_items.Count}";
        IndexText.Text = $"{_index + 1} / {_items.Count}";
        RefreshChrome();
        ErrorText.Visibility = Visibility.Collapsed;
        CurrentChanged?.Invoke(this, EventArgs.Empty);

        ShowPlaceholder(item);

        try
        {
            if (!_cache.TryGetValue(item.FilePath, out var bitmap))
            {
                LoadingRing.IsActive = true;

                var decoded = await Task.Run(() => ThumbnailService.DecodeFullAsync(item.FilePath, MaxDecodeEdge))
                    .ConfigureAwait(true);

                if (generation != _loadGeneration || !ReferenceEquals(Current, item)) return;

                LoadingRing.IsActive = false;

                if (decoded is null)
                {
                    ViewerImage.Source = null;
                    ErrorText.Text = $"无法解码这张图片\n{item.FileName}";
                    ErrorText.Visibility = Visibility.Visible;
                    return;
                }

                bitmap = decoded;
                Remember(item.FilePath, decoded);
            }

            await DisplayAsync(item, bitmap, generation);
            PrefetchNeighbors();
        }
        catch (Exception ex)
        {
            // 单张图片出问题不应该让整个查看器崩掉。
            App.Log($"查看器显示失败（{item.FileName}）：{ex}");
            LoadingRing.IsActive = false;
            ViewerImage.Source = null;
            ErrorText.Text = $"无法显示这张图片\n{item.FileName}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>把解码结果贴上界面。SoftwareBitmapSource 只在 UI 线程上、按顺序创建。</summary>
    private async Task DisplayAsync(PhotoItem item, SoftwareBitmap bitmap, int generation)
    {
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);

        if (generation != _loadGeneration || !ReferenceEquals(Current, item)) return;

        ViewerImage.Source = source;
        PlaceholderImage.Source = null;
        PlaceholderImage.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        ErrorText.Visibility = Visibility.Collapsed;

        var raster = XamlRoot?.RasterizationScale ?? 1.0;
        if (raster <= 0) raster = 1.0;

        _nativeW = bitmap.PixelWidth / raster;
        _nativeH = bitmap.PixelHeight / raster;

        // 新图片：回到「适应窗口」= 角标 100%
        _zoom = 1.0;
        _isFitZoom = true;
        _fitRetries = 0;

        if (FitToViewport()) TryRunPendingEntrance();
    }

    private void ShowPlaceholder(PhotoItem item)
    {
        PlaceholderImage.Source = null;
        PlaceholderImage.Visibility = Visibility.Collapsed;

        // 入场动画本身就是图片从缩略图长大，再显示一张居中占位缩略图会先蹦一下。
        if (EntranceSourceElement is not null) return;


        var thumbPath = item.ThumbnailPath ?? ThumbnailService.GetCachePath(item);
        if (!File.Exists(thumbPath)) return;

        try
        {
            PlaceholderImage.Source = new BitmapImage(new Uri(thumbPath));
            PlaceholderImage.Visibility = Visibility.Visible;
        }
        catch (UriFormatException)
        {
        }
    }

    // ------------------------------------------------------------------
    // 缩放与布局（全部走布局，不用渲染变换）
    // ------------------------------------------------------------------

    /// <summary>
    /// 重新计算「适应窗口」的尺寸并应用。视口还没完成布局时返回 false，稍后重试。
    /// </summary>
    private bool FitToViewport()
    {
        if (_nativeW <= 0 || _nativeH <= 0) return false;

        var vw = Viewport.ActualWidth;
        var vh = Viewport.ActualHeight;

        if (vw < 1 || vh < 1)
        {
            // 视口还没完成布局。用 Low 优先级让布局 tick 先跑完再重试；
            // 次数有上限，另有 SizeChanged 兜底，不会一直转。
            if (_fitRetries++ < MaxFitRetries)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RetryFit);
            return false;
        }

        var ratio = Math.Min(Math.Min(vw / _nativeW, vh / _nativeH), 1.0);
        _fitW = _nativeW * ratio;
        _fitH = _nativeH * ratio;

        _zoom = 1.0;
        _isFitZoom = true;
        ApplyLayout();
        return true;
    }

    /// <summary>
    /// 视口布局完成后的补算。入场动画必须等这里成功之后再放：
    /// 否则共享元素过渡会以「还没定好尺寸」的矩形为终点，
    /// 动画结束时看着过度放大，落定后才恢复正常。
    /// </summary>
    private void RetryFit()
    {
        if (FitToViewport()) TryRunPendingEntrance();
    }

    /// <summary>按当前缩放算出尺寸和位置，并把位置夹在视口内。</summary>
    private void ApplyLayout()
    {
        if (_fitW <= 0 || _fitH <= 0) return;

        var w = _fitW * _zoom;
        var h = _fitH * _zoom;

        ViewerImage.Width = w;
        ViewerImage.Height = h;

        var vw = Viewport.ActualWidth;
        var vh = Viewport.ActualHeight;

        if (vw >= 1 && vh >= 1)
        {
            // 比视口小就居中；比视口大就不许拖出边界。
            _posX = w <= vw ? (vw - w) / 2 : Math.Clamp(_posX, vw - w, 0);
            _posY = h <= vh ? (vh - h) / 2 : Math.Clamp(_posY, vh - h, 0);

            Canvas.SetLeft(ViewerImage, _posX);
            Canvas.SetTop(ViewerImage, _posY);
        }

        ZoomText.Text = $"{_zoom * 100:0}%";
    }

    /// <summary>以某个点为锚点缩放（滚轮、按钮、双击都走这里）。</summary>
    private void ZoomAt(Point anchor, double factor)
    {
        if (_fitW <= 0) return;

        var newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 1e-6) return;

        // 锚点下的图片内容保持不动
        var contentX = (anchor.X - _posX) / _zoom;
        var contentY = (anchor.Y - _posY) / _zoom;

        _zoom = newZoom;
        _posX = anchor.X - contentX * _zoom;
        _posY = anchor.Y - contentY * _zoom;

        _isFitZoom = false;
        ApplyLayout();
    }

    private void ZoomAtCenter(double factor)
        => ZoomAt(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), factor);

    /// <summary>回到「适应窗口」，也就是角标上的 100%。</summary>
    private void ResetToFit()
    {
        _zoom = 1.0;
        _posX = 0;
        _posY = 0;
        _isFitZoom = true;
        ApplyLayout();
    }

    // ------------------------------------------------------------------
    // 指针交互
    // ------------------------------------------------------------------

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewport);
        if (point.Properties.IsHorizontalMouseWheel) return;

        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        ZoomAt(point.Position, Math.Pow(1.0015, delta));
        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_fitW <= 0) return;

        if (_isFitZoom)
            ZoomAtCenter(DoubleTapZoom);
        else
            ResetToFit();

        e.Handled = true;
    }

    private void OnPanStart(object sender, PointerRoutedEventArgs e)
    {
        if (_fitW <= 0) return;

        var point = e.GetCurrentPoint(Viewport);

        // 鼠标必须按住左键才开始拖拽；触控/笔始终允许。
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panOrigin = point.Position;
        _panOriginX = _posX;
        _panOriginY = _posY;

        Viewport.CapturePointer(e.Pointer);
        FocusViewer();

        // 不设置 Handled，否则双击缩放手势不会被识别。
    }

    private void OnPanMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;

        var point = e.GetCurrentPoint(Viewport);
        _posX = _panOriginX + (point.Position.X - _panOrigin.X);
        _posY = _panOriginY + (point.Position.Y - _panOrigin.Y);

        ApplyLayout();
        e.Handled = true;
    }

    private void OnPanEnd(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        Viewport.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Viewport.Clip is RectangleGeometry clip)
            clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);

        if (_nativeW <= 0) return;

        // 适应窗口模式下跟着窗口重新算；放大状态下保持倍数，只重新夹位置。
        var ratio = Math.Min(Math.Min(e.NewSize.Width / _nativeW, e.NewSize.Height / _nativeH), 1.0);
        _fitW = _nativeW * ratio;
        _fitH = _nativeH * ratio;
        ApplyLayout();
    }

    // ------------------------------------------------------------------
    // 键盘
    // ------------------------------------------------------------------

    private void OnViewerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsKeyDown(VirtualKey.Control);
        if (_items.Count == 0 && e.Key != VirtualKey.Escape) return;

        switch (e.Key)
        {
            case VirtualKey.Escape:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;

            case VirtualKey.Left:
            case VirtualKey.PageUp:
                GoTo(-1);
                e.Handled = true;
                return;

            case VirtualKey.Right:
            case VirtualKey.PageDown:
            case VirtualKey.Space:
                GoTo(1);
                e.Handled = true;
                return;

            case VirtualKey.Home:
                ShowAt(0);
                FocusViewer();
                e.Handled = true;
                return;

            case VirtualKey.End:
                ShowAt(_items.Count - 1);
                FocusViewer();
                e.Handled = true;
                return;

            case VirtualKey.Delete:
                DeleteRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;

            case VirtualKey.F:
                if (ctrl) return;
                FavoriteToggleRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;

            case VirtualKey.C:
                if (!ctrl) return;
                CopyRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;

            case VirtualKey.O:
                if (ctrl) return;
                RevealRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;

            case VirtualKey.Add:
            case (VirtualKey)0xBB: // OEM '+'
                ZoomAtCenter(1.25);
                e.Handled = true;
                return;

            case VirtualKey.Subtract:
            case (VirtualKey)0xBD: // OEM '-'
                ZoomAtCenter(1 / 1.25);
                e.Handled = true;
                return;

            case VirtualKey.Number0:
                ResetToFit();
                e.Handled = true;
                return;

            case VirtualKey.Number1:
                ZoomAtCenter(DoubleTapZoom);
                e.Handled = true;
                return;
        }
    }

    private void GoTo(int delta)
    {
        if (_items.Count <= 1) return;
        ShowAt(_index + delta);
        FocusViewer();
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => GoTo(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => GoTo(1);

    private void FocusViewer() => Focus(FocusState.Programmatic);

    private static bool IsKeyDown(VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return state.HasFlag(CoreVirtualKeyStates.Down);
    }

    // ------------------------------------------------------------------
    // 解码缓存与预取
    // ------------------------------------------------------------------

    private void PrefetchNeighbors()
    {
        if (_items.Count <= 1) return;

        Prefetch(_index + 1);
        Prefetch(_index - 1);
    }

    /// <summary>后台预解码相邻图片。这里只把像素放进缓存，GPU 纹理留给显示路径创建。</summary>
    private void Prefetch(int index)
    {
        if (_items.Count == 0) return;
        if (_prefetching.Count >= MaxConcurrentPrefetch) return;

        var wrapped = ((index % _items.Count) + _items.Count) % _items.Count;
        var item = _items[wrapped];
        if (_cache.ContainsKey(item.FilePath)) return;
        if (!_prefetching.Add(item.FilePath)) return;

        _ = Task.Run(async () =>
        {
            SoftwareBitmap? bitmap = null;
            try
            {
                bitmap = await ThumbnailService.DecodeFullAsync(item.FilePath, MaxDecodeEdge).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Log($"预解码失败（{item.FileName}）：{ex.Message}");
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                _prefetching.Remove(item.FilePath);

                if (bitmap is null) return;

                // 查看器可能已经关了：别把几十 MB 的位图塞进一个已经被清空的缓存。
                if (Visibility != Visibility.Visible)
                {
                    bitmap.Dispose();
                    return;
                }

                Remember(item.FilePath, bitmap);
            });
        });
    }

    private void Remember(string path, SoftwareBitmap bitmap)
    {
        // 回调可能在导航之后才跑到，这张图已经被缓存过了。
        if (_cache.ContainsKey(path))
        {
            bitmap.Dispose();
            return;
        }

        _cache[path] = bitmap;
        _cacheOrder.Enqueue(path);
        Evict();
    }

    private void Evict()
    {
        // 淘汰最旧的条目，但绝不淘汰正在显示的那张。
        var attempts = 0;
        var maxAttempts = _cacheOrder.Count + 1;

        while (_cache.Count > CacheCapacity && _cacheOrder.Count > 0 && attempts++ < maxAttempts)
        {
            var oldest = _cacheOrder.Dequeue();

            if (string.Equals(oldest, Current?.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _cacheOrder.Enqueue(oldest);
                continue;
            }

            if (_cache.Remove(oldest, out var evicted))
                evicted.Dispose();
        }
    }

    private void ClearDecodedCache()
    {
        foreach (var bitmap in _cache.Values) bitmap.Dispose();
        _cache.Clear();
        _cacheOrder.Clear();
        _prefetching.Clear();
    }

    // ------------------------------------------------------------------
    // 放大 / 缩小动画
    //
    // 自己用 Storyboard 做，而不是用 ConnectedAnimation：
    // DirectConnectedAnimationConfiguration 是线性插值，速度是恒定的，
    // 想要"慢-快-慢"的非线性手感就必须自己控制缓动曲线。
    // 位置和尺寸都在布局上，所以直接动画这四个值即可。
    // ------------------------------------------------------------------

    /// <summary>入场动画的起点元素（缩略图）。矩形等查看器可见后再算，否则坐标是错的。</summary>
    public FrameworkElement? EntranceSourceElement { get; set; }

    /// <summary>缩略图可能还没实例化，所以入场动画等图片真正上屏时再播。</summary>
    public void PlayOpenAnimation() => _entrancePending = true;

    /// <summary>把某个元素（缩略图）的矩形换算成视口坐标，供主窗口设置动画起点/终点。</summary>
    public Rect? GetSourceRectFor(FrameworkElement element)
    {
        if (element.ActualWidth < 1 || element.ActualHeight < 1) return null;

        try
        {
            var transform = element.TransformToVisual(Viewport);
            return transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private void TryRunPendingEntrance()
    {
        if (!_entrancePending) return;
        _entrancePending = false;

        // 上一次关闭后背景停在 0，这里复位
        Backdrop.Opacity = 0;
        TopBar.Opacity = 0;

        var target = new Rect(_posX, _posY, _fitW * _zoom, _fitH * _zoom);
        var source = EntranceSourceElement is null ? null : GetSourceRectFor(EntranceSourceElement);
        EntranceSourceElement = null;
        var from = source ?? new Rect(target.X + target.Width / 2, target.Y + target.Height / 2, 1, 1);

        RunRectAnimation(from, target, fadeIn: true, onCompleted: null);
    }

    /// <summary>
    /// 关闭前的反向动画：图片从当前位置缩回缩略图。
    /// 动画结束后才真正隐藏查看器。
    /// </summary>
    public void PlayExitAnimation(Rect target, Action onCompleted)
    {
        var from = new Rect(_posX, _posY, _fitW * _zoom, _fitH * _zoom);
        RunRectAnimation(from, target, fadeIn: false, onCompleted);
    }

    /// <summary>
    /// 同时动画位置和尺寸，用缓入缓出的非线性曲线（慢-快-慢）。
    /// FillBehavior=Stop 配合"基准值先设成终点"，动画结束后会自然落到终点，不会闪。
    /// </summary>
    private void RunRectAnimation(Rect from, Rect to, bool fadeIn, Action? onCompleted)
    {
        // EaseInOut：起步慢、中间快、收尾慢 —— 就是"非线性"
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(340));

        // 基准值 = 终点；动画播放时覆盖它，结束后自动回落到它
        ViewerImage.Width = to.Width;
        ViewerImage.Height = to.Height;
        Canvas.SetLeft(ViewerImage, to.X);
        Canvas.SetTop(ViewerImage, to.Y);

        _zoom = _fitW > 0 ? to.Width / _fitW : 1.0;
        _posX = to.X;
        _posY = to.Y;
        ZoomText.Text = $"{_zoom * 100:0}%";

        var storyboard = new Storyboard();

        void Animate(DependencyObject target, string property, double a, double b)
        {
            var animation = new DoubleAnimation
            {
                From = a,
                To = b,
                Duration = duration,
                EasingFunction = ease,
                EnableDependentAnimation = true,
                FillBehavior = FillBehavior.Stop,
            };

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        Animate(ViewerImage, "Width", from.Width, to.Width);
        Animate(ViewerImage, "Height", from.Height, to.Height);
        Animate(ViewerImage, "(Canvas.Left)", from.X, to.X);
        Animate(ViewerImage, "(Canvas.Top)", from.Y, to.Y);

        // 背景和顶栏跟着图片一起淡入 / 淡出，而不是一打开就固定铺在那儿。
        // 背景走得稍慢一点，收尾更自然。
        var fadeDuration = new Duration(TimeSpan.FromMilliseconds(fadeIn ? 260 : 220));
        var backdropEase = new QuadraticEase { EasingMode = fadeIn ? EasingMode.EaseOut : EasingMode.EaseIn };

        void Fade(DependencyObject target, double a, double b)
        {
            var fade = new DoubleAnimation
            {
                From = a,
                To = b,
                Duration = fadeDuration,
                EasingFunction = backdropEase,
                EnableDependentAnimation = true,
                FillBehavior = FillBehavior.Stop,
            };

            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
        }

        // 基准值取终点，配合 FillBehavior=Stop，动画结束后自然落到终点
        Backdrop.Opacity = fadeIn ? 1 : 0;
        TopBar.Opacity = fadeIn ? 1 : 0;

        Fade(Backdrop, fadeIn ? 0 : 1, fadeIn ? 1 : 0);
        Fade(TopBar, fadeIn ? 0 : 1, fadeIn ? 1 : 0);

        if (onCompleted is not null)
            storyboard.Completed += (_, _) => onCompleted();

        storyboard.Begin();
    }

    // ------------------------------------------------------------------
    // 右键菜单
    // ------------------------------------------------------------------

    private void OnContextRevealClick(object sender, RoutedEventArgs e)
        => RevealRequested?.Invoke(this, EventArgs.Empty);

    private void OnContextCopyClick(object sender, RoutedEventArgs e)
        => CopyRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------
    // 顶部按钮
    // ------------------------------------------------------------------

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
        => FavoriteToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnCopyClick(object sender, RoutedEventArgs e)
        => CopyRequested?.Invoke(this, EventArgs.Empty);

    private void OnRevealClick(object sender, RoutedEventArgs e)
        => RevealRequested?.Invoke(this, EventArgs.Empty);

    private void OnDeleteClick(object sender, RoutedEventArgs e)
        => DeleteRequested?.Invoke(this, EventArgs.Empty);
}
