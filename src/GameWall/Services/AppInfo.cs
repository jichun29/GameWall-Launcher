using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace GameWall.Services;

/// <summary>
/// 应用自身的元信息：作者、版本、仓库地址。
///
/// 所有「写死」的地方集中在这一个文件里，关于窗口、更新服务、日志抬头、exe 属性
/// 都从这里取值，改作者信息只需要动这里（以及 GameWall.csproj 的 AssemblyMetadata）。
/// </summary>
public static class AppInfo
{
    /// <summary>作者（中文显示名）</summary>
    public const string Author = "季春二九";

    /// <summary>作者在 GitHub 上的账号</summary>
    public const string AuthorHandle = "jichun29";

    /// <summary>作者主页</summary>
    public const string AuthorUrl = "https://github.com/jichun29";

    /// <summary>项目主页</summary>
    public const string HomeUrl = "https://github.com/jichun29/GameWall-Launcher";

    /// <summary>问题反馈</summary>
    public const string IssuesUrl = "https://github.com/jichun29/GameWall-Launcher/issues";

    /// <summary>更新日志</summary>
    public const string ReleasesUrl = "https://github.com/jichun29/GameWall-Launcher/releases";

    /// <summary>仓库 owner（更新检查用）</summary>
    public const string RepoOwner = "jichun29";

    /// <summary>仓库名（更新检查用）</summary>
    public const string RepoName = "GameWall-Launcher";

    /// <summary>版权声明</summary>
    public const string Copyright = "© 2026 季春二九 (jichun29)。基于 MIT 许可证开源。";

    /// <summary>一句话描述</summary>
    public const string Tagline = "轻量 Windows 游戏封面墙";

    /// <summary>产品名（关于窗口标题用）</summary>
    public const string ProductName = "GameWall";

    /// <summary>作者头像位显示的字（取名字里的第一个字）</summary>
    public const string AuthorGlyph = "季";

    /// <summary>许可证摘要</summary>
    public const string LicenseSummary =
        "MIT License。可以自由使用、修改、再分发，保留版权声明即可。"
        + "内置图标与封面占位图均为程序自己绘制，不含任何第三方美术素材。";

    /// <summary>致谢 / 第三方说明</summary>
    public const string CreditLine =
        "游戏封面与图标来自你自己的本机：图标由 Windows Shell 从 exe 提取，"
        + "Steam 竖版封面直接读取 Steam 客户端的本地缓存，全程不联网抓图。";

    /// <summary>平台摘要（关于窗口徽标用）</summary>
    public static string PlatformLabel =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "Windows x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "Windows arm64",
            System.Runtime.InteropServices.Architecture.X86 => "Windows x86",
            var other => "Windows " + other,
        };

    private static string? _version;
    private static string? _buildDate;

    /// <summary>
    /// 当前版本号（如 1.6.0）。
    ///
    /// 取的是程序集 InformationalVersion，因为 .NET 会在后面自动追加
    /// "+&lt;源码版本哈希&gt;"，这里把加号之后的部分切掉，只留干净的语义版本。
    /// </summary>
    public static string Version
    {
        get
        {
            if (_version is not null)
            {
                return _version;
            }

            var asm = Assembly.GetExecutingAssembly();
            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                _version = plus > 0 ? informational[..plus] : informational;
                return _version;
            }

            _version = asm.GetName().Version is { } v
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : "0.0.0";
            return _version;
        }
    }

    /// <summary>显示用版本号，如 <c>v1.6.0</c></summary>
    public static string VersionLabel => "v" + Version;

    /// <summary>编译时间（本地时区），用于关于窗口。</summary>
    public static string BuildDate
    {
        get
        {
            if (_buildDate is not null)
            {
                return _buildDate;
            }

            try
            {
                var exe = Environment.ProcessPath;
                _buildDate = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe)
                    ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd")
                    : DateTime.Now.ToString("yyyy-MM-dd");
            }
            catch
            {
                _buildDate = DateTime.Now.ToString("yyyy-MM-dd");
            }

            return _buildDate;
        }
    }

    /// <summary>
    /// 是否是「单文件发布」的正式版。
    ///
    /// 判断依据：进程所在目录里存在同名 .dll 说明是普通 build/publish 输出目录，
    /// 那种目录下自我更新会覆盖掉一堆依赖文件，因此只提示、不自动替换。
    /// </summary>
    public static bool IsSingleFileBuild
    {
        get
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var dir = Path.GetDirectoryName(exe);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return false;
                }

                var name = Path.GetFileNameWithoutExtension(exe);
                return !File.Exists(Path.Combine(dir, name + ".dll"));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>运行环境摘要（日志 / 关于窗口用）。</summary>
    public static string RuntimeSummary =>
        $"{Environment.OSVersion.VersionString} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}"
        + (IsSingleFileBuild ? " · 单文件" : " · 开发构建");

    /// <summary>把版本字符串解析成可比较的 <see cref="System.Version"/>，解析失败返回 null。</summary>
    public static System.Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Trim();
        if (cleaned.StartsWith('v') || cleaned.StartsWith('V'))
        {
            cleaned = cleaned[1..];
        }

        // 只取 数字与点 的前缀，兼容 "1.6.0-beta" 这类带后缀的 tag
        var chars = cleaned.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray();
        var numeric = new string(chars).TrimEnd('.');

        // 注意：这里必须写全名 System.Version。AppInfo 自己有个 string Version 属性，
        // 直接写 Version.TryParse 会被解析成那个字符串属性。
        return System.Version.TryParse(numeric, out var parsed) ? parsed : null;
    }

    /// <summary>比较两个版本：返回 &gt;0 表示 a 比 b 新。</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var va = ParseVersion(a);
        var vb = ParseVersion(b);

        if (va is null && vb is null)
        {
            return 0;
        }

        if (va is null)
        {
            return -1;
        }

        if (vb is null)
        {
            return 1;
        }

        return va.CompareTo(vb);
    }

    /// <summary>用默认程序打开一个网址（失败写日志，不抛）。</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"打开链接失败：{url}", ex);
        }
    }
}
