using System.Text;
using System.Text.Json;
using GameGallery.Models;
using Microsoft.Win32;

namespace GameGallery.Services;

public enum DiscoverySource
{
    /// <summary>HoYoPlay 启动器状态库（%APPDATA%\miHoYo\HYP\...\gamedata.dat）。</summary>
    HoyoPlay,

    /// <summary>Windows 卸载注册表项 InstallLocation。</summary>
    UninstallRegistry,

    /// <summary>磁盘特征扫描（按可执行文件名识别）。</summary>
    DriveScan,

    /// <summary>用户手动添加。</summary>
    Manual,
}

/// <summary>一个可浏览的截图文件夹及其来源说明。</summary>
public sealed class LocatedFolder
{
    public required string GameKey { get; init; }
    public required string GameName { get; init; }
    public required string ScreenshotPath { get; init; }
    public string? InstallPath { get; init; }
    public DiscoverySource Source { get; init; }
    public bool Exists { get; init; }

    public string SourceText => Source switch
    {
        DiscoverySource.HoyoPlay => "HoYoPlay 启动器",
        DiscoverySource.UninstallRegistry => "注册表",
        DiscoverySource.DriveScan => "磁盘扫描",
        DiscoverySource.Manual => "手动添加",
        _ => "未知",
    };
}

/// <summary>
/// 定位各游戏的截图文件夹。
///
/// 重要结论：miHoYo 的三个游戏**不会**把安装目录写进常规注册表项
/// （HKCU\Software\miHoYo\&lt;游戏名&gt; 只有画质/账号设置，
///  卸载项 InstallLocation 指向启动器 D:\miHoYo Launcher 而不是游戏本体）。
/// 真正可靠的来源是 HoYoPlay 自己的状态库 gamedata.dat，其中每个游戏都有明文 installPath。
/// 因此这里的顺序是：HoYoPlay 状态库 → 卸载注册表 → 磁盘特征扫描 → 用户手动添加。
/// </summary>
public static class GameLocator
{
    private static readonly string[] ContainerDirectoryNames =
    {
        "Games", "Game", "Games2", "Program Files", "Program Files (x86)",
        "steamapps", "common", "miHoYo", "HoYoPlay", "HoYoverse",
    };

    private static readonly string[] GameishKeywords =
    {
        "genshin", "yuanshen", "原神", "star", "rail", "星穹", "honkai", "崩坏",
        "zenless", "绝区", "mihoyo", "hoyo",
    };

