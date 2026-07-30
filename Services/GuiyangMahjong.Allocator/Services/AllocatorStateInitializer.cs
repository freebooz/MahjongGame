// Allocator 状态初始化器：在服务启动时校验持久化结构、恢复端口租约并清理失联实例。
// 初始化未完成前不得接收分配请求；恢复失败必须使就绪探针失败而不是静默使用空状态。
namespace GuiyangMahjong.Allocator.Services;

/// <summary>
/// ASP.NET 托管生命周期中的 Allocator 恢复入口。
/// StartAsync 完成前主机不会宣告启动完成；停止阶段不重复终止实例，
/// 实例清理由管理器和宿主关闭流程负责。
/// </summary>
public sealed class AllocatorStateInitializer(GameServerInstanceManager manager) : IHostedService
{
    /// <summary>恢复持久化状态、端口租约和可接管进程；失败会阻止服务启动。</summary>
    public Task StartAsync(CancellationToken cancellationToken) => manager.InitializeAsync(cancellationToken);

    /// <summary>宿主停止通知；不在此销毁仍由编排器管理的 Dedicated Server。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
