using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

/// <summary>
/// 周期驱动实例管理器超时、进程退出和故障通知扫描的后台服务。
/// 扫描间隔来自已验证配置；单轮异常记录后继续，宿主取消时退出。
/// </summary>
public sealed class InstanceMonitorService(
    GameServerInstanceManager manager,
    IOptions<AllocatorOptions> options,
    ILogger<InstanceMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(options.Value.MonitorIntervalMilliseconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await manager.MonitorAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "GameServer 监控轮询失败"); }
        }
    }
}