    public static IReadOnlyList<LocatedFolder> Locate(
        IReadOnlyList<CustomFolder> customFolders,
        IReadOnlyList<GameDefinition> games,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<LocatedFolder>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(LocatedFolder folder, bool requireExistingScreenshotFolder)
        {
            var exists = SafeDirectoryExists(folder.ScreenshotPath);
            if (requireExistingScreenshotFolder && !exists) return;

            var key = NormalizePath(folder.ScreenshotPath);
            if (!seenPaths.Add(key)) return;

            results.Add(new LocatedFolder
            {
                GameKey = folder.GameKey,
                GameName = folder.GameName,
                ScreenshotPath = folder.ScreenshotPath,
                InstallPath = folder.InstallPath,
                Source = folder.Source,
                Exists = exists,
            });
            resolvedGames.Add(folder.GameKey);
        }

        // ---- 1. HoYoPlay 状态库（最可靠）----
        progress?.Report("正在读取 HoYoPlay 启动器配置…");
        foreach (var (installPath, biz, isPrimary) in ReadHoyoPlayInstallPaths(ct))
        {
            ct.ThrowIfCancellationRequested();

            var game = GameCatalog.ByBiz(biz) ?? GameCatalog.ByInstallDirectory(installPath);
            if (game is null) continue;
            if (!SafeDirectoryExists(installPath)) continue;

            var shotPath = ResolveScreenshotPath(game, installPath);
            Add(
                new LocatedFolder
                {
                    GameKey = game.Key,
                    GameName = game.DisplayName,
                    ScreenshotPath = shotPath,
                    InstallPath = installPath,
                    Source = DiscoverySource.HoyoPlay,
                    Exists = SafeDirectoryExists(shotPath),
                },
                requireExistingScreenshotFolder: !isPrimary);
        }

        // ---- 2. 卸载注册表项 ----
        progress?.Report("正在检查注册表…");
        foreach (var (displayName, installLocation) in ReadUninstallEntries(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(installLocation) || !SafeDirectoryExists(installLocation)) continue;

            var game = GameCatalog.ByInstallDirectory(installLocation)
                       ?? MatchByDisplayName(displayName, games);
            if (game is null) continue;

            var shotPath = ResolveScreenshotPath(game, installLocation);
            Add(
                new LocatedFolder
                {
                    GameKey = game.Key,
                    GameName = game.DisplayName,
                    ScreenshotPath = shotPath,
                    InstallPath = installLocation,
                    Source = DiscoverySource.UninstallRegistry,
                    Exists = SafeDirectoryExists(shotPath),
                },
                requireExistingScreenshotFolder: true);
        }

        // ---- 3. 磁盘特征扫描（只补充尚未定位到的游戏）----
        var missing = games.Where(g => !resolvedGames.Contains(g.Key)).ToList();
        if (missing.Count > 0)
        {
            progress?.Report("正在扫描磁盘上的游戏目录…");
            foreach (var installPath in FindInstallDirectories(missing, ct))
            {
                ct.ThrowIfCancellationRequested();
                var game = GameCatalog.ByInstallDirectory(installPath);
                if (game is null || resolvedGames.Contains(game.Key)) continue;

                var shotPath = ResolveScreenshotPath(game, installPath);
                Add(
                    new LocatedFolder
                    {
                        GameKey = game.Key,
                        GameName = game.DisplayName,
                        ScreenshotPath = shotPath,
                        InstallPath = installPath,
                        Source = DiscoverySource.DriveScan,
                        Exists = SafeDirectoryExists(shotPath),
                    },
                    requireExistingScreenshotFolder: true);
            }
        }

        // ---- 4. 用户手动添加 ----
        foreach (var custom in customFolders)
        {
            if (string.IsNullOrWhiteSpace(custom.Path)) continue;
            if (!SafeDirectoryExists(custom.Path)) continue;

            var game = ResolveCustomFolderGame(custom, games);
            Add(
                new LocatedFolder
                {
                    GameKey = game.Key,
                    GameName = game.DisplayName,
                    ScreenshotPath = custom.Path,
                    InstallPath = null,
                    Source = DiscoverySource.Manual,
                    Exists = true,
                },
                requireExistingScreenshotFolder: false);
        }

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < games.Count; i++) rank[games[i].Key] = i;
        rank[UnknownGame.Key] = games.Count;

