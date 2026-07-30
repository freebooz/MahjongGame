using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Services;

namespace GuiyangMahjong.Lobby.Api;

/// <summary>
/// Lobby 路由共享的幂等、安全凭据、房间控制结果和监控游标规则。
/// 公共辅助逻辑不得直接访问存储，以便各路由分区保持可测试的依赖边界。
/// </summary>
public static partial class LobbyEndpoints
{
    /// <summary>读取并校验客户端幂等键；失败时不执行任何有副作用的 Lobby 操作。</summary>
    private static string RequireIdempotencyKey(HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is < 16 or > 128)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.InvalidRequest,
                "Idempotency-Key 长度必须为 16 到 128",
                StatusCodes.Status400BadRequest);
        }
        return key;
    }

    /// <summary>将业务响应转换为可被幂等存储重放的稳定 JSON 响应。</summary>
    private static IdempotentHttpResponse JsonResponse(int statusCode, object body) =>
        new(
            statusCode,
            JsonSerializer.SerializeToElement(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    /// <summary>将房间领域状态投影为 Admin 控制结果，终止态允许幂等重放。</summary>
    private static AdminUpdateRoomControlResult ToRoomControlResult(
        LobbyRoom room,
        string actionType,
        bool alreadyTerminal) =>
        new(
            room.RoomId,
            actionType,
            room.StateSequence,
            room.NewPlayersProhibited,
            room.MaintenanceMode,
            room.MarkedAbnormal,
            room.Lifecycle,
            room.Route?.ServerInstanceId
                ?? room.PendingServerInstanceId
                ?? room.LastServerInstanceId,
            alreadyTerminal);

    /// <summary>Lobby 允许 Admin 工作流触发的房间管理动作白名单。</summary>
    private enum AdminManagementRoomAction
    {
        MarkRoomAbnormal,
        ProhibitNewPlayers,
        EnableMaintenanceMode,
        ForceDissolveRoom
    }

    /// <summary>使用固定时间比较验证内部服务凭据，配置不足 32 字符时关闭内部入口。</summary>
    private static bool HasInternalCredential(
        HttpContext context,
        string expectedToken)
    {
        if (expectedToken.Length < 32) return false;
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(
                "Bearer ",
                StringComparison.OrdinalIgnoreCase))
            return false;
        var supplied = Encoding.UTF8.GetBytes(authorization[7..].Trim());
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        var valid = supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected);
        CryptographicOperations.ZeroMemory(supplied);
        CryptographicOperations.ZeroMemory(expected);
        return valid;
    }

    /// <summary>提取内部 Bearer 凭据；缺失或格式不符时返回空字符串。</summary>
    private static string GetBearerCredential(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(
                "Bearer ",
                StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return authorization[7..].Trim();
    }

    /// <summary>解析 Lobby 内部键集游标；损坏游标直接拒绝，避免退化为不可控全表扫描。</summary>
    private static bool TryReadMonitoringCursor(
        string? cursor,
        string expectedFilterFingerprint,
        out DateTimeOffset? createdAtUtc,
        out string? roomId)
    {
        createdAtUtc = null;
        roomId = null;
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        try
        {
            var payload = JsonSerializer.Deserialize<LobbyMonitoringCursor>(
                Convert.FromBase64String(cursor));
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.RoomId)
                || !string.Equals(
                    payload.FilterFingerprint,
                    expectedFilterFingerprint,
                    StringComparison.Ordinal))
                return false;
            createdAtUtc = payload.CreatedAtUtc;
            roomId = payload.RoomId;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>生成不含玩家信息的房间分页游标，供 Admin 聚合器断点读取下一页。</summary>
    private static string WriteMonitoringCursor(
        DateTimeOffset createdAtUtc,
        string roomId,
        string filterFingerprint) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new LobbyMonitoringCursor(
                createdAtUtc,
                roomId,
                filterFingerprint)));

    /// <summary>将标准化筛选条件绑定到游标，防止跨筛选复用导致错页或越权读取。</summary>
    private static string CreateMonitoringFilterFingerprint(
        string? lifecycle,
        string? gameMode,
        string? search) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(
                '\n',
                lifecycle?.Trim().ToUpperInvariant() ?? string.Empty,
                gameMode?.Trim().ToUpperInvariant() ?? string.Empty,
                search?.Trim().ToUpperInvariant() ?? string.Empty))));

    /// <summary>Lobby 房间键集游标，创建时间和 RoomId 共同保证确定性顺序。</summary>
    private sealed record LobbyMonitoringCursor(
        DateTimeOffset CreatedAtUtc,
        string RoomId,
        string FilterFingerprint);
}
