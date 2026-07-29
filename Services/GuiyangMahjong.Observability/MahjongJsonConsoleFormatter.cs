using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Observability;

/// <summary>
/// 输出稳定单行 JSON 日志契约；基础字段始终存在，业务属性经过拒绝清单和值级脱敏。
/// </summary>
public sealed class MahjongJsonConsoleFormatter(
    IOptionsMonitor<MahjongJsonConsoleFormatterOptions> options)
    : ConsoleFormatter(FormatterName)
{
    /// <summary>注册到 ConsoleLoggerOptions 的格式器名称。</summary>
    public const string FormatterName = "mahjong-json";

    /// <summary>
    /// 将日志条目写为一行 UTF-8 JSON；异常只记录类型，不输出可能含凭据或内部地址的原始正文。
    /// </summary>
    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var current = options.CurrentValue;
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (scopeProvider is not null)
        {
            scopeProvider.ForEachScope((scope, target) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var value in SensitiveDataSanitizer.SanitizeProperties(values))
                        target[value.Key] = value.Value;
                }
            }, properties);
        }
        if (logEntry.State is IEnumerable<KeyValuePair<string, object?>> state)
        {
            foreach (var value in SensitiveDataSanitizer.SanitizeProperties(state))
                properties[value.Key] = value.Value;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("Timestamp", DateTimeOffset.UtcNow);
            writer.WriteString("Level", logEntry.LogLevel.ToString());
            writer.WriteString("Service", current.ServiceName);
            writer.WriteString("Environment", current.EnvironmentName);
            WriteNullableString(writer, "TraceId", GetString(
                properties, "TraceId")
                ?? Activity.Current?.TraceId.ToString());
            WriteNullableString(writer, "RoomId", GetString(properties, "RoomId"));
            WriteNullableString(writer, "PlayerId", GetString(properties, "PlayerId"));
            WriteNullableString(writer, "MatchId", GetString(properties, "MatchId"));
            WriteNullableString(
                writer,
                "ServerInstanceId",
                GetString(properties, "ServerInstanceId"));
            WriteNullableString(writer, "EventId", GetString(properties, "EventId"));
            writer.WriteString("Category", logEntry.Category);
            writer.WriteString(
                "Message",
                SensitiveDataSanitizer.SanitizeValue(
                    logEntry.Formatter(logEntry.State, null))?.ToString());
            if (logEntry.Exception is not null)
            {
                writer.WriteString(
                    "ExceptionType",
                    logEntry.Exception.GetType().FullName);
                writer.WriteString(
                    "ExceptionSummary",
                    "处理请求时发生异常，原始异常正文已按安全策略省略。");
            }
            writer.WritePropertyName("Properties");
            JsonSerializer.Serialize(writer, properties);
            writer.WriteEndObject();
        }
        textWriter.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string? GetString(
        IReadOnlyDictionary<string, object?> values,
        string key) =>
        values.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            writer.WriteNull(propertyName);
        else
            writer.WriteString(propertyName, value);
    }
}
