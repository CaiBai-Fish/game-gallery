using GameGallery.Services;
using Microsoft.UI.Xaml;

namespace GameGallery;

public partial class App : Application
{
    /// <summary>
    /// 单实例互斥体。进程退出时系统会自动释放，不需要手动 Release。
    /// 用 Local\ 前缀（每用户会话一个），和 per-user 安装的定位一致。
    /// </summary>
    private static Mutex? _instanceMutex;

    private const string InstanceMutexName = @"Local\GameGallery.SingleInstance.v1";

    private Window? _window;

    public App()
    {
        InitializeComponent();

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("AppDomain.UnhandledException: " + e.ExceptionObject);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("TaskScheduler.UnobservedTaskException: " + e.Exception);
            e.SetObserved();
        };
    }

    public static Window? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 单实例：已经有实例在跑就把它切到前台，本次启动直接退出。
        // 注意要在建窗口之前判断，否则会闪出一个窗口再关掉。
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            var activated = ShellInterop.ActivateExistingInstance();
            Log(activated
                ? "已有实例在运行，已把它切到前台，本次启动退出"
                : "已有实例在运行，但没能把它切到前台，本次启动退出");

            // 只 return 不行：WinUI 的消息循环照旧跑着，会留下一个没有窗口的进程
            // （实测任务管理器里能看到两个 GameGallery.exe）。这里直接结束进程。
            try { Exit(); } catch (Exception ex) { Log("退出时出错：" + ex.Message); }
            Environment.Exit(0);
            return;
        }

        try
        {
            AppStorage.EnsureCreated();
            Log($"启动：数据目录 {AppStorage.RootDirectory}（可移植模式={AppStorage.IsPortable}）");

            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log("启动失败：" + ex);
            throw;
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"XamlUnhandledException: {e.Message}\n{e.Exception}");
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppStorage.RootDirectory);
            File.AppendAllText(
                AppStorage.LogFilePath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
