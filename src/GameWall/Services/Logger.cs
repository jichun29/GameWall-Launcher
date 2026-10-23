using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GameWall.Services;

/// <summary>一条日志。界面上「运行日志」窗口直接绑定它。</summary>
public sealed class LogEntry
{
    public DateTime Time { get; init; }

    public string Level { get; init; } = "INFO";

    public string Message { get; init; } = string.Empty;

    /// <summary>异常的完整堆栈（没有就是空串）。</summary>
    public string Detail { get; init; } = string.Empty;

    public bool IsError => Level == "ERROR";

    public bool IsWarn => Level == "WARN";

    /// <summary>界面上显示的时间，精确到秒就够了。</summary>
    public string TimeText => Time.ToString("HH:mm:ss");

    public string FullText => Detail.Length == 0
        ? $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Message}"
        : $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Message}{Environment.NewLine}{Detail}";

    public string LevelText => Level switch
    {
        "ERROR" => "错误",
        "WARN" => "警告",
        _ => "信息",
    };
}

/// <summary>
/// 极简本地日志：写 %AppData%\GameWall\gamewall.log，同时留一份最近的内存副本。
///
/// 内存副本是给「设置 → 运行日志」那个窗口用的 —— 玩家遇到问题不用满硬盘找文件，
/// 直接打开窗口就能看到刚才扫描/启动/更新发生了什么，一键复制或导出成文档发给开发者。
/// 上限 512KB 落盘、内存里保留最近 2000 条，超了就丢最旧的。
/// </summary>
public static class Logger
{
    private const long MaxBytes = 512 * 1024;
    private const int MaxMemoryEntries = 2000;

    private static readonly object Gate = new();
    private static readonly List<LogEntry> Buffer = new(MaxMemoryEntries);

    /// <summary>有新日志时触发。注意可能在后台线程上调用，订阅方自己切回 UI 线程。</summary>
    public static event Action<LogEntry>? Written;

    /// <summary>内存里保留的日志快照（按时间正序）。</summary>
    public static IReadOnlyList<LogEntry> Recent
    {
        get
        {
            lock (Gate)
            {
                return Buffer.ToArray();
            }
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>清空界面上的记录（磁盘上的日志文件不动，方便事后回头查）。</summary>
    public static void ClearMemory()
    {
        lock (Gate)
        {
            Buffer.Clear();
        }
    }

    /// <summary>把内存里的日志拼成可以直接粘给别人的纯文本。</summary>
    public static string ExportText(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.AppendLine(entry.FullText);
        }

        return sb.ToString();
    }

    private static void Write(string level, string message, Exception? ex)
    {
        var entry = new LogEntry
        {
            Time = DateTime.Now,
            Level = level,
            Message = message ?? string.Empty,
            Detail = ex?.ToString() ?? string.Empty,
        };

        lock (Gate)
        {
            Buffer.Add(entry);
            if (Buffer.Count > MaxMemoryEntries)
            {
                Buffer.RemoveRange(0, Buffer.Count - MaxMemoryEntries);
            }
        }

        try
        {
            lock (Gate)
            {
                AppPaths.EnsureCreated();
                var path = AppPaths.LogFile;

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }

                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                  .Append(" [").Append(level).Append("] ")
                  .Append(message);

                if (ex is not null)
                {
                    sb.AppendLine().Append(ex);
                }

                File.AppendAllText(path, sb.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 日志本身不能抛异常影响主流程
        }

        try
        {
            Written?.Invoke(entry);
        }
        catch
        {
            // 订阅方炸了也不能影响主流程
        }
    }
}
