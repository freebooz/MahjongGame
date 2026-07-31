using System.Diagnostics;
using GuiyangMahjong.GameData.Domain;
using GuiyangMahjong.GameData.Infrastructure;
using GuiyangMahjong.GameData.Options;
using GuiyangMahjong.GameData.Settlement;
using GuiyangMahjong.GameData.GameRecords;
using GuiyangMahjong.GameData.Leaderboards;
using GuiyangMahjong.GameData.Administration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.GameData.Api;

/// <summary>注册内部结算入口、只读战绩/证据/排行榜接口和三类健康探针。</summary>
public static class GameDataEndpoints
{
    /// <summary>映射 GameData API；所有写入口均要求 DS 工作负载凭据和显式幂等键。</summary>
    public static void MapGameDataEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/startup", () => Results.Ok(new { status = "started" }));
        app.MapGet("/health/ready", async (IGameDataStore store, CancellationToken cancellationToken) =>
            await store.CheckHealthAsync(cancellationToken)
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapPost("/internal/settlements/{matchId}", async (
            string matchId,
            HttpContext context,
            FinalResultEnvelope envelope,
            SettlementService settlementService,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(matchId, envelope.MatchId, StringComparison.Ordinal))
                throw GameDataException.Invalid("MATCH_SCOPE_MISMATCH", "路径 MatchId 与结算信封不一致");
            var credential = RequireBearer(context);
            var idempotencyKey = RequireHeader(context, "Idempotency-Key", 8, 160);
            var requestId = GetOrCreateId(context, "X-Request-Id");
            var correlationId = GetOrCreateId(context, "X-Correlation-Id");
            var result = await settlementService.CommitAsync(
                envelope, credential, false, idempotencyKey, Activity.Current?.TraceId.ToString() ?? requestId,
                correlationId, cancellationToken);
            return result.Duplicate ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(new RequestSizeLimitAttribute(256 * 1024));

        app.MapPost("/internal/settlements/{matchId}/recovery", async (
            string matchId,
            HttpContext context,
            FinalResultEnvelope envelope,
            IOptions<GameDataOptions> options,
            SettlementService settlementService,
            CancellationToken cancellationToken) =>
        {
            RequireDedicatedBearer(context, options.Value.AllocatorRecoveryToken, "RECOVERY_FORBIDDEN");
            if (matchId != envelope.MatchId)
                throw GameDataException.Invalid("MATCH_SCOPE_MISMATCH", "路径 MatchId 与结算信封不一致");
            var idempotencyKey = RequireHeader(context, "Idempotency-Key", 8, 160);
            var requestId = GetOrCreateId(context, "X-Request-Id");
            var correlationId = GetOrCreateId(context, "X-Correlation-Id");
            var result = await settlementService.CommitAsync(
                envelope, null, true, idempotencyKey, Activity.Current?.TraceId.ToString() ?? requestId,
                correlationId, cancellationToken);
            return Results.Ok(result);
        }).WithMetadata(new RequestSizeLimitAttribute(256 * 1024));

        app.MapPost("/internal/settlements/{matchId}/shadow-validate", async (
            string matchId,
            HttpContext context,
            FinalResultEnvelope envelope,
            SettlementService settlementService,
            CancellationToken cancellationToken) =>
        {
            if (matchId != envelope.MatchId)
                throw GameDataException.Invalid("MATCH_SCOPE_MISMATCH", "路径 MatchId 与结算信封不一致");
            var result = await settlementService.ValidateOnlyAsync(
                envelope, RequireBearer(context), cancellationToken);
            return Results.Ok(result);
        }).WithMetadata(new RequestSizeLimitAttribute(256 * 1024));

        var monitoring = app.MapGroup("/internal/monitoring");
        monitoring.MapGet("/matches/{matchId}", async (
            string matchId, HttpContext context, IOptions<GameDataOptions> options,
            GameRecordQueries queries, CancellationToken cancellationToken) =>
        {
            RequireMonitoring(context, options.Value.MonitoringToken);
            if (!Guid.TryParse(matchId, out _)) return Results.BadRequest();
            var record = await queries.GetMatchAsync(matchId, cancellationToken);
            return record is null ? Results.NotFound() : Results.Ok(record);
        });
        monitoring.MapGet("/players/{playerId}/records", async (
            string playerId, int? limit, HttpContext context, IOptions<GameDataOptions> options,
            GameRecordQueries queries, CancellationToken cancellationToken) =>
        {
            RequireMonitoring(context, options.Value.MonitoringToken);
            var take = Math.Clamp(limit ?? 50, 1, 200);
            return Results.Ok(await queries.GetPlayerAsync(playerId, take, cancellationToken));
        });
        monitoring.MapGet("/evidence/{evidenceId}", async (
            string evidenceId, HttpContext context, IOptions<GameDataOptions> options,
            ReplayEvidenceQueries queries, CancellationToken cancellationToken) =>
        {
            RequireMonitoring(context, options.Value.MonitoringToken);
            if (!Guid.TryParse(evidenceId, out _)) return Results.BadRequest();
            var record = await queries.GetAsync(evidenceId, cancellationToken);
            return record is null ? Results.NotFound() : Results.Ok(record);
        });
        monitoring.MapGet("/leaderboards/basic", async (
            int? limit, HttpContext context, IOptions<GameDataOptions> options,
            LeaderboardQueries queries, CancellationToken cancellationToken) =>
        {
            RequireMonitoring(context, options.Value.MonitoringToken);
            return Results.Ok(await queries.GetBasicAsync(Math.Clamp(limit ?? 100, 1, 500), cancellationToken));
        });
    }

    private static string RequireBearer(HttpContext context)
    {
        const string prefix = "Bearer ";
        var value = context.Request.Headers.Authorization.ToString();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || value[prefix.Length..].Trim().Length < 32)
            throw GameDataException.Unauthorized("WORKLOAD_IDENTITY_REQUIRED", "缺少 Dedicated Server 工作负载身份");
        return value[prefix.Length..].Trim();
    }

