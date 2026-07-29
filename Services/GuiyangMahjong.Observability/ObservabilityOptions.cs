using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Observability;

/// <summary>
/// 所有 .NET 服务共用的 OpenTelemetry 配置；生产环境通过环境变量覆盖，不在仓库保存凭据。
/// </summary>
public sealed class MahjongObservabilityOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Observability";

    /// <summary>是否把日志、指标和追踪发送到 OTLP Collector；结构化控制台日志始终启用。</summary>
    public bool Enabled { get; init; }

    /// <summary>OTLP gRPC 入口，必须是 Collector 内网地址，不得包含查询凭据。</summary>
    [Required, Url]
    public string OtlpEndpoint { get; init; } = "http://127.0.0.1:4317";

    /// <summary>父级采样优先的根跨度采样率，有效范围 0～1；管理命令仍保留业务 TraceId。</summary>
    [Range(0, 1)]
    public double TraceSampleRatio { get; init; } = 0.1;

    /// <summary>是否导出 .NET Runtime 和进程级 USE 指标。</summary>
    public bool RuntimeMetricsEnabled { get; init; } = true;
}

/// <summary>
/// 结构化 JSON 控制台格式器配置，固定服务名和环境名，防止调用方遗漏基础字段。
/// </summary>
public sealed class MahjongJsonConsoleFormatterOptions
    : Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions
{
    /// <summary>低基数服务标识。</summary>
    public string ServiceName { get; set; } = "unknown";

    /// <summary>部署环境标识，例如 Development、Staging 或 Production。</summary>
    public string EnvironmentName { get; set; } = "unknown";
}
