using System.Diagnostics;
using System.Text.Json;
using GuiyangMahjong.BuildingBlocks.Observability;
using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;
using GuiyangMahjong.Contracts.Grpc;
using Xunit;

namespace GuiyangMahjong.BuildingBlocks.Tests;

/// <summary>验证强类型 ID、事件信封、gRPC 源契约和调用上下文的稳定协议行为。</summary>
public sealed class StrongIdAndContractTests
{
    /// <summary>强类型 ID 在 JSON 边界写入协议原值，往返后保持值对象相等。</summary>
    [Fact]
    public void StrongIds_JsonRoundTripPreservesTypeAndValue()
    {
        var expected = new ContractSample(
            PlayerId.Parse("player-contract-001"),
            RoomId.Parse("room-contract-001"),
            RoomEpoch.Parse(7),
            RuleSetVersion.Parse("1.2.0"));

        var jsonOptions =
            new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(expected, jsonOptions);
        var actual = JsonSerializer.Deserialize<ContractSample>(
            json,
            jsonOptions);

        Assert.Equal(expected, actual);
        Assert.Contains("\"playerId\":\"player-contract-001\"", json);
        Assert.Contains("\"roomEpoch\":7", json);
    }

    /// <summary>空白、控制字符、低熵幂等键、非法版本和负序号必须在入口被拒绝。</summary>
    [Fact]
    public void StrongIds_InvalidInputsAreRejected()
    {
        Assert.False(PlayerId.TryParse("", out _));
        Assert.False(RoomId.TryParse("room value", out _));
        Assert.False(IdempotencyKey.TryParse("short", out _));
        Assert.False(BuildVersion.TryParse("release/latest", out _));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ActionSequence.Parse(-1));
    }

    /// <summary>普通日志字符串不能自动暴露玩家、会话或幂等键原值。</summary>
    [Fact]
    public void SensitiveStrongIds_ToStringUsesIrreversibleFingerprint()
    {
        const string raw = "player-sensitive-contract-001";
        var playerId = PlayerId.Parse(raw);

        Assert.DoesNotContain(raw, playerId.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("PlayerId(sha256:", playerId.ToString());
    }

    /// <summary>事件信封字段使用 snake_case，载荷类型和 Schema 版本必须匹配。</summary>
    [Fact]
    public void EventEnvelope_IsVersionedAndReadsTypedPayload()
    {
        var now = DateTimeOffset.Parse("2026-07-31T10:00:00Z");
        var payload = new RoomCreated(
            RoomId.Parse("room-event-001"),
            RoomEpoch.Parse(2),
            PlayerId.Parse("player-event-001"),
            RuleSetVersion.Parse("1.0.0"),
            now);
        var envelope = EventEnvelope.Create(
            payload,
            "Room",
            "room-event-001",
            1,
            "LobbyControl",
            "0123456789abcdef0123456789abcdef",
            CorrelationId.Parse("correlation-event-001"),
            now);

        var json = JsonSerializer.Serialize(envelope);

        Assert.Equal(PlatformEventTypes.RoomCreated, envelope.EventType);
        Assert.Equal(1, envelope.SchemaVersion);
        Assert.Contains("\"event_id\":", json);
        Assert.Contains("\"schema_version\":1", json);
        Assert.Equal(payload, envelope.ReadPayload<RoomCreated>());
        Assert.Throws<InvalidDataException>(
            () => envelope.ReadPayload<RoomTerminated>());
    }

    /// <summary>Request、Correlation 和 W3C Trace 必须通过 Header 保持不变。</summary>
    [Fact]
    public void CallContext_PropagatesRequestCorrelationAndTrace()
    {
        using var activity = new Activity("contract-client")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        var context = new CallContextContract(
            "request-contract-001",
            CorrelationId.Parse("correlation-contract-001"),
            activity.TraceId.ToHexString(),
            "Identity",
            BuildVersion.Parse("1.0.0"),
            "1",
            BuildVersion.Parse("2.0.0"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var headers = CallContextPropagation.CreateOutgoingHeaders(context);
        var parsed = CallContextPropagation.ParseIncomingHeaders(headers);

        Assert.Equal(context.RequestId, parsed.RequestId);
        Assert.Equal(context.CorrelationId, parsed.CorrelationId);
        Assert.Equal(activity.TraceId.ToHexString(), parsed.TraceId);
        Assert.Equal(context.CallerService, parsed.CallerService);
    }

    /// <summary>gRPC 项目必须把版本化 proto 复制到输出，并包含全部调用上下文字段。</summary>
    [Fact]
    public void GrpcContract_ContainsVersionedCallContext()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            GrpcContractCatalog.ProtoPath.Replace(
                '/',
                Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"缺少 gRPC proto：{path}");
        var proto = File.ReadAllText(path);
        Assert.Contains(
            $"package {GrpcContractCatalog.Package};",
            proto);
        foreach (var field in new[]
                 {
                     "request_id",
                     "correlation_id",
                     "trace_id",
                     "caller_service",
                     "deadline_unix_ms"
                 })
        {
            Assert.Contains(field, proto);
        }
    }

    /// <summary>测试用复合契约，验证不同底层类型的 JSON 转换器协同工作。</summary>
    private sealed record ContractSample(
        PlayerId PlayerId,
        RoomId RoomId,
        RoomEpoch RoomEpoch,
        RuleSetVersion RuleSetVersion);
}