    private static void RequireMonitoring(HttpContext context, string expected)
    {
        RequireDedicatedBearer(context, expected, "MONITORING_FORBIDDEN");
    }

    private static void RequireDedicatedBearer(HttpContext context, string expected, string errorCode)
    {
        var supplied = RequireBearer(context);
        var left = System.Text.Encoding.UTF8.GetBytes(supplied);
        var right = System.Text.Encoding.UTF8.GetBytes(expected);
        var valid = left.Length == right.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(left);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(right);
        if (!valid) throw GameDataException.Unauthorized(errorCode, "用途隔离服务凭据无效");
    }

    private static string RequireHeader(HttpContext context, string name, int minimum, int maximum)
    {
        var value = context.Request.Headers[name].ToString().Trim();
        if (value.Length < minimum || value.Length > maximum)
            throw GameDataException.Invalid("REQUIRED_HEADER_INVALID", $"{name} 无效");
        return value;
    }

    private static string GetOrCreateId(HttpContext context, string name)
    {
        var value = context.Request.Headers[name].ToString().Trim();
        if (value.Length is < 8 or > 128) value = Guid.NewGuid().ToString("N");
        context.Response.Headers[name] = value;
        return value;
    }

    /// <summary>纵深拒绝凭据、直接身份信息和支付字段，防止专用内网令牌被误用为任意JSON写入口。</summary>
    private static bool ContainsForbiddenReplayData(System.Text.Json.JsonElement element)
    {
        string[] forbidden =
        [
            "authorization", "password", "passwd", "token", "accesstoken", "refreshtoken",
            "cookie", "secret", "privatekey", "fullip", "phone", "mobile", "name", "idcard",
            "bankcard", "cardnumber", "cvv", "email", "address"
        ];
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = property.Name.Replace("_", string.Empty, StringComparison.Ordinal);
                if (forbidden.Contains(normalized, StringComparer.OrdinalIgnoreCase)
                    || ContainsForbiddenReplayData(property.Value)) return true;
            }
        }
        return element.ValueKind == System.Text.Json.JsonValueKind.Array
            && element.EnumerateArray().Any(ContainsForbiddenReplayData);
    }
}