        return results
            .OrderBy(f => rank.TryGetValue(f.GameKey, out var r) ? r : int.MaxValue)
            .ThenBy(f => f.ScreenshotPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static GameDefinition ResolveCustomFolderGame(CustomFolder custom, IReadOnlyList<GameDefinition> games)
    {
        if (!string.IsNullOrWhiteSpace(custom.GameKey))
        {
            var explicitGame = games.FirstOrDefault(g => g.Key == custom.GameKey);
            if (explicitGame is not null) return explicitGame;
        }

        // 用户可能直接选到了 <安装目录>\ScreenShot，因此从该目录向上回溯，寻找游戏本体。
        var dir = new DirectoryInfo(custom.Path);
        for (var i = 0; i < 3 && dir is not null; i++, dir = dir.Parent)
        {
            var found = GameCatalog.ByInstallDirectory(dir.FullName);
            if (found is not null) return found;
        }

        // 按路径关键字兜底。
        var lower = custom.Path.ToLowerInvariant();
        foreach (var g in games)
        {
            if (lower.Contains(g.DisplayName.ToLowerInvariant())) return g;
        }

        return UnknownGame;
    }

    public static readonly GameDefinition UnknownGame = new(
        key: "other",
        displayName: "其他截图",
        bizPrefix: string.Empty,
        executableNames: Array.Empty<string>(),
        screenshotSubPaths: Array.Empty<string>(),
        glyph: "\uE8B7",
        accent: "#7A7A7A");

    private static GameDefinition? MatchByDisplayName(string displayName, IReadOnlyList<GameDefinition> games)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        foreach (var g in games)
        {
            if (displayName.Contains(g.DisplayName, StringComparison.OrdinalIgnoreCase))
                return g;
        }

        if (displayName.Contains("Genshin", StringComparison.OrdinalIgnoreCase)) return GameCatalog.ByKey("genshin");
        if (displayName.Contains("StarRail", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("Star Rail", StringComparison.OrdinalIgnoreCase)) return GameCatalog.ByKey("starrail");
        if (displayName.Contains("Zenless", StringComparison.OrdinalIgnoreCase)) return GameCatalog.ByKey("zzz");
        if (displayName.Contains("Honkai Impact", StringComparison.OrdinalIgnoreCase)) return GameCatalog.ByKey("hi3");
        return null;
    }

    /// <summary>按优先级返回第一个存在的截图候选目录；都不存在时返回第一个候选。</summary>
    public static string ResolveScreenshotPath(GameDefinition game, string installPath)
    {
        foreach (var sub in game.ScreenshotSubPaths)
        {
            try
            {
                var candidate = Path.Combine(installPath, sub);
                if (Directory.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        if (game.ScreenshotSubPaths.Length == 0) return installPath;

        try
        {
            return Path.Combine(installPath, game.ScreenshotSubPaths[0]);
        }
        catch (ArgumentException)
        {
            return installPath;
        }
    }

    // ------------------------------------------------------------------
    // HoYoPlay 状态库
    // ------------------------------------------------------------------

    private static readonly string[] HoyoPlayBases =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "miHoYo"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cognosphere"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "miHoYo"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cognosphere"),
    };

    private static IEnumerable<(string InstallPath, string Biz, bool IsPrimary)> ReadHoyoPlayInstallPaths(CancellationToken ct)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseDir in HoyoPlayBases)
        {
            var hypDir = Path.Combine(baseDir, "HYP");
            if (!SafeDirectoryExists(hypDir)) continue;

            foreach (var file in EnumerateGamedataFiles(hypDir, ct))
            {
                // HYP\<版本>\data\gamedata.dat 视为当前生效状态的“主”来源，
                // beta\standalone\... 视为测试服来源（要求截图目录真实存在才采用）。
                var isPrimary = !file.Contains(@"\beta\", StringComparison.OrdinalIgnoreCase);

                string text;
                try
                {
                    text = ReadAllTextShared(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var (biz, installPath) in ParseGamedata(text))
                {
                    var key = biz + "|" + installPath;
                    if (!yielded.Add(key)) continue;
                    yield return (installPath, biz, isPrimary);
                }
            }
        }
    }

    /// <summary>在 HYP 目录下浅层搜索 gamedata.dat，跳过体积巨大的浏览器缓存目录。</summary>
    private static IEnumerable<string> EnumerateGamedataFiles(string root, CancellationToken ct)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fedata", "cache", "Cache", "modules", "logs", "crash", "CrashDumps",
            "fepak", "ico", "patch", "report", "languages", "config", "gameData",
        };

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "gamedata.dat");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var f in files) yield return f;

            if (depth >= 5) continue;

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (skip.Contains(name)) continue;
                queue.Enqueue((sub, depth + 1));
            }
        }
    }

    /// <summary>
    /// gamedata.dat 是若干“长度前缀 + JSON 对象”的拼接，且文件可能正被启动器占用，
    /// 因此以共享方式读取，并用括号配平截出完整的 JSON 对象后再解析。
    /// </summary>
    internal static IEnumerable<(string Biz, string InstallPath)> ParseGamedata(string text)
    {
        const string marker = "{\"download_transaction_no\"";
        var index = 0;

        while (true)
        {
            var start = text.IndexOf(marker, index, StringComparison.Ordinal);
            if (start < 0) yield break;

            var end = FindMatchingBrace(text, start);
            if (end < 0) yield break;

            index = end + 1;

            var slice = text.AsSpan(start, end - start + 1);
            string? biz = null;
            string? installPath = null;
            try
            {
                using var doc = JsonDocument.Parse(slice.ToString());
                var root = doc.RootElement;
                if (root.TryGetProperty("gameBiz", out var b)) biz = b.GetString();
                if (root.TryGetProperty("installPath", out var p)) installPath = p.GetString();
            }
            catch (JsonException)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(biz) && !string.IsNullOrWhiteSpace(installPath))
                yield return (biz, installPath);
        }
    }

    private static int FindMatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped) { escaped = false; continue; }

            if (inString)
            {
                if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    /// <summary>以共享读写方式读取被启动器占用的文件。</summary>
    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // ------------------------------------------------------------------
    // 注册表
    // ------------------------------------------------------------------

    private static IEnumerable<(string DisplayName, string InstallLocation)> ReadUninstallEntries(CancellationToken ct)
    {
        var roots = new (RegistryKey Hive, string SubKey)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, subKey) in roots)
        {
            ct.ThrowIfCancellationRequested();
            RegistryKey? root = null;
            try
            {
                root = hive.OpenSubKey(subKey);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
            }

            if (root is null) continue;

            using (root)
            {
                foreach (var name in root.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();

                    RegistryKey? entry = null;
                    var display = string.Empty;
                    var location = string.Empty;
                    var isHoyoGame = false;

                    // C# 不允许在带 catch 的 try 里 yield，因此先把值读出来再返回。
                    try
                    {
                        entry = root.OpenSubKey(name);
                        if (entry is not null)
                        {
                            display = entry.GetValue("DisplayName") as string ?? string.Empty;
                            location = entry.GetValue("InstallLocation") as string ?? string.Empty;
                            isHoyoGame = LooksLikeHoyoGame(display);
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                    }
                    finally
                    {
                        entry?.Dispose();
                    }

                    if (isHoyoGame)
                        yield return (display, location);
                }
            }
        }
    }

    private static bool LooksLikeHoyoGame(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;

        ReadOnlySpan<string> markers =
        [
            "原神", "Genshin", "星穹铁道", "Star Rail", "StarRail",
            "绝区零", "Zenless", "崩坏3", "Honkai Impact",
        ];

        foreach (var m in markers)
        {
            if (displayName.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // 磁盘扫描
    // ------------------------------------------------------------------

    private static IEnumerable<string> FindInstallDirectories(
        IReadOnlyList<GameDefinition> games,
        CancellationToken ct)
    {
        foreach (var drive in GetFixedDrives())
        {
            ct.ThrowIfCancellationRequested();

            foreach (var dir in EnumerateRoots(drive, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (GameCatalog.ByInstallDirectory(dir) is not null)
                    yield return dir;
            }
        }
    }

    private static IEnumerable<string> GetFixedDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var d in drives)
        {
            var isFixed = false;
            try
            {
                isFixed = d.DriveType == DriveType.Fixed && d.IsReady;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            if (isFixed) yield return d.RootDirectory.FullName;
        }
    }

    /// <summary>
    /// 只做“根目录一层 + 常见容器目录两层”的浅层枚举，避免整盘遍历。
    /// </summary>
    private static IEnumerable<string> EnumerateRoots(string driveRoot, CancellationToken ct)
    {
        string[] level1;
        try
        {
            level1 = Directory.GetDirectories(driveRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var dir in level1)
        {
            ct.ThrowIfCancellationRequested();
            yield return dir;

            var name = Path.GetFileName(dir);
            var shouldDescend = ContainerDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                                || GameishKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

            if (!shouldDescend) continue;

            string[] level2;
            try
            {
                level2 = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var sub in level2)
            {
                ct.ThrowIfCancellationRequested();
                yield return sub;
            }
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
