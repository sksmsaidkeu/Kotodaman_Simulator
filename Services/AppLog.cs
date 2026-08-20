using System.IO;
using System.Text;

namespace KotodamanWordFinder.Services;

public static class AppLog
{
    private static readonly object SyncRoot = new();
    private const int MaximumLogFiles = 14;

    public static string CurrentLogPath => Path.Combine(
        AppPaths.LogDirectory,
        $"KotodamanWordFinder_{DateTime.Now:yyyyMMdd}.log");

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            PruneOldLogs();
            Info($"{AppPaths.DisplayName} v{AppPaths.AppVersion} 시작");
        }
        catch
        {
            // 로깅 실패가 앱 실행을 막지 않습니다.
        }
    }

    public static void Info(string message)
        => Write("INFO", message, null);

    public static void Warning(string message, Exception? exception = null)
        => Write("WARN", message, exception);

    public static void Error(string message, Exception exception)
        => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                var builder = new StringBuilder();
                builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
                builder.Append(" [");
                builder.Append(level);
                builder.Append("] ");
                builder.AppendLine(message);

                if (exception is not null)
                {
                    builder.AppendLine(exception.ToString());
                }

                File.AppendAllText(CurrentLogPath, builder.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // 로깅 실패는 원래 동작에 영향을 주지 않습니다.
        }
    }

    private static void PruneOldLogs()
    {
        string[] logs = Directory
            .EnumerateFiles(AppPaths.LogDirectory, "*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path =>
            {
                try
                {
                    return File.GetLastWriteTimeUtc(path);
                }
                catch
                {
                    return DateTime.MinValue;
                }
            })
            .ToArray();

        foreach (string path in logs.Skip(MaximumLogFiles))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // 오래된 로그 정리 실패는 무시합니다.
            }
        }
    }
}
