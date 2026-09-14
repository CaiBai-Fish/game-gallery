using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace GameGallery.Services;

/// <summary>与 Windows 外壳的交互：资源管理器、回收站、剪贴板、系统对话框宿主窗口。</summary>
public static class ShellInterop
{
    // ------------------------------------------------------------------
    // 资源管理器
    // ------------------------------------------------------------------

    public static void RevealInExplorer(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
    }

    public static void OpenFolder(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
    }

    public static void OpenWithDefaultApp(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
    }

    // ------------------------------------------------------------------
    // 删除到回收站
    // ------------------------------------------------------------------

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>
    /// 删除到回收站（可撤销）。返回实际删除成功的路径。
    /// </summary>
    public static IReadOnlyList<string> DeleteToRecycleBin(IReadOnlyList<string> paths)
    {
        var deleted = new List<string>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path))
            {
                // 文件已经不在了，视为已删除，让界面同步。
                deleted.Add(path);
                continue;
            }

            var op = new SHFILEOPSTRUCTW
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                // 路径列表必须以双 NUL 结尾。
                pFrom = path + "\0\0",
                pTo = null,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING,
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null,
            };

            try
            {
                var result = SHFileOperationW(ref op);
                if (result == 0 && !op.fAnyOperationsAborted && !File.Exists(path))
                    deleted.Add(path);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return deleted;
            }
        }

        return deleted;
    }

    // ------------------------------------------------------------------
    // 剪贴板
    // ------------------------------------------------------------------

    /// <summary>
    /// 复制文件到剪贴板：既提供“文件”格式（可粘贴到资源管理器/聊天工具），
    /// 单张时再额外提供位图格式（可粘贴到画图、Photoshop 等）。
    /// </summary>
    public static async Task<bool> CopyToClipboardAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;

        try
        {
            var files = new List<IStorageItem>();
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                files.Add(await StorageFile.GetFileFromPathAsync(path));
            }

            if (files.Count == 0) return false;

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(files);

            if (files.Count == 1 && files[0] is StorageFile single)
            {
                package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(single));
            }

            Clipboard.SetContent(package);

            // 让剪贴板内容在本进程退出后依然可用。
            try
            {
                Clipboard.Flush();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 系统对话框需要宿主窗口句柄（非打包应用必须显式初始化）
    // ------------------------------------------------------------------

    public static IntPtr GetWindowHandle(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    public static void InitializeWithWindow(object target, Window window)
        => WinRT.Interop.InitializeWithWindow.Initialize(target, GetWindowHandle(window));
}

/// <summary>文件夹选择器（非打包 WinUI 应用需要先绑定窗口句柄）。</summary>
public static class FolderPickerHelper
{
    public static async Task<string?> PickFolderAsync(Window window)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };

            picker.FileTypeFilter.Add("*");
            ShellInterop.InitializeWithWindow(picker, window);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
