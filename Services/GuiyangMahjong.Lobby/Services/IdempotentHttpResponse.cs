using System.Text.Json;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 表示可被幂等存储持久化并重放的 HTTP 响应。
/// 响应体使用 <see cref="JsonElement"/> 保存，避免缓存层依赖具体业务 DTO。
/// </summary>
public sealed record IdempotentHttpResponse(int StatusCode, JsonElement Body);
