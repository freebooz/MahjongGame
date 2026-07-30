namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 定义 Lobby 写操作的幂等执行边界。
/// 同一幂等键只能共享一次成功结果，失败结果不得阻止调用方重试。
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// 按幂等键执行或重放异步操作。
    /// </summary>
    /// <param name="key">调用方生成且在业务操作范围内唯一的幂等键。</param>
    /// <param name="operation">仅允许成功执行一次的业务操作。</param>
    /// <param name="cancellationToken">等待结果的取消令牌；取消不会把失败结果写入缓存。</param>
    /// <returns>首次成功执行或已缓存的 HTTP 响应。</returns>
    Task<IdempotentHttpResponse> ExecuteAsync(
        string key,
        Func<Task<IdempotentHttpResponse>> operation,
        CancellationToken cancellationToken);
}
