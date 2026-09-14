namespace GameGallery.Models;

/// <summary>
/// 一个受支持游戏的静态描述：如何识别它的安装目录，以及截图会落在哪里。
/// </summary>
public sealed class GameDefinition
{
    public GameDefinition(
        string key,
        string displayName,
        string bizPrefix,
        string[] executableNames,
        string[] screenshotSubPaths,
        string glyph,
        string accent)
    {
        Key = key;
        DisplayName = displayName;
        BizPrefix = bizPrefix;
        ExecutableNames = executableNames;
        ScreenshotSubPaths = screenshotSubPaths;
        Glyph = glyph;
        Accent = accent;
    }

    /// <summary>稳定标识，用于设置持久化。</summary>
    public string Key { get; }

    public string DisplayName { get; }

    /// <summary>HoYoPlay 的 gameBiz 前缀，例如 hk4e / hkrpg / nap / bh3。</summary>
    public string BizPrefix { get; }

    /// <summary>安装目录中必定存在的可执行文件，用于校验目录确实是该游戏。</summary>
    public string[] ExecutableNames { get; }

    /// <summary>相对于安装目录的截图子路径候选，按优先级排列。</summary>
    public string[] ScreenshotSubPaths { get; }

    /// <summary>Segoe Fluent Icons 字形。</summary>
    public string Glyph { get; }

    /// <summary>标签页强调色。</summary>
    public string Accent { get; }
}

public static class GameCatalog
{
    public static IReadOnlyList<GameDefinition> All { get; } = new[]
    {
        new GameDefinition(
            key: "genshin",
            displayName: "原神",
            bizPrefix: "hk4e",
            executableNames: new[] { "YuanShen.exe", "GenshinImpact.exe" },
            screenshotSubPaths: new[]
            {
                "ScreenShot",
                @"Genshin Impact Game\ScreenShot",
                @"YuanShen_Data\ScreenShot",
            },
            glyph: "\uE8B9",
            accent: "#4A9EFF"),

        new GameDefinition(
            key: "starrail",
            displayName: "崩坏：星穹铁道",
            bizPrefix: "hkrpg",
            executableNames: new[] { "StarRail.exe" },
            screenshotSubPaths: new[]
            {
                @"StarRail_Data\ScreenShots",
                @"StarRail_Data\ScreenShot",
                "ScreenShot",
            },
            glyph: "\uE7F4",
            accent: "#B47CFF"),

        new GameDefinition(
            key: "zzz",
            displayName: "绝区零",
            bizPrefix: "nap",
            executableNames: new[] { "ZenlessZoneZero.exe" },
            screenshotSubPaths: new[]
            {
                "ScreenShot",
                @"ZenlessZoneZero_Data\ScreenShot",
            },
            glyph: "\uE7FC",
            accent: "#FFB03A"),

        new GameDefinition(
            key: "hi3",
            displayName: "崩坏3",
            bizPrefix: "bh3",
            executableNames: new[] { "BH3.exe" },
            screenshotSubPaths: new[] { "ScreenShot" },
            glyph: "\uE734",
            accent: "#FF6B81"),
    };

    public static GameDefinition? ByKey(string key)
        => All.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>根据 HoYoPlay 的 gameBiz（如 hk4e_cn / hkrpg_global）反查游戏。</summary>
    public static GameDefinition? ByBiz(string biz)
        => All.FirstOrDefault(g => biz.StartsWith(g.BizPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>根据安装目录中的可执行文件反查游戏。</summary>
    public static GameDefinition? ByInstallDirectory(string directory)
    {
        foreach (var game in All)
        {
            foreach (var exe in game.ExecutableNames)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, exe)))
                        return game;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return null;
    }
}
