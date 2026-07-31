using System.Diagnostics;
using System.Diagnostics.Metrics;
using GuiyangMahjong.Contracts.Events;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;

namespace GuiyangMahjong.BuildingBlocks.Messaging;

/// <summary>
/// 使用 JetStream 发布确认实现的生产事件发布器。
/// 每次发布使用 event_id 作为 Nats-Msg-Id，解决“服务端已保存但 Outbox 标记失败”后的重复发布；
/// 最终业务幂等仍由消费端 Inbox 保证，不能只依赖 JetStream 去重窗口。
/// </summary>
public sealed class NatsJetStreamEventPublisher : IEventPublisher, IAsyncDisposable
{
    private static readonly ActivitySource ActivitySource =
        new("GuiyangMahjong.Messaging", "1.0.0");
    private static readonly Meter Meter =
        new("GuiyangMahjong.Messaging", "1.0.0");
    private static readonly Counter<long> Published = Meter.CreateCounter<long>(
        "mahjong_messaging_published_total",
        description: "JetStream 已确认保存的事件数量。");
    private static readonly Counter<long> PublishFailures = Meter.CreateCounter<long>(
        "mahjong_messaging_publish_failures_total",
        description: "JetStream 发布未获得确认的事件数量。");

    private readonly NatsClient client;
    private readonly INatsJSContext jetStream;

    /// <summary>
    /// 创建独占 NATS 连接。URL 只能来自受控配置；凭据应通过 NATS 标准凭据文件或环境注入，
    /// 不得写入日志。调用方负责在应用停止时异步释放连接。
    /// </summary>
    public NatsJetStreamEventPublisher(
        string url,
        string clientName,
        string? username = null,
        string? password = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)
            || string.IsNullOrWhiteSpace(clientName))
        {
            throw new ArgumentException("NATS 发布器配置无效。");
        }
        client = new NatsClient(new NatsOpts
        {
            Url = url,
            Name = clientName,
            // 凭据与 URL 分开注入，避免异常或连接诊断意外打印包含密码的 URI。
            AuthOpts = string.IsNullOrWhiteSpace(username)
                ? NatsAuthOpts.Default
                : new NatsAuthOpts
                {
                    Username = username,
                    Password = password
                }
        });
        jetStream = client.CreateJetStreamContext();
    }

    /// <inheritdoc/>
    public async Task PublishAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var subject = PlatformEventSubjects.Resolve(
            envelope.EventType,
            envelope.SchemaVersion);
        using var activity = ActivitySource.StartActivity(
            "jetstream publish",
            ActivityKind.Producer);
        activity?.SetTag("messaging.system", "nats");
        activity?.SetTag("messaging.destination.name", subject);
        activity?.SetTag("messaging.message.id", envelope.EventId.Value);
        activity?.SetTag("mahjong.correlation_id", envelope.CorrelationId.Value);

        var headers = new NatsHeaders
        {
            { "X-Correlation-Id", envelope.CorrelationId.Value },
            { "X-Event-Id", envelope.EventId.Value }
        };
        if (Activity.Current?.Id is { } traceParent)
        {
            headers.Add("traceparent", traceParent);
        }

        try
        {
            var acknowledgement = await jetStream.PublishAsync(
                subject,
                envelope,
                opts: new NatsJSPubOpts { MsgId = envelope.EventId.Value },
                headers: headers,
                cancellationToken: cancellationToken);
            acknowledgement.EnsureSuccess();
            Published.Add(1, new TagList { { "subject", subject } });
        }
        catch (NatsJSDuplicateMessageException)
        {
            // 发布确认已到达但 Outbox 标记失败时会使用同一 event_id 重发；
            // JetStream 的 Duplicate ACK 证明原消息已持久化，因此应视为幂等成功并允许 Outbox 完成。
            Published.Add(1, new TagList
            {
                { "subject", subject },
                { "outcome", "duplicate-confirmed" }
            });
        }
        catch
        {
            PublishFailures.Add(1, new TagList { { "subject", subject } });
            throw;
        }
    }

    /// <summary>关闭连接并等待缓冲操作结束，避免进程终止时遗留未确认发布。</summary>
    public ValueTask DisposeAsync() => client.DisposeAsync();
}
