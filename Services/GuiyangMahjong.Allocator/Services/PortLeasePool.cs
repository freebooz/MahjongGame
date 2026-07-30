using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

/// <summary>
/// 单节点 Dedicated Server 端口租约池。
/// available 与 leased 在 gate 内原子迁移；池只管理配置闭区间内的端口，
/// 实际监听失败由实例管理器负责补偿释放。
/// </summary>
public sealed class PortLeasePool
{
    // available 保持升序以实现确定性最低端口分配，leased 防止重复释放和恢复冲突。
    private readonly SortedSet<int> available;
    private readonly HashSet<int> leased = [];
    private readonly Lock gate = new();

    /// <summary>根据已验证的起止端口创建完整可用集合；起止范围在配置启动校验阶段保证合法。</summary>
    public PortLeasePool(IOptions<AllocatorOptions> options)
    {
        available = new SortedSet<int>(Enumerable.Range(
            options.Value.PortStart,
            options.Value.PortEnd - options.Value.PortStart + 1));
    }

    /// <summary>租用当前最小可用端口；容量耗尽时抛出可映射为 503 的领域异常。</summary>
    public int Acquire()
    {
        lock (gate)
        {
            if (available.Count == 0) throw new AllocatorOperationException("没有可用的 GameServer 端口", 503);
            var port = available.Min;
            available.Remove(port);
            leased.Add(port);
            return port;
        }
    }

    /// <summary>归还已租用端口；未知或重复释放返回 false，不污染可用集合。</summary>
    public bool Release(int port)
    {
        lock (gate)
        {
            if (!leased.Remove(port)) return false;
            available.Add(port);
            return true;
        }
    }

    /// <summary>恢复持久化实例时预留指定端口；端口不在可用集合或已占用时返回 false。</summary>
    public bool TryReserve(int port)
    {
        lock (gate)
        {
            if (!available.Remove(port)) return false;
            leased.Add(port);
            return true;
        }
    }

    /// <summary>当前可租用端口数量；值在读取锁内形成一致快照。</summary>
    public int AvailableCount
    {
        get { lock (gate) return available.Count; }
    }

    /// <summary>返回升序可用端口副本，供就绪探针观察；调用方不能修改池状态。</summary>
    public IReadOnlyList<int> GetAvailablePorts()
    {
        lock (gate) return available.ToArray();
    }
}
