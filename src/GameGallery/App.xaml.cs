using GameGallery.Services;
using Microsoft.UI.Xaml;

namespace GameGallery;

public partial class App : Application
{
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
