using System;
using System.IO;
using System.Text;

namespace GameWall.Services;

/// <summary>
/// 极简本地日志：只在出错时写入 %AppData%\GameWall\gamewall.log，
/// 上限 512KB，超过后自动截断，避免无限增长。
/// </summary>
public static class Logger
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
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
    }
}
