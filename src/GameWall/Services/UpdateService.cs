using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameWall.Services;

/// <summary>一次更新检查的结果里，描述「新版本」的那部分。</summary>
public sealed class UpdateInfo
{
    /// <summary>版本号（已去掉 tag 的 v 前缀），如 1.6.0</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>原始 tag，如 v1.6.0</summary>
    public string TagName { get; init; } = string.Empty;

    /// <summary>Release 标题</summary>
    public string ReleaseName { get; init; } = string.Empty;

    /// <summary>Release 说明（markdown 原文）</summary>
    public string ReleaseNotes { get; init; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>官方下载地址（github.com 直连）</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>资产大小（字节），0 表示未知</summary>
    public long Size { get; init; }

    /// <summary>资产 SHA256（小写十六进制，不带 sha256: 前缀），null 表示 GitHub 没给</summary>
    public string? Sha256 { get; init; }

    /// <summary>这次元数据是从哪个源拿到的（诊断用）</summary>
    public string MetadataSource { get; init; } = string.Empty;

    /// <summary>发布说明的纯文本版（去掉 markdown 记号，给对话框用）</summary>
    public string NotesPlainText => UpdateService.PlainText(ReleaseNotes);
}

/// <summary>检查更新的结果。</summary>
public sealed class UpdateCheckResult
{
    /// <summary>是否成功拿到 GitHub 上的版本信息</summary>
    public bool Success { get; init; }

    /// <summary>是否有比当前更新的版本</summary>
    public bool HasUpdate { get; init; }

    /// <summary>新版本信息（HasUpdate 为 true 时有效）</summary>
    public UpdateInfo? Info { get; init; }

    /// <summary>当前版本</summary>
    public string CurrentVersion { get; init; } = AppInfo.Version;

    /// <summary>给用户看的一句话</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>失败原因（诊断用）</summary>
    public string? Error { get; init; }

    public static UpdateCheckResult Fail(string message, string? error = null) =>
        new() { Success = false, Message = message, Error = error };
}

/// <summary>下载进度。</summary>
public readonly struct UpdateDownloadProgress
{
    public UpdateDownloadProgress(long received, long total, double bytesPerSecond)
    {
        Received = received;
        Total = total;
        BytesPerSecond = bytesPerSecond;
    }

    public long Received { get; }

    /// <summary>总大小，0 表示服务端没给 Content-Length</summary>
    public long Total { get; }

    public double BytesPerSecond { get; }

    /// <summary>0~1，总大小未知时返回 0</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Received / Total, 0, 1);
}

/// <summary>
/// 检查更新 / 下载 / 自我替换。
///
/// 设计要点
///  1. 版本号以 GitHub Release 的 tag 为准（tag 就是发布时打进 exe 的版本号）。
///  2. 国内访问 github.com 经常被掐，所以元数据和下载都走「多源回退」：
///     直连失败就依次换国内加速镜像，谁先成功用谁，并把成功的源记在结果里。
///  3. 下载完必须校验：大小必须等于 GitHub 报的大小；如果 GitHub 给了资产 digest
///     （sha256:...），再比一次哈希，防止镜像返回被篡改或截断的文件。
///  4. 单文件发布才能自我替换：运行目录里没有同名 dll 才认为是单文件版。
/// </summary>
public static class UpdateService
{
    public const string SourceAuto = "auto";
    public const string SourceGithub = "github";

    private const string AssetName = "GameWall.exe";

    /// <summary>默认只检查 GameWall 自己的仓库</summary>
    private static readonly string LatestReleaseApi =
        $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest";

    private static readonly string ReleasesApi =
        $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases?per_page=20";

    /// <summary>
    /// 下载加速镜像。Prefix 直接拼在原始 URL 前面；
    /// 直连的 Prefix 是空字符串，排在第一位。
    /// </summary>
    private static readonly Mirror[] Mirrors =
    {
        new(SourceGithub, "GitHub 直连", string.Empty),
        new("ghfast", "ghfast.top", "https://ghfast.top/"),
        new("ghproxy", "ghproxy.net", "https://ghproxy.net/"),
        new("ghproxycom", "gh-proxy.com", "https://gh-proxy.com/"),
        new("moeyy", "github.moeyy.xyz", "https://github.moeyy.xyz/"),
        new("gitmirror", "hub.gitmirror.com", "https://hub.gitmirror.com/"),
    };

