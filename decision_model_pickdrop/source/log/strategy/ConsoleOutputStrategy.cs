using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LogLib.log.model;
using LogLib.log.strategy;

namespace LogLib.log.strategy;

/// <summary>
/// 로그 엔트리를 콘솔(Standard Output / Error)에 출력하는 출력 전략입니다.
/// </summary>
/// <remarks>
/// 로그 레벨에 따라 출력 대상과 색상이 달라지며,
/// 출력이 리디렉션되지 않은 경우에만 콘솔 색상을 사용합니다.
/// 스레드 안전성을 보장하며 <see cref="IDisposable"/>을 구현합니다.
/// </remarks>
public sealed class ConsoleOutputStrategy : ILogOutputStrategy, IDisposable
{
    /// <summary>
    /// UTC 타임스탬프 출력에 사용되는 날짜/시간 형식입니다.
    /// </summary>
    // private const string TimestampFormatUtc = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string TimestampFormatUtc = "yy-MM-dd HH:mm:ss.fff";

    /// <summary>
    /// 동시 접근을 제어하기 위한 동기화 객체입니다.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// 인스턴스가 해제되었는지 여부를 나타냅니다.
    /// </summary>
    private bool _disposed = false;

    /// <summary>
    /// <see cref="ConsoleOutputStrategy"/> 클래스의 새 인스턴스를 초기화합니다.
    /// </summary>
    public ConsoleOutputStrategy()
    {
    }

    /// <summary>
    /// 단일 로그 엔트리를 콘솔에 출력합니다.
    /// </summary>
    /// <param name="entry">출력할 로그 엔트리</param>
    /// <returns>출력이 처리되었으면 <c>true</c>, 그렇지 않으면 <c>false</c></returns>
    public bool Write(LogEntry entry)
    {
        return WriteBatch(new[] { entry });
    }

    /// <summary>
    /// 여러 로그 엔트리를 배치로 콘솔에 출력합니다.
    /// </summary>
    /// <param name="entries">출력할 로그 엔트리 목록</param>
    /// <returns>출력이 처리되었으면 <c>true</c>, 그렇지 않으면 <c>false</c></returns>
    public bool WriteBatch(IReadOnlyList<LogEntry> entries)
    {
        if (entries.Count == 0)
            return false;

        lock (_lock)
        {
            if (_disposed)
                return true;

            foreach (var entry in entries)
            {
                var isErr = entry.Level == LogLevel.Warn || entry.Level == LogLevel.Error;
                var writer = isErr ? System.Console.Error : System.Console.Out;
                var line = FormatLine(entry);

                if (!System.Console.IsOutputRedirected)
                {
                    WriteWithColor(entry.Level, writer, line);
                    continue;
                }

                writer.WriteLine(line);
                writer.Flush();
            }
        }

        return true;
    }

    /// <summary>
    /// 리소스를 해제하고 이후 출력이 수행되지 않도록 합니다.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
    }

    /// <summary>
    /// 로그 엔트리를 단일 문자열 라인으로 포맷합니다.
    /// </summary>
    /// <param name="entry">포맷할 로그 엔트리</param>
    /// <returns>포맷된 로그 문자열</returns>
    private static string FormatLine(LogEntry entry)
    {
        var sb = new StringBuilder(256);

        // Timestamp string + long 병렬 표기 (항상)
        sb.Append(FormatTimestampUtc(entry.TimeStamp));
        // sb.Append(" (");
        // sb.Append(entry.TimeStamp);
        // sb.Append(")");

        // Level string 출력 (항상)
        sb.Append(" [");
        sb.Append(entry.Level.ToString());
        sb.Append("] ");

        // Message (항상)
        sb.Append(entry.Message);

        // Meta (있으면 항상)
        if (entry.Meta is { Count: > 0 })
        {
            sb.Append(" | ");
            AppendMetaSorted(sb, entry.Meta);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Unix 시간(밀리초)을 UTC 기준 문자열로 변환합니다.
    /// </summary>
    /// <param name="unixTimeMs">Unix Epoch 기준 밀리초 단위 시간</param>
    /// <returns>UTC 기준 포맷된 타임스탬프 문자열</returns>
    private static string FormatTimestampUtc(long unixTimeMs)
    {
        // 계약: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 기반
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(unixTimeMs);
        return dto.UtcDateTime.ToString(TimestampFormatUtc);
    }

    /// <summary>
    /// 메타데이터를 키 기준으로 정렬하여 문자열에 추가합니다.
    /// </summary>
    /// <param name="sb">출력에 사용할 <see cref="StringBuilder"/></param>
    /// <param name="meta">메타데이터 컬렉션</param>
    private static void AppendMetaSorted(StringBuilder sb, IReadOnlyDictionary<string, string> meta)
    {
        // 재현성 위해 key 정렬 고정
        foreach (var kv in meta.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(kv.Value);
            sb.Append(", ");
        }

        // trailing ", " 제거
        if (sb.Length >= 2)
            sb.Length -= 2;
    }

    /// <summary>
    /// 로그 레벨에 따라 콘솔 색상을 적용하여 로그를 출력합니다.
    /// </summary>
    /// <param name="level">로그 레벨</param>
    /// <param name="writer">출력에 사용할 <see cref="TextWriter"/></param>
    /// <param name="line">출력할 로그 문자열</param>
    private static void WriteWithColor(LogLevel level, TextWriter writer, string line)
    {
        var original = System.Console.ForegroundColor;
        try
        {
            System.Console.ForegroundColor = level switch
            {
                LogLevel.Debug => ConsoleColor.Gray,
                LogLevel.Info => ConsoleColor.Cyan,
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _ => original
            };

            writer.WriteLine(line);
            writer.Flush();
        }
        finally
        {
            System.Console.ForegroundColor = original;
        }
    }
}