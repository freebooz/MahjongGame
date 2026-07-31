using System.Text.Json;
using System.Text.Json.Serialization;
using GuiyangMahjong.Contracts.Common;

namespace GuiyangMahjong.Contracts.Events;

/// <summary>所有事件载荷必须提供不可变事件名和正整数 Schema 版本。</summary>
public interface IVersionedEventPayload
{
    static abstract string EventType { get; }
    static abstract int SchemaVersion { get; }
}

/// <summary>
/// 跨服务事件的统一信封。
/// Payload 保留原始 JSON，消费者必须先验证 event_type 和 schema_version 再反序列化。
/// </summary>
public sealed record EventEnvelope(
    [property: JsonPropertyName("event_id")] EventId EventId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("aggregate_type")] string AggregateType,
    [property: JsonPropertyName("aggregate_id")] string AggregateId,
    [property: JsonPropertyName("aggregate_version")] long AggregateVersion,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("correlation_id")] CorrelationId CorrelationId,
    [property: JsonPropertyName("causation_id")] EventId? CausationId,
    [property: JsonPropertyName("idempotency_key")] IdempotencyKey? IdempotencyKey,
    [property: JsonPropertyName("payload")] JsonElement Payload)
{
    /// <summary>
    /// 从强类型版本化载荷创建信封；创建时拒绝空聚合和无效版本，避免无效事件进入 Outbox。
    /// </summary>
    public static EventEnvelope Create<TPayload>(
        TPayload payload,
        string aggregateType,
        string aggregateId,
        long aggregateVersion,
        string producer,
        string traceId,
        CorrelationId correlationId,
        DateTimeOffset occurredAt,
        EventId? causationId = null,
        IdempotencyKey? idempotencyKey = null,
        JsonSerializerOptions? serializerOptions = null)
        where TPayload : IVersionedEventPayload
    {
        if (!StrongValueValidation.IsIdentifier(aggregateType)
            || !StrongValueValidation.IsIdentifier(aggregateId)
            || !StrongValueValidation.IsIdentifier(producer)
            || !StrongValueValidation.IsIdentifier(traceId)
            || aggregateVersion < 0
            || TPayload.SchemaVersion <= 0)
        {
            throw new ArgumentException("事件信封字段格式无效。");
        }

        return new EventEnvelope(
            Common.EventId.New(),
            TPayload.EventType,
            TPayload.SchemaVersion,
            aggregateType,
            aggregateId,
            aggregateVersion,
            occurredAt,
            producer,
            traceId,
            correlationId,
            causationId,
            idempotencyKey,
            JsonSerializer.SerializeToElement(
                payload,
                serializerOptions ?? new JsonSerializerOptions(
                    JsonSerializerDefaults.Web)));
    }

    /// <summary>在消费者边界按事件类型和版本反序列化，契约不匹配时失败关闭。</summary>
    public TPayload ReadPayload<TPayload>(
        JsonSerializerOptions? serializerOptions = null)
        where TPayload : IVersionedEventPayload
    {
        if (!string.Equals(
                EventType,
                TPayload.EventType,
                StringComparison.Ordinal)
            || SchemaVersion != TPayload.SchemaVersion)
        {
            throw new InvalidDataException(
                $"事件契约不匹配：{EventType} v{SchemaVersion}。");
        }
        return Payload.Deserialize<TPayload>(
                   serializerOptions
                   ?? new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidDataException("事件 payload 为空。");
    }
}