    /// <summary>
    /// 元数据（API）镜像。GitHub API 在部分网络下也会被重置，
    /// 所以 API 也走一遍回退：有的镜像支持 <c>{prefix}https://api.github.com/...</c>。
    /// </summary>
    private static readonly (string Source, string Prefix)[] ApiPrefixes =
    {
        ("github", string.Empty),
        ("ghfast", "https://ghfast.top/"),
        ("ghproxy", "https://ghproxy.net/"),
        ("ghproxycom", "https://gh-proxy.com/"),
        ("moeyy", "https://github.moeyy.xyz/"),
    };

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // 加速镜像会 302 到别的域名，跟随跳转是必须的
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        var client = new HttpClient(handler)
        {
            // 下载大文件时不要整体超时，靠 CancellationToken 控制
            Timeout = TimeSpan.FromMinutes(30),
        };

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GameWall", AppInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>是不是内置支持的更新源名字。</summary>
    public static bool IsKnownSource(string? source) =>
        string.IsNullOrWhiteSpace(source)
        || source.Equals(SourceAuto, StringComparison.OrdinalIgnoreCase)
        || Mirrors.Any(m => m.Key.Equals(source, StringComparison.OrdinalIgnoreCase));

    /// <summary>给设置界面下拉框用的源列表（key, 显示名）。</summary>
    public static IReadOnlyList<(string Key, string Display)> SourceOptions =>
        new List<(string, string)>
        {
            (SourceAuto, "自动（首选直连，失败切加速源）"),
        }.Concat(Mirrors.Select(m => (m.Key, m.Display))).ToList();

    /// <summary>
    /// 检查 GitHub 上有没有新版本。
    /// </summary>
    /// <param name="preferredSource">设置里的更新源；auto 时按顺序试</param>
    /// <param name="token">取消</param>
    /// <param name="log">进度/诊断信息</param>
    public static async Task<UpdateCheckResult> CheckAsync(
        string? preferredSource,
        CancellationToken token = default,
        IProgress<string>? log = null)
    {
        var errors = new List<string>();

        // 用户指定了某一个源就先试它，其余再按默认顺序补
        var candidates = OrderApiPrefixes(preferredSource);

        foreach (var (source, prefix) in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                log?.Report($"检查更新：尝试 {Describe(source)}…");
                var url = prefix + LatestReleaseApi;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));

                var json = await Client.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
                var info = ParseRelease(json, source);

                if (info is null)
                {
                    errors.Add($"{Describe(source)}：返回里没有可用的 exe 资产");
                    continue;
                }

                var hasUpdate = AppInfo.CompareVersions(info.Version, AppInfo.Version) > 0;
                var message = hasUpdate
                    ? $"发现新版本 v{info.Version}（当前 v{AppInfo.Version}）"
                    : $"已经是最新版本 v{AppInfo.Version}";

                Logger.Info($"检查更新成功（{Describe(source)}）：远端 v{info.Version} / 本地 v{AppInfo.Version} / hasUpdate={hasUpdate}");

                return new UpdateCheckResult
                {
                    Success = true,
                    HasUpdate = hasUpdate,
                    Info = info,
                    Message = message,
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{Describe(source)}：{Shorten(ex.Message)}");
                log?.Report($"{Describe(source)} 失败：{Shorten(ex.Message)}");
            }
        }

