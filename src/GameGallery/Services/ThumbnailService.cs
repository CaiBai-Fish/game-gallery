using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GameGallery.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace GameGallery.Services;

/// <summary>
/// 缩略图服务。
///
/// 策略：磁盘缓存的 JPEG 缩略图 + WinUI 内部解码缓存。
///  - 每个源文件只生成一次 512px 长边的 JPEG；缓存键包含文件大小与写入时间，截图被覆盖时会自动重新生成。
///  - 显示时通过 BitmapImage.DecodePixelWidth 让 WinUI 按实际显示尺寸解码，避免大图占内存。
/// </summary>
public sealed class ThumbnailService
{
    /// <summary>缓存缩略图的长边像素。</summary>
    public const int CacheEdge = 512;

    private readonly SemaphoreSlim _gate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

    /// <summary>把 UI 需要的显示尺寸吸附到 32px 档位，减少 WinUI 解码缓存条目。</summary>
    public static int BucketSize(double displayEdge, double rasterizationScale)
    {
        var physical = displayEdge * (rasterizationScale <= 0 ? 1 : rasterizationScale);
        var bucket = (int)(Math.Ceiling(physical / 32.0) * 32);
        return Math.Clamp(bucket, 96, CacheEdge);
    }

    public static string GetCachePath(PhotoItem item)
    {
        var raw = $"{item.FilePath.ToLowerInvariant()}|{item.SizeBytes}|{item.CapturedAt.Ticks}|{CacheEdge}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return Path.Combine(AppStorage.ThumbnailDirectory, hash[..2], hash + ".jpg");
    }

    /// <summary>
    /// 取得缩略图。返回的 BitmapImage 已绑定缓存文件，像素解码由 WinUI 异步完成。
    /// 必须在 UI 线程上调用。
    /// </summary>
    public async Task<BitmapImage?> GetThumbnailAsync(PhotoItem item, int decodePixelWidth, CancellationToken ct = default)
    {
        var cachePath = GetCachePath(item);
        item.ThumbnailPath = cachePath;

        if (!File.Exists(cachePath))
        {
            var generated = await EnsureCacheAsync(item.FilePath, cachePath, ct).ConfigureAwait(true);
            if (!generated) return null;
        }

        if (ct.IsCancellationRequested) return null;

        var image = new BitmapImage
        {
            DecodePixelWidth = decodePixelWidth,
            DecodePixelType = DecodePixelType.Physical,
        };

        try
        {
            image.UriSource = new Uri(cachePath);
        }
        catch (UriFormatException)
        {
            return null;
        }

        return image;
    }

    /// <summary>确保缓存文件存在；必要时在线程池上解码并编码。</summary>
    public async Task<bool> EnsureCacheAsync(string sourcePath, string cachePath, CancellationToken ct = default)
    {
        if (File.Exists(cachePath)) return true;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(cachePath)) return true;

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            // 先写唯一的临时文件再原子替换，避免并发请求写出半个文件。
            var temp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                var ok = await Task.Run(() => EncodeThumbnailAsync(sourcePath, temp, ct), ct).ConfigureAwait(false);
                if (!ok) return false;

                File.Move(temp, cachePath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                return false;
            }
            finally
            {
                TryDelete(temp);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<bool> EncodeThumbnailAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested) return false;

            using var source = await FileRandomAccessStream.OpenAsync(sourcePath, FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(source);

            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if (width == 0 || height == 0) return false;

            var scale = Math.Min(1.0, CacheEdge / (double)Math.Max(width, height));
            var transform = new BitmapTransform
            {
                InterpolationMode = BitmapInterpolationMode.Fant,
                ScaledWidth = Math.Max(1u, (uint)Math.Round(width * scale)),
                ScaledHeight = Math.Max(1u, (uint)Math.Round(height * scale)),
            };

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            // 图块已经被回收的话，编码和落盘就可以省掉了。
            if (ct.IsCancellationRequested) return false;

            using (File.Create(destinationPath)) { }

            using var destination = await FileRandomAccessStream.OpenAsync(destinationPath, FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, destination);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();

            return true;
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            // 损坏或不受支持的图像不应让整个图库失败，视为“无缩略图”。
            return false;
        }
    }

    /// <summary>
    /// 为查看器解码完整图片。SoftwareBitmap 可在后台线程创建，再在 UI 线程包装成 SoftwareBitmapSource。
    /// </summary>
    public static async Task<SoftwareBitmap?> DecodeFullAsync(string path, int maxEdge, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return null;

        try
        {
            using var source = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
            if (ct.IsCancellationRequested) return null;

            var decoder = await BitmapDecoder.CreateAsync(source);

            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if (width == 0 || height == 0) return null;

            BitmapTransform? transform = null;
            var longest = Math.Max(width, height);
            if (longest > maxEdge && maxEdge > 0)
            {
                var scale = maxEdge / (double)longest;
                transform = new BitmapTransform
                {
                    InterpolationMode = BitmapInterpolationMode.Fant,
                    ScaledWidth = Math.Max(1u, (uint)Math.Round(width * scale)),
                    ScaledHeight = Math.Max(1u, (uint)Math.Round(height * scale)),
                };
            }

            return transform is null
                ? await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                : await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Windows 图像编解码组件在文件损坏时抛出 COM 异常，这类失败可以安全地降级处理。</summary>
    private static bool IsDecodeFailure(Exception ex) => ex switch
    {
        IOException => true,                 // 也覆盖 FileNotFoundException / DirectoryNotFoundException
        UnauthorizedAccessException => true,
        ArgumentException or NotSupportedException => true,
        COMException => true,
        ExternalException => true,
        _ => false,
    };

    /// <summary>清空磁盘缩略图缓存，返回释放的字节数。</summary>
    public static long ClearCache()
    {
        long freed = 0;

        try
        {
            var cacheRoot = Path.GetFullPath(AppStorage.ThumbnailDirectory);
            var dataRoot = Path.GetFullPath(AppStorage.RootDirectory);

            // 缩略图目录应当位于数据目录之下。GAMEGALLERY_DATA_DIR 是用户可设的，
            // 万一指到别处，这里绝不能因为一次“清理缓存”就删掉别人的文件。
            if (!cacheRoot.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cacheRoot, dataRoot, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (!Directory.Exists(cacheRoot)) return 0;

            // 只删除本服务自己生成的 .jpg 与残留的 .tmp，不做无差别删除。
            foreach (var file in Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    freed += new FileInfo(file).Length;
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(cacheRoot))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return freed;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
