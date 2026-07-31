using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GuiyangMahjong.Observability;

/// <summary>
/// Web 服务统一可观测性入口，集中配置结构化日志、OTLP 追踪、指标和请求上下文。
/// </summary>
public static class MahjongObservabilityExtensions
{
    /// <summary>
    /// 注册结构化日志和 OpenTelemetry。每个进程只能调用一次，serviceName 必须是稳定低基数值。
    /// </summary>
    public static WebApplicationBuilder AddMahjongObservability(
        this WebApplicationBuilder builder,
        string serviceName)
    {
        var settings = builder.Configuration
            .GetSection(MahjongObservabilityOptions.SectionName)
            .Get<MahjongObservabilityOptions>()
            ?? new MahjongObservabilityOptions();
        builder.Services
            .AddOptions<MahjongObservabilityOptions>()
            .Bind(builder.Configuration.GetSection(
                MahjongObservabilityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.Configure<MahjongJsonConsoleFormatterOptions>(options =>
        {
            options.ServiceName = serviceName;
            options.EnvironmentName = builder.Environment.EnvironmentName;
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
            options.FormatterName = MahjongJsonConsoleFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<
            MahjongJsonConsoleFormatter,
            MahjongJsonConsoleFormatterOptions>();

        if (!settings.Enabled) return builder;

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName,
                serviceVersion: typeof(MahjongObservabilityExtensions)
                    .Assembly.GetName().Version?.ToString())
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment.name"] =
                    builder.Environment.EnvironmentName
            });
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.ParseStateValues = true;
            logging.SetResourceBuilder(resource);
            logging.AddOtlpExporter(exporter =>
                ConfigureExporter(exporter, settings));
        });
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder
                .AddService(serviceName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] =
                        builder.Environment.EnvironmentName
                }))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(
                    new TraceIdRatioBasedSampler(
                        Math.Clamp(settings.TraceSampleRatio, 0, 1))))
                .AddSource(MahjongTelemetry.ActivitySourceName)
                // 消息发布与消费使用独立 Source，确保 HTTP → Outbox → JetStream → Inbox 链路可关联。
                .AddSource("GuiyangMahjong.Messaging")
                .AddSource("GuiyangMahjong.Workers")
                .AddAspNetCoreInstrumentation(instrumentation =>
                {
                    instrumentation.Filter = context =>
                        !context.Request.Path.StartsWithSegments(
                            "/health",
                            StringComparison.Ordinal)
                        && !context.Request.Path.StartsWithSegments(
                            "/metrics",
                            StringComparison.Ordinal);
                })
                .AddHttpClientInstrumentation()
                .AddProcessor(new SensitiveActivityProcessor())
                .AddOtlpExporter(exporter =>
                    ConfigureExporter(exporter, settings)))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(MahjongTelemetry.MeterName)
                    .AddMeter("GuiyangMahjong.Admin.Monitoring")
                    .AddMeter("GuiyangMahjong.Messaging")
                     .AddMeter("GuiyangMahjong.Workers")
                     .AddMeter("GuiyangMahjong.Configuration")
                     .AddMeter("GuiyangMahjong.Allocator.Configuration")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
                if (settings.RuntimeMetricsEnabled)
                    metrics.AddRuntimeInstrumentation();
                metrics.AddOtlpExporter((exporter, reader) =>
                {
                    ConfigureExporter(exporter, settings);
                    // 房间运维需要秒级趋势；10 秒周期兼顾实时性和 Collector 压力。
                    reader.PeriodicExportingMetricReaderOptions =
                        new PeriodicExportingMetricReaderOptions
                        {
                            ExportIntervalMilliseconds = 5_000,
                            ExportTimeoutMilliseconds = 5_000
                        };
                });
            });
        _ = telemetry;
        return builder;
    }

    /// <summary>
    /// 在认证/业务中间件之前建立统一日志作用域和业务 TraceId，并记录低基数 RED 指标。
    /// </summary>
    public static IApplicationBuilder UseMahjongObservability(
        this IApplicationBuilder app,
        string serviceName,
        string environmentName) =>
        app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            var traceId = NormalizeTraceId(
                context.Request.Headers["X-Trace-Id"].ToString(),
                Activity.Current?.TraceId.ToString()
                    ?? context.TraceIdentifier);
            context.Response.Headers["X-Trace-Id"] = traceId;
            var scope = new Dictionary<string, object?>
            {
                ["Service"] = serviceName,
                ["Environment"] = environmentName,
                ["TraceId"] = traceId,
                ["RoomId"] = RouteValue(context, "roomId"),
                ["PlayerId"] = RouteValue(context, "playerId"),
                ["MatchId"] = RouteValue(context, "matchId"),
                ["ServerInstanceId"] = RouteValue(context, "serverInstanceId"),
                ["EventId"] = RouteValue(context, "eventId")
            };
            Activity.Current?.SetTag("mahjong.business_trace_id", traceId);
            Activity.Current?.AddBaggage("mahjong.business_trace_id", traceId);
            foreach (var item in scope.Where(item => item.Value is not null))
            {
                if (item.Key is not ("Service" or "Environment" or "TraceId"))
                    Activity.Current?.SetTag($"mahjong.{item.Key}", item.Value);
            }
            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("GuiyangMahjong.HttpRequest");
            using (logger.BeginScope(scope))
            {
                try
                {
                    await next(context);
                }
                finally
                {
                    var route = (context.GetEndpoint() as RouteEndpoint)
                        ?.RoutePattern.RawText
                        ?? "unmatched";
                    var elapsed =
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    MahjongTelemetry.RecordHttpRequest(
                        serviceName,
                        context.Request.Method,
                        route,
                        context.Response.StatusCode,
                        elapsed);
                    logger.LogInformation(
                        "HTTP 请求完成 Method={RequestMethod} Route={RequestRoute} StatusCode={StatusCode} DurationMilliseconds={DurationMilliseconds}",
                        context.Request.Method,
                        route,
                        context.Response.StatusCode,
                        elapsed);
                }
            }
        });

    private static string? RouteValue(HttpContext context, string key)
    {
        foreach (var value in context.Request.RouteValues)
        {
            if (value.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return value.Value?.ToString();
        }
        return null;
    }

    private static string NormalizeTraceId(string supplied, string fallback)
    {
        var candidate = supplied.Trim();
        return candidate.Length is >= 8 and <= 64
            && candidate.All(character =>
                char.IsLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')
            ? candidate
            : fallback;
    }

    private static void ConfigureExporter(
        OtlpExporterOptions exporter,
        MahjongObservabilityOptions settings)
    {
        exporter.Endpoint = new Uri(settings.OtlpEndpoint);
        exporter.Protocol = OtlpExportProtocol.Grpc;
    }
}

/// <summary>
/// 最后一道跨度属性过滤器，移除潜在凭据、聊天正文、支付字段和未脱敏 IP。
/// </summary>
public sealed class SensitiveActivityProcessor
    : OpenTelemetry.BaseProcessor<Activity>
{
    /// <summary>导出前删除禁止属性；业务标识保留在跨度中但不得进入指标标签。</summary>
    public override void OnEnd(Activity data)
    {
        foreach (var tag in data.TagObjects.ToArray())
        {
            if (SensitiveDataSanitizer.IsForbiddenKey(tag.Key))
                data.SetTag(tag.Key, null);
            else
                data.SetTag(
                    tag.Key,
                    SensitiveDataSanitizer.SanitizeValue(tag.Value));
        }
    }
}