        Logger.Error("检查更新失败：" + string.Join(" | ", errors));
        return UpdateCheckResult.Fail("检查更新失败，可能是网络问题，稍后再试。", string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// 下载新版本到临时目录，返回本地文件路径。
    /// </summary>
    /// <param name="info">CheckAsync 得到的版本信息</param>
    /// <param name="preferredSource">设置里的更新源</param>
    /// <param name="progress">下载进度</param>
    /// <param name="token">取消</param>
    /// <param name="log">诊断信息</param>
    public static async Task<string> DownloadAsync(
        UpdateInfo info,
        string? preferredSource,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken token = default,
        IProgress<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            throw new InvalidOperationException("这个版本没有提供可下载的 exe。");
        }

        var targetDir = Path.Combine(Path.GetTempPath(), "GameWallUpdate");
        Directory.CreateDirectory(targetDir);

        // 清掉上次没成功的残留
        foreach (var stale in Directory.EnumerateFiles(targetDir, "*.part"))
        {
            TryDelete(stale);
        }

        var target = Path.Combine(targetDir, $"GameWall-{info.Version}.exe");
        var errors = new List<string>();

        foreach (var mirror in OrderMirrors(preferredSource))
        {
            token.ThrowIfCancellationRequested();
            var url = mirror.Prefix + info.DownloadUrl;
            var part = target + ".part";

            try
            {
                log?.Report($"下载：{mirror.Display}…");
                Logger.Info($"开始下载 {info.Version}：{mirror.Display}");

                var received = await DownloadToFileAsync(url, part, info.Size, progress, token).ConfigureAwait(false);

                // 校验大小：镜像截断/返回 HTML 错误页时大小一定不对
                if (info.Size > 0 && received != info.Size)
                {
                    throw new IOException($"文件大小不对（收到 {received} 字节，应为 {info.Size} 字节）");
                }

                // 校验哈希：GitHub 从 2024 起会给资产 digest，有就必须对得上
                if (!string.IsNullOrWhiteSpace(info.Sha256))
                {
                    var actual = await ComputeSha256Async(part, token).ConfigureAwait(false);
                    if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException($"SHA256 校验失败（收到 {actual[..12]}…，应为 {info.Sha256[..12]}…）");
                    }
                }

                if (File.Exists(target))
                {
                    TryDelete(target);
                }

                File.Move(part, target);
                Logger.Info($"下载完成：{target}（{received} 字节，源 {mirror.Display}）");
                return target;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                TryDelete(part);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(part);
                errors.Add($"{mirror.Display}：{Shorten(ex.Message)}");
                log?.Report($"{mirror.Display} 失败：{Shorten(ex.Message)}");
            }
        }

        Logger.Error("所有下载源都失败：" + string.Join(" | ", errors));
        throw new IOException(
            "所有下载源都失败了，稍后再试，或者在设置里换一个更新源。\r\n\r\n"
            + string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// 用刚下载的 exe 替换当前程序并重启。
    /// 返回 true 表示已经拉起替换脚本，调用方应当立即退出应用。
    /// </summary>
    /// <param name="downloadedExe">DownloadAsync 返回的路径</param>
    /// <param name="error">失败原因</param>
    public static bool TryApplyAndRestart(string downloadedExe, out string error)
    {
        error = string.Empty;

        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !current.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "拿不到当前程序路径，请手动下载新版本。";
            return false;
        }

        if (!AppInfo.IsSingleFileBuild)
        {
            error = "当前是从源码构建的开发版本，自动替换不安全。请重新发布单文件版本，或手动下载。";
            return false;
        }

        if (!File.Exists(downloadedExe))
        {
            error = "下载的文件不见了，请重新下载。";
            return false;
        }

        try
        {
            // 先放到 exe 同目录（同分区才能原子替换），没有写权限再退回临时目录
            var dir = Path.GetDirectoryName(current)!;
            var staged = Path.Combine(dir, Path.GetFileNameWithoutExtension(current) + ".update.exe");

            try
            {
                File.Copy(downloadedExe, staged, overwrite: true);
            }
            catch (UnauthorizedAccessException)
            {
                staged = downloadedExe;
            }
            catch (IOException)
            {
                staged = downloadedExe;
            }

            var script = Path.Combine(Path.GetTempPath(), "gamewall-update.cmd");
            File.WriteAllText(script, BuildSwapScript(), Encoding.ASCII);
            Logger.Info($"准备自我替换：{staged} -> {current}");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{script}\" {Environment.ProcessId} \"{staged}\" \"{current}\"\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("启动更新脚本失败", ex);
            error = "启动更新脚本失败：" + ex.Message;
            return false;
        }
    }

    // ── 内部实现 ────────────────────────────────────────────

    /// <summary>
    /// 替换脚本：等旧进程退出 → 覆盖文件 → 启动新版本 → 删掉自己。
    /// 全部用 ASCII 写，避免 cmd 的代码页把中文变乱码导致语法错误。
    /// </summary>
    private static string BuildSwapScript() =>
        string.Join("\r\n", new[]
        {
            "@echo off",
            "setlocal",
            "set OLDPID=%1",
            "set SRC=%~2",
            "set DST=%~3",
            "set /a N=0",
            ":wait",
            "tasklist /FI \"PID eq %OLDPID%\" 2>nul | find \"%OLDPID%\" >nul",
            "if errorlevel 1 goto swap",
            "set /a N+=1",
            "if %N% GEQ 40 goto force",
            "ping -n 2 127.0.0.1 >nul",
            "goto wait",
            ":force",
            "taskkill /PID %OLDPID% /F >nul 2>&1",
            "ping -n 3 127.0.0.1 >nul",
            ":swap",
            "move /Y \"%SRC%\" \"%DST%\" >nul 2>&1",
            "if errorlevel 1 (",
            "  ping -n 3 127.0.0.1 >nul",
            "  move /Y \"%SRC%\" \"%DST%\" >nul 2>&1",
            ")",
            "start \"\" \"%DST%\"",
            "(goto) 2>nul & del \"%~f0\"",
            string.Empty,
        });

