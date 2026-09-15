using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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

    /// <summary>没查出来（网络不可用、读取失败等）。</summary>
    Unknown,
}

public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string CurrentVersion,
    string? LatestVersion,
    string? LatestTag,
    string Message,
    string Url);

/// <summary>下载下来的安装包没通过哈希校验。</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>
/// 检查更新 / 下载更新。
///
/// 版本号按五路探测，前四路是「正式发布 / 标签」，CHANGELOG 只在前四路都拿不到时才用：
///   1. api.github.com/repos/.../releases/latest —— 最理想（JSON + 发布页地址）
///   2. api.github.com/repos/.../tags
///   3. github.com/.../releases/latest 的 302 —— 有 Release 时 Location 指向 /releases/tag/&lt;tag&gt;
///   4. github.com/.../tags 页面的 HTML
///   5. CHANGELOG.md 里第一个 <c>## [x.y.z]</c> 标题 —— 保底方案，仓库还没发 Release / 打了 tag 时也能工作
///
/// 单独任何一路都会在某些网络下失效：api.github.com 未认证请求按出口 IP 限流（每小时 60 次，实测经常 403）；
/// raw.githubusercontent.com 在部分网络下不可达（本次实测时通时不通）；
/// github.com 的 blob 页面里内嵌了文件原文（rawLines），可以当纯文本解析。
///
/// 文件哈希的来源是 <c>hashes</c> 分支下的 <c>&lt;版本&gt;.txt</c>，格式为每行
/// <c>&lt;sha256&gt;  &lt;文件名&gt;</c>（由发布工作流在推 tag 时自动生成）。
/// </summary>
public static class UpdateService
{
    public const string Owner = "CaiBai-Fish";
    public const string Repo = "game-gallery";

    public static string RepositoryUrl => $"https://github.com/{Owner}/{Repo}";

    public static string ReleasesPageUrl => $"{RepositoryUrl}/releases";

    public static string ChangelogUrl => $"{RepositoryUrl}/blob/main/CHANGELOG.md";

    /// <summary>当前程序版本，取自程序集信息（csproj 的 &lt;Version&gt;）。</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    private static readonly HttpClient Http = CreateClient(allowRedirect: true);

    private static readonly HttpClient NoRedirect = CreateClient(allowRedirect: false);

    // ------------------------------------------------------------------
    // 检查更新
    // ------------------------------------------------------------------

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        string? tag = null;

