using System.IO;
using Microsoft.UI.Windowing;

namespace GameGallery.Services;

/// <summary>
/// 给窗口设置图标（标题栏、任务栏按钮、Alt+Tab 都用它）。
/// 未打包的 WinUI 3 应用不会自动继承 EXE 内嵌的图标资源，必须显式设置，
/// 否则这几处显示的是 Windows App SDK 的默认图标——文件资源管理器里的图标却是对的。
/// </summary>
internal static class WindowIcon
{
    public static void Apply(AppWindow window)
    {
        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "GameGallery.ico");
            if (File.Exists(bundled))
            {
                window.SetIcon(bundled);
                App.Log($"窗口图标已设置：{bundled}");
                return;
            }

            App.Log($"捆绑图标不存在（{bundled}），改用 EXE 内嵌图标");

            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                window.SetIcon(exe);
                App.Log($"窗口图标已设置：{exe}");
                return;
            }

            App.Log("窗口图标设置失败：没有可用的图标来源");
        }
        catch (Exception ex)
        {
            App.Log($"设置窗口图标失败：{ex.Message}");
        }
    }
}