    private static async Task<long> DownloadToFileAsync(
        string url,
        string path,
        long expectedSize,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken token)
    {
        using var response = await Client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;

        // 有些镜像对不存在的文件返回 200 + HTML，这里靠 Content-Type 先挡一道
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("下载源返回的是网页而不是文件");
        }

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long received = 0;
        var watch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            received += read;

            // 每 200ms 汇报一次，别让 UI 线程被进度回调刷屏
            if (watch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200))
            {
                lastReport = watch.Elapsed;
                var speed = watch.Elapsed.TotalSeconds > 0 ? received / watch.Elapsed.TotalSeconds : 0;
                progress?.Report(new UpdateDownloadProgress(received, total, speed));
            }
        }

        await destination.FlushAsync(token).ConfigureAwait(false);

        var finalSpeed = watch.Elapsed.TotalSeconds > 0 ? received / watch.Elapsed.TotalSeconds : 0;
        progress?.Report(new UpdateDownloadProgress(received, total, finalSpeed));
        return received;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static UpdateInfo? ParseRelease(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
        var version = AppInfo.ParseVersion(tag)?.ToString();
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? picked = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
            {
                picked = asset;
                break;
            }

            // 名字里带 .exe 的也算，方便以后改资产名
            if (picked is null && name is not null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                picked = asset;
            }
        }

        if (picked is not { } chosen)
        {
            return null;
        }

        var url = chosen.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        long size = chosen.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s) ? s : 0;

        // GitHub 从 2024 年起会给资产 digest："sha256:xxxx"
        string? sha = null;
        if (chosen.TryGetProperty("digest", out var digestEl) && digestEl.ValueKind == JsonValueKind.String)
        {
            var digest = digestEl.GetString();
            if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha = digest["sha256:".Length..].ToLowerInvariant();
            }
        }

        DateTimeOffset? published = null;
        if (root.TryGetProperty("published_at", out var pubEl)
            && pubEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(pubEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            published = parsed;
        }

        return new UpdateInfo
        {
            Version = version,
            TagName = tag ?? ("v" + version),
            ReleaseName = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty,
            ReleaseNotes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty,
            PublishedAt = published,
            DownloadUrl = url,
            Size = size,
            Sha256 = sha,
            MetadataSource = source,
        };
    }

    private static IEnumerable<(string Source, string Prefix)> OrderApiPrefixes(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)
            && !preferred.Equals(SourceAuto, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in ApiPrefixes.Where(p => p.Source.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
            {
                yield return item;
            }
        }

        foreach (var item in ApiPrefixes)
        {
            if (!string.IsNullOrWhiteSpace(preferred)
                && item.Source.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return item;
        }
    }

    private static IEnumerable<Mirror> OrderMirrors(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)
            && !preferred.Equals(SourceAuto, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var mirror in Mirrors.Where(m => m.Key.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
            {
                yield return mirror;
            }
        }

        foreach (var mirror in Mirrors)
        {
            if (!string.IsNullOrWhiteSpace(preferred)
                && mirror.Key.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return mirror;
        }
    }

    private static string Describe(string source) =>
        Mirrors.FirstOrDefault(m => m.Key.Equals(source, StringComparison.OrdinalIgnoreCase))?.Display ?? source;

    private static string Shorten(string text, int max = 120)
    {
        var single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就算了，下次下载前会再清一遍
        }
    }

    /// <summary>把 Release 说明里的 markdown 记号去掉，给纯文本控件用。</summary>
    public static string PlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var output = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            line = line.TrimStart();
            while (line.StartsWith('#'))
            {
                line = line[1..];
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                line = "· " + line[2..];
            }

            line = line.Replace("**", string.Empty).Replace("`", string.Empty);

            // 去掉 [文字](链接) 只留文字
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");

            output.Add(line.Trim());
        }

        var text = string.Join("\n", output).Trim();
        return text.Length <= 1500 ? text : text[..1500] + "…";
    }

    private sealed record Mirror(string Key, string Display, string Prefix);
}