        foreach (var (name, probe) in VersionProbes())
        {
            try
            {
                var found = await probe(cancellationToken);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    tag = found.Trim();
                    App.Log($"检查更新：从「{name}」读到 {tag}");
                    break;
                }

                failures.Add($"{name} — 没有可用结果");
            }
            catch (Exception ex)
            {
                failures.Add($"{name} — {Describe(ex)}");
            }
        }

        if (tag is null)
        {
            var detail = failures.Count == 0 ? "没有可用的数据源" : string.Join("；", failures);
            return new UpdateCheckResult(
                UpdateCheckOutcome.Unknown,
                CurrentVersion,
                null,
                null,
                $"检查更新失败：{detail}。可以手动打开仓库查看：",
                ReleasesPageUrl);
        }

        return Compare(tag);
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
                tag,
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
                tag,
                $"发现新版本 {latest}（当前 {current}）。",
                $"{ReleasesPageUrl}/tag/{tag}");
        }

        if (comparison == 0)
        {
            return new UpdateCheckResult(
                UpdateCheckOutcome.UpToDate,
                current,
                latest,
                tag,
                $"已是最新版本（{current}）。",
                ReleasesPageUrl);
        }

        return new UpdateCheckResult(
            UpdateCheckOutcome.AheadOfRepo,
            current,
            latest,
            tag,
            $"当前版本 {current} 比仓库里的 {latest} 还新（开发版）。",
            ReleasesPageUrl);
    }

    /// <summary>版本号的五路探测，按顺序尝试，第一个拿到结果的胜出。</summary>
    private static (string Name, Func<CancellationToken, Task<string?>> Probe)[] VersionProbes() =>
    [
        ("GitHub API 的 releases/latest", TryApiLatestReleaseAsync),
        ("GitHub API 的 tags", TryApiTagsAsync),
        ("github.com 的 releases/latest 跳转", TryReleaseRedirectAsync),
        ("github.com 的 tags 页面", TryTagsPageAsync),
        ("CHANGELOG.md（保底）", TryChangelogVersionAsync),
    ];

    private static async Task<string?> TryApiLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync($"{ApiBase}/releases/latest", cancellationToken);

        // 仓库还没有正式发布时是 404，属于"这一路没有答案"，交给下一路
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
    }

    private static async Task<string?> TryApiTagsAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync($"{ApiBase}/tags", cancellationToken);
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

        // 有正式发布时跳到 /releases/tag/<tag>；没有时跳到 /releases，正则自然匹配不到
        var match = Regex.Match(location.ToString(), @"/releases/tag/([^/?#]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private static async Task<string?> TryTagsPageAsync(CancellationToken cancellationToken)
    {
        var html = await Http.GetStringAsync($"{RepositoryUrl}/tags", cancellationToken);
        var match = Regex.Match(html, @"/releases/tag/([^""'?#]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// 保底：从 CHANGELOG.md 的第一个版本标题取版本号。
    /// 仓库还没发 Release、也没打 tag 时，前四路都拿不到东西，这里至少有 CHANGELOG。
    /// </summary>
    private static async Task<string?> TryChangelogVersionAsync(CancellationToken cancellationToken)
    {
        var sources = new (string Name, Func<CancellationToken, Task<string>> Read)[]
        {
            ("CHANGELOG.md @ raw", ct => Http.GetStringAsync($"{RawBase}/main/CHANGELOG.md", ct)),
            ("CHANGELOG.md @ blob 页面", ct => Http.GetStringAsync($"{RepositoryUrl}/blob/main/CHANGELOG.md", ct)),
            ("CHANGELOG.md @ api", ct => ReadApiFileAsync("CHANGELOG.md", "main", ct)),
        };

        foreach (var (name, read) in sources)
        {
            try
            {
                var text = await read(cancellationToken);
                var version = ParseFirstVersion(text);
                if (version is not null)
                {
                    App.Log($"检查更新：CHANGELOG 保底读到版本 {version}（来源：{name}）");
                    return version;
                }
            }
            catch (Exception ex)
            {
                App.Log($"检查更新：{name} 读取失败 — {Describe(ex)}");
            }
        }

        return null;
    }

    /// <summary>取 CHANGELOG 里的第一个版本标题，例如 <c>## [0.1.1] - 2026-09-15</c> → 0.1.1。</summary>
    internal static string? ParseFirstVersion(string text)
    {
        foreach (Match match in Regex.Matches(text, @"(?m)^##\s*\[?(?<v>\d+\.\d+(?:\.\d+)?)\]?"))
        {
            return match.Groups["v"].Value;
        }

        // blob 页面里原始内容是 JSON 字符串数组，行首可能带转义/缩进，再宽松匹配一次
        var loose = Regex.Match(text, @"##\s*\[(?<v>\d+\.\d+(?:\.\d+)?)\]");
        return loose.Success ? loose.Groups["v"].Value : null;
    }

    // ------------------------------------------------------------------
    // 下载并校验安装包
    // ------------------------------------------------------------------

    /// <summary>
    /// 下载指定版本的 MSI，核对哈希后返回本地路径。
    /// <paramref name="tag"/> 是 Release 上的原始标签（可能是 v0.1.2），文件名用的是去掉 v 的版本号。
    /// 校验失败会删掉文件并抛 <see cref="UpdateVerificationException"/>。
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(
        string version,
        string? tag = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fileName = $"GameGallery-{version}.msi";
        var directory = Path.Combine(Path.GetTempPath(), "GameGallery-update");
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, fileName);
        if (File.Exists(target)) File.Delete(target);

        var url = $"{RepositoryUrl}/releases/download/{tag ?? version}/{fileName}";
        App.Log($"更新：开始下载 {url}");

        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(target);

            var buffer = new byte[81920];
            long copied = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                if (total > 0) progress?.Report((double)copied / total);
            }
        }

        try
        {
            await VerifyAsync(target, fileName, version, cancellationToken);
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) { }
            throw;
        }

        App.Log($"更新：{fileName} 下载并校验通过");
        return target;
    }

    /// <summary>把本地文件的 SHA-256 与 hashes 分支里的清单核对。</summary>
    private static async Task VerifyAsync(string path, string fileName, string version, CancellationToken cancellationToken)
    {
        var expected = await GetExpectedHashAsync(version, fileName, cancellationToken);

        if (expected is null)
        {
            throw new UpdateVerificationException(
                $"拿不到 {version} 的哈希清单（hashes 分支下的 {version}.txt），为安全起见不自动安装。" +
                "请用「打开发布页」手动下载。");
        }

        var actual = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }, cancellationToken);

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateVerificationException(
                $"安装包哈希不匹配，已删除下载的文件。\n期望 {expected}\n实际 {actual}");
        }
    }

    /// <summary>从 hashes 分支取某个版本的文件名 → SHA-256 映射。</summary>
    private static async Task<string?> GetExpectedHashAsync(string version, string fileName, CancellationToken cancellationToken)
    {
        var sources = new (string Name, Func<CancellationToken, Task<string>> Read)[]
        {
            ("raw.githubusercontent.com", ct => Http.GetStringAsync($"{RawBase}/hashes/{version}.txt", ct)),
            ("github.com 的 blob 页面", ct => Http.GetStringAsync($"{RepositoryUrl}/blob/hashes/{version}.txt", ct)),
            ("api.github.com", ct => ReadApiFileAsync($"{version}.txt", "hashes", ct)),
        };

        foreach (var (name, read) in sources)
        {
            try
            {
                var text = await read(cancellationToken);
                var hash = ParseHash(text, fileName);
                if (hash is not null)
                {
                    App.Log($"更新：从「{name}」取到 {fileName} 的哈希 {hash}");
                    return hash;
                }
            }
            catch (Exception ex)
            {
                App.Log($"更新：从「{name}」取哈希失败 — {Describe(ex)}");
            }
        }

        return null;
    }

    /// <summary>解析 <c>&lt;sha256&gt;  &lt;文件名&gt;</c> 行。</summary>
    internal static string? ParseHash(string text, string fileName)
    {
        foreach (Match match in Regex.Matches(text, @"(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>[^\s""\\]+)"))
        {
            var name = match.Groups["name"].Value;
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return match.Groups["hash"].Value.ToLowerInvariant();
        }

        return null;
    }

    /// <summary>用 msiexec 安装下载好的 MSI（per-user 安装，不需要管理员）。</summary>
    public static bool RunInstaller(string msiPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{msiPath}\"",
                UseShellExecute = true,
            };

            return System.Diagnostics.Process.Start(startInfo) is not null;
        }
        catch (Exception ex)
        {
            App.Log("更新：启动安装程序失败 — " + ex);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 内部工具
    // ------------------------------------------------------------------

    private static string ApiBase => $"https://api.github.com/repos/{Owner}/{Repo}";

    private static string RawBase => $"https://raw.githubusercontent.com/{Owner}/{Repo}";

    /// <summary>用 GitHub contents 接口取文件原文（限流时会 403，交给调用方换数据源）。</summary>
    private static async Task<string> ReadApiFileAsync(string path, string reference, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/contents/{path}?ref={reference}");
        request.Headers.Accept.ParseAdd("application/vnd.github.raw");

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string NormalizeVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        return value;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var match = Regex.Match(NormalizeVersion(value), @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?");
        if (!match.Success) return false;

        var parts = new List<int>();
        for (var i = 1; i <= 4; i++)
        {
            if (!match.Groups[i].Success) break;
            parts.Add(int.Parse(match.Groups[i].Value));
        }

        while (parts.Count < 4) parts.Add(0);
        version = new Version(parts[0], parts[1], parts[2], parts[3]);
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
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GameGallery/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, text/html;q=0.9, */*;q=0.8");
        return client;
    }

    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "接口返回 403（GitHub 未认证请求配额用尽）",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "文件不存在（404）",
        HttpRequestException { StatusCode: { } code } => $"接口返回 {(int)code}",
        TaskCanceledException => "请求超时",
        HttpRequestException => "网络不可达",
        _ => exception.Message,
    };
}
