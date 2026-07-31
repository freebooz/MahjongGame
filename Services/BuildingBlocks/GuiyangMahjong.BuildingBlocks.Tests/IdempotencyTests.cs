using System.Text;
using GuiyangMahjong.BuildingBlocks.Idempotency;
using GuiyangMahjong.Contracts.Common;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GuiyangMahjong.BuildingBlocks.Tests;

/// <summary>验证 API 幂等键、指纹、重复请求、参数冲突和过期清理。</summary>
public sealed class IdempotencyTests
{
    /// <summary>HTTP 适配器只接受单个合法 Idempotency-Key Header。</summary>
    [Fact]
    public void HeaderReader_RejectsMissingMultipleAndMalformedKeys()
    {
        var context = new DefaultHttpContext();
        Assert.False(IdempotencyHeaderReader.TryRead(
            context.Request,
            out _));

        context.Request.Headers[
            IdempotencyHeaderReader.HeaderName] =
            new Microsoft.Extensions.Primitives.StringValues(
                [
                    "valid-key-contract-001",
                    "second-key-contract-001"
                ]);
        Assert.False(IdempotencyHeaderReader.TryRead(
            context.Request,
            out _));

        context.Request.Headers[
            IdempotencyHeaderReader.HeaderName] =
            "valid-key-contract-001";
        Assert.True(IdempotencyHeaderReader.TryRead(
            context.Request,
            out var key));
        Assert.Equal(
            "valid-key-contract-001",
            key.Value);
    }

    /// <summary>相同 Key 和相同请求只能执行一次，并重放首次完成响应。</summary>
    [Fact]
    public async Task SameKeyAndFingerprint_ReplaysFirstResponse()
    {
        var store = new InMemoryIdempotencyStore();
        var fingerprint = CreateFingerprint("""{"room":"A"}""");
        var key = IdempotencyKey.Parse("create-room-contract-001");
        var now = DateTimeOffset.UtcNow;

        var first = await store.TryBeginAsync(
            "room.create",
            key,
            fingerprint,
            now,
            now.AddHours(1),
            CancellationToken.None);
        var concurrent = await store.TryBeginAsync(
            "room.create",
            key,
            fingerprint,
            now,
            now.AddHours(1),
            CancellationToken.None);
        var response = new IdempotentResponse(
            201,
            "application/json",
            Encoding.UTF8.GetBytes("""{"roomId":"R1"}"""));
        await store.CompleteAsync(
            "room.create",
            key,
            fingerprint,
            response,
            CancellationToken.None);
        var replay = await store.TryBeginAsync(
            "room.create",
            key,
            fingerprint,
            now.AddSeconds(1),
            now.AddHours(1),
            CancellationToken.None);

        Assert.Equal(IdempotencyDecision.Acquired, first.Decision);
        Assert.Equal(IdempotencyDecision.InProgress, concurrent.Decision);
        Assert.Equal(IdempotencyDecision.Replay, replay.Decision);
        Assert.Equal(response, replay.Response);
    }

    /// <summary>同一 Key 绑定不同请求指纹时必须返回冲突，不能重放或覆盖旧响应。</summary>
    [Fact]
    public async Task SameKeyWithDifferentFingerprint_IsConflict()
    {
        var store = new InMemoryIdempotencyStore();
        var key = IdempotencyKey.Parse("create-room-contract-002");
        var now = DateTimeOffset.UtcNow;
        await store.TryBeginAsync(
            "room.create",
            key,
            CreateFingerprint("""{"room":"A"}"""),
            now,
            now.AddHours(1),
            CancellationToken.None);

        var result = await store.TryBeginAsync(
            "room.create",
            key,
            CreateFingerprint("""{"room":"B"}"""),
            now,
            now.AddHours(1),
            CancellationToken.None);

        Assert.Equal(IdempotencyDecision.Conflict, result.Decision);
        Assert.Null(result.Response);
    }

    /// <summary>过期记录清理后相同 Key 可以建立新的处理周期。</summary>
    [Fact]
    public async Task ExpiredRecord_CanBeCleanedAndReacquired()
    {
        var store = new InMemoryIdempotencyStore();
        var key = IdempotencyKey.Parse("create-room-contract-003");
        var now = DateTimeOffset.UtcNow;
        var fingerprint = CreateFingerprint("{}");
        await store.TryBeginAsync(
            "room.create",
            key,
            fingerprint,
            now,
            now.AddSeconds(1),
            CancellationToken.None);

        var removed = await store.DeleteExpiredAsync(
            now.AddSeconds(2),
            10,
            CancellationToken.None);
        var result = await store.TryBeginAsync(
            "room.create",
            key,
            fingerprint,
            now.AddSeconds(2),
            now.AddHours(1),
            CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Equal(IdempotencyDecision.Acquired, result.Decision);
    }

    private static string CreateFingerprint(string body) =>
        new Sha256RequestFingerprint().Compute(
            "POST",
            "/api/v1/rooms",
            Encoding.UTF8.GetBytes(body));
}
