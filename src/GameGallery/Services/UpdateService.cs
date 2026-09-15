using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameGallery.Services;

public enum UpdateCheckOutcome
{
    /// <summary>已是最新。</summary>
    UpToDate,

    /// <summary>仓库里有更新的版本。</summary>
    UpdateAvailable,

    /// <summary>本地版本比仓库里的还新（开发版）。</summary>
    AheadOfRepo,

    /// <summary>没查出来（网络不可用、仓库还没有发布等）。</summary>
    Unknown,
}

public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string CurrentVersion,
    string? LatestVersion,
    string Message,
    string Url);

/// <summary>
/// 检查 GitHub 上有没有新版本。
///
/// 这里刻意做了四路探测，因为单独任何一路都会在某些网络下失效：
///   1. api.github.com/repos/.../releases/latest —— 最理想（JSON、带发布页地址），但未认证请求
///      只有每小时 60 次，而且配额按出口 IP 算。实测本机出口 IP 的配额经常是 0（HTTP 403）。
///   2. api.github.com/repos/.../tags —— 同上，作为没有正式 Release 时的来源。
///   3. github.com/.../releases/latest 的 302 —— 有 Release 时 Location 指向 /releases/tag/&lt;tag&gt;，
///      没有 Release 时指向 /releases。不受 API 配额限制。
///   4. github.com/.../tags 页面的 HTML —— 抓 /releases/tag/&lt;tag&gt; 链接，同样不受配额限制。
/// raw.githubusercontent.com 本来最省事，但在部分网络下不可达，因此没有采用。
/// </summary>
public static class UpdateService
{
    public const string Owner = "CaiBai-Fish";
    public const string Repo = "game-gallery";

    public static string RepositoryUrl => $"https://github.com/{Owner}/{Repo}";

    public static string ReleasesPageUrl => $"{RepositoryUrl}/releases";

    public static string ChangelogUrl => $"{RepositoryUrl}/blob/main/CHANGELOG.md";

    /// <summary>当前程序版本，取自程序集信息（&lt;Version&gt; 属性）。</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    private static readonly HttpClient Api = CreateClient(allowRedirect: true);

    private static readonly HttpClient NoRedirect = CreateClient(allowRedirect: false);

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var probes = new (string Name, Func<CancellationToken, Task<string?>> Run)[]
        {
            ("GitHub API 的 releases/latest", TryApiLatestReleaseAsync),
            ("GitHub API 的 tags", TryApiTagsAsync),
            ("github.com 的 releases/latest 跳转", TryReleaseRedirectAsync),
            ("github.com 的 tags 页面", TryTagsPageAsync),
        };

        var failures = new List<string>();

        foreach (var (name, run) in probes)
        {
            try
            {
                var tag = await run(cancellationToken);
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    App.Log($"检查更新：从「{name}」读到 {tag}");
                    return Compare(tag.Trim());
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{name} — {Describe(ex)}");
            }
        }

        var detail = failures.Count == 0
            ? "仓库里既没有正式发布也没有标签"
            : string.Join("；", failures);

        return new UpdateCheckResult(
            UpdateCheckOutcome.Unknown,
            CurrentVersion,
            null,
            $"检查更新失败：{detail}。可以手动打开仓库查看：",
            ReleasesPageUrl);
    }

    private static UpdateCheckResult Compare(string tag)
    {
        var latest = NormalizeVersion(tag);
        var current = CurrentVersion;

        if (!TryParseVersion(latest, out var latestVersion) || !TryParseVersion(current, out var currentVersion))
        {
            return new UpdateCheckResult(
                UpdateCheckOutcome.Unknown,
                current,
                latest,
                $"读取到仓库版本 {latest}，但无法与当前版本 {current} 比较。",
                ReleasesPageUrl);
        }

        var comparison = latestVersion.CompareTo(currentVersion);

        if (comparison > 0)
        {
            return new UpdateCheckResult(
                UpdateCheckOutcome.UpdateAvailable,
                current,
                latest,
                $"发现新版本 {latest}（当前 {current}）。",
                ReleasesPageUrl);
        }

        if (comparison == 0)
        {
            return new UpdateCheckResult(
                UpdateCheckOutcome.UpToDate,
                current,
                latest,
                $"已是最新版本（{current}）。",
                ReleasesPageUrl);
        }

        return new UpdateCheckResult(
            UpdateCheckOutcome.AheadOfRepo,
            current,
            latest,
            $"当前版本 {current} 比仓库里的 {latest} 还新（开发版）。",
            ReleasesPageUrl);
    }

    // ------------------------------------------------------------------
    // 四路探测
    // ------------------------------------------------------------------

    private static async Task<string?> TryApiLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await Api.GetAsync($"{ApiBase}/releases/latest", cancellationToken);

        // 仓库还没有正式发布时是 404，属于"这一路没有答案"，交给下一路。
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
    }

    private static async Task<string?> TryApiTagsAsync(CancellationToken cancellationToken)
    {
        using var response = await Api.GetAsync($"{ApiBase}/tags", cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("name", out var name)) return name.GetString();
        }

        return null;
    }

    private static async Task<string?> TryReleaseRedirectAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{RepositoryUrl}/releases/latest");
        using var response = await NoRedirect.SendAsync(request, cancellationToken);

        var location = response.Headers.Location;
        if (location is null) return null;

        // 有正式发布时跳到 /releases/tag/<tag>；没有时跳到 /releases，正则自然匹配不到。
        var match = Regex.Match(location.ToString(), @"/releases/tag/([^/?#]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private static async Task<string?> TryTagsPageAsync(CancellationToken cancellationToken)
    {
        var html = await Api.GetStringAsync($"{RepositoryUrl}/tags", cancellationToken);
        var match = Regex.Match(html, @"/releases/tag/([^""'?#]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    // ------------------------------------------------------------------
    // 版本号处理
    // ------------------------------------------------------------------

    private static string ApiBase => $"https://api.github.com/repos/{Owner}/{Repo}";

    /// <summary>去掉 tag 前缀并保留原始字符串（用于展示），例如 "v0.1.0" → "0.1.0"。</summary>
    private static string NormalizeVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        return value;
    }

    /// <summary>只取数字主体比较，"0.1.1-beta.2" 按 0.1.1 处理。</summary>
    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0);
        var match = Regex.Match(value, @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?");
        if (!match.Success) return false;

        var parts = new List<int>();
        for (var i = 1; i <= 4; i++)
        {
            if (!match.Groups[i].Success) break;
            parts.Add(int.Parse(match.Groups[i].Value));
        }

        while (parts.Count < 2) parts.Add(0);
        version = new Version(parts[0], parts[1], parts.Count > 2 ? parts[2] : 0, parts.Count > 3 ? parts[3] : 0);
        return true;
    }

    private static string ReadCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');       // SDK 会附加 +<commit sha>
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static HttpClient CreateClient(bool allowRedirect)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = allowRedirect };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GameGallery/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, text/html;q=0.9, */*;q=0.8");
        return client;
    }

    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "接口返回 403（GitHub 未认证请求配额用尽）",
        HttpRequestException { StatusCode: { } code } => $"接口返回 {(int)code}",
        TaskCanceledException => "请求超时",
        HttpRequestException => "网络不可达",
        _ => exception.Message,
    };
}
