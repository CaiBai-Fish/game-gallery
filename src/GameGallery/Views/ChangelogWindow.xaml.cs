using GameGallery.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.System;

namespace GameGallery.Views;

/// <summary>
/// 更新日志浮窗：固定大小、不可拖拽调整（只有最小化和关闭），内容用 Markdig 解析后渲染成 WinUI 元素。
/// 同时只允许存在一个实例，重复点击只是把它激活。
/// </summary>
public sealed partial class ChangelogWindow : Window
{
    private const int WindowWidth = 780;
    private const int WindowHeight = 840;

    private static ChangelogWindow? _current;

    public static void ShowWindow()
    {
        if (_current is not null)
        {
            _current.Activate();
            return;
        }

        var window = new ChangelogWindow();
        _current = window;
        window.Closed += (_, _) => _current = null;
        window.Activate();
    }

    private ChangelogWindow()
    {
        InitializeComponent();

        Title = $"更新日志 — 游戏截图图库 {UpdateService.CurrentVersion}";
        StatusText.Text = "正在获取更新日志…";

        ConfigureWindow();
        _ = LoadAsync();
    }

    private void ConfigureWindow()
    {
        try
        {
            AppWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));
            WindowIcon.Apply(AppWindow);

            // 「无法调整的浮窗」：去掉拖拽边框和最大化，只留最小化/关闭
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = true;
            }

            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (area is not null)
            {
                var x = area.WorkArea.X + ((area.WorkArea.Width - WindowWidth) / 2);
                var y = area.WorkArea.Y + ((area.WorkArea.Height - WindowHeight) / 2);
                AppWindow.Move(new PointInt32(Math.Max(0, x), Math.Max(0, y)));
            }
        }
        catch (Exception ex)
        {
            App.Log("更新日志窗口初始化失败：" + ex);
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var fetch = await UpdateService.FetchChangelogAsync();

            if (fetch.Markdown is null)
            {
                StatusText.Text = $"获取失败：{fetch.Error}";
                DocPanel.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "没能取到 CHANGELOG.md。可以点下面的「在 GitHub 上查看」，或稍后再打开这个窗口。",
                });
                return;
            }

            DocPanel.Children.Add(MarkdownRenderer.Render(fetch.Markdown));

            StatusText.Text = fetch.FromCache
                ? $"离线：显示上次获取的内容（{fetch.Error}）"
                : $"来源：{UpdateService.ChangelogUrl}";

            App.Log($"更新日志窗口：已渲染 {fetch.Markdown.Length} 字符");
        }
        catch (Exception ex)
        {
            App.Log("渲染更新日志失败：" + ex);
            StatusText.Text = "渲染失败：" + ex.Message;
        }
    }

    private async void OnOpenOnGitHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(UpdateService.ChangelogUrl));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开链接失败：{ex.Message}";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
