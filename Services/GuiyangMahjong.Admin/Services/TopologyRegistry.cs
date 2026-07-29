using System.Collections.Concurrent;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>
/// 多地域来源租约目录。相同 SourceId 采用更高 Generation；相同拓扑路由冲突时，
/// 仅 SourceId 字典序最小的来源生效，使所有 Admin 副本得到确定结果。
/// </summary>
public sealed class TopologyRegistry(
    IOptions<AdminOptions> options,
    TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, MonitoringSourceLease> leases =
        new(StringComparer.Ordinal);
    private readonly TopologyDiscoveryOptions settings =
        options.Value.TopologyDiscovery;

    /// <summary>
    /// 幂等注册或刷新租约；旧 Generation 和同 Generation 不同 RegistrationId 会被拒绝，
    /// 避免旧进程在网络分区恢复后覆盖新进程。
    /// </summary>
    public MonitoringSourceLease Register(
        MonitoringSourceRegistration registration)
    {
        Validate(registration);
        var now = timeProvider.GetUtcNow();
        var proposed = new MonitoringSourceLease(
            registration with { RegisteredAtUtc = now },
            now.AddSeconds(settings.LeaseSeconds),
            "Active",
            null);
        leases.AddOrUpdate(
            registration.SourceId,
            proposed,
            (_, existing) =>
            {
                if (registration.Generation < existing.Registration.Generation)
                    return existing;
                if (registration.Generation == existing.Registration.Generation
                    && registration.RegistrationId
                        != existing.Registration.RegistrationId)
                {
                    return existing;
                }
                return proposed;
            });
        ReconcileConflicts(now);
        return leases[registration.SourceId];
    }

    /// <summary>返回未过期且未冲突来源；地域故障只移除对应租约，不影响其他地域。</summary>
    public MonitoringSourceLease[] ListActive(MonitoringSourceKind kind)
    {
        var now = timeProvider.GetUtcNow();
        ReconcileConflicts(now);
        return leases.Values
            .Where(item =>
                item.ExpiresAtUtc > now
                && item.Status == "Active"
                && item.Registration.Kind == kind)
            .OrderBy(item => item.Registration.RegionId, StringComparer.Ordinal)
            .ThenBy(item => item.Registration.ClusterId, StringComparer.Ordinal)
            .ThenBy(item => item.Registration.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>返回全部租约供管理台诊断，包括冲突和过期来源。</summary>
    public MonitoringSourceLease[] ListAll()
    {
        var now = timeProvider.GetUtcNow();
        ReconcileConflicts(now);
        return leases.Values
            .OrderBy(item => item.Registration.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    private void ReconcileConflicts(DateTimeOffset now)
    {
        foreach (var key in leases.Keys)
        {
            if (leases.TryGetValue(key, out var lease)
                && lease.ExpiresAtUtc <= now)
            {
                leases[key] = lease with
                {
                    Status = "Expired",
                    ConflictWith = null
                };
            }
        }
        var active = leases.Values
            .Where(item => item.ExpiresAtUtc > now)
            .GroupBy(item => RouteKey(item.Registration), StringComparer.Ordinal);
        foreach (var group in active)
        {
            var ordered = group
                .OrderBy(item => item.Registration.SourceId, StringComparer.Ordinal)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var current = ordered[index];
                leases[current.Registration.SourceId] = current with
                {
                    Status = index == 0 ? "Active" : "Conflict",
                    ConflictWith = index == 0
                        ? null
                        : ordered[0].Registration.SourceId
                };
            }
        }
    }

    private static string RouteKey(MonitoringSourceRegistration value) =>
        $"{value.Kind}/{value.RegionId}/{value.ClusterId}/{value.LobbyId}/{value.NodeId}";

    private static void Validate(MonitoringSourceRegistration value)
    {
        var ids = new[]
        {
            value.RegistrationId,
            value.SourceId,
            value.RegionId,
            value.ClusterId,
            value.LobbyId,
            value.NodeId
        };
        if (ids.Any(item =>
                item.Length is < 1 or > 128
                || item.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character)
                        || character is '.' or '_' or ':' or '-')))
            || value.Generation < 1
            || !Uri.TryCreate(value.BaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw AdminOperationException.Invalid(
                "Topology registration contains invalid identity or endpoint metadata.");
        }
    }
}
