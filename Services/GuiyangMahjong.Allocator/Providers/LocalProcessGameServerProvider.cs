using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Services;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;

namespace GuiyangMahjong.Allocator.Providers;

/// <summary>
/// 本机 Dedicated Server Provider：独占管理端口、受控子进程、恢复核对和孤儿检测。
/// 可执行文件来自启动配置而非 HTTP 输入，因此生产接口不能借此启动任意程序。
/// </summary>
public sealed class LocalProcessGameServerProvider(
    PortLeasePool ports,
    IGameServerProcessLauncher launcher,
    IOptions<AllocatorOptions> options,
    ILogger<LocalProcessGameServerProvider> logger) : IGameServerProvider
{
    private readonly AllocatorOptions options = options.Value;

    /// <inheritdoc/>
    public AllocatorBackendMode Mode => AllocatorBackendMode.LocalProcess;

    /// <inheritdoc/>
    public async Task<GameServerProviderHandle> AllocateAsync(
        GameServerProviderRequest request,
        CancellationToken cancellationToken)
    {
        var port = options.ValidateOperatingSystemPortAvailability
            ? ports.Acquire(IsBindableUdpPort)
            : ports.Acquire();
        IManagedGameServerProcess? process = null;
        try
        {
            var launchSpec = request.LaunchSpec with { Port = port };
            process = await launcher.LaunchAsync(launchSpec, cancellationToken);
            if (process.HasExited)
                throw new InvalidOperationException("GameServer process exited during startup.");
            return new GameServerProviderHandle(
                Mode.ToString(),
                options.AdvertisedIp,
                port,
                process,
                null);
        }
        catch
        {
            if (process is not null)
            {
                try
                {
                    await process.StopAsync(TimeSpan.Zero, CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    logger.LogWarning(
                        exception,
                        "Failed to dispose rejected GameServer process ProcessId={ProcessId}",
                        process.ProcessId);
                }
            }
            ports.Release(port);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<GameServerProviderStatus> GetStatusAsync(
        GameServerProviderHandle handle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = handle.Process is { HasExited: false };
        return Task.FromResult(new GameServerProviderStatus(
            exists,
            exists,
            exists,
            exists ? "Running" : "Exited",
            handle.Process?.ProcessId));
    }

    /// <inheritdoc/>
    public async Task DrainAsync(
        GameServerProviderHandle handle,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        if (handle.Process is not null)
            await handle.Process.StopAsync(gracePeriod, cancellationToken);
        ports.Release(handle.Port);
    }

    /// <inheritdoc/>
    public async Task TerminateAsync(
        GameServerProviderHandle handle,
        CancellationToken cancellationToken)
    {
        if (handle.Process is not null)
            await handle.Process.StopAsync(TimeSpan.Zero, cancellationToken);
        ports.Release(handle.Port);
    }

    /// <inheritdoc/>
    public async Task RenewLeaseAsync(
        GameServerProviderHandle handle,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(handle, cancellationToken);
        if (!status.Healthy)
            throw new AllocatorOperationException("GameServer process is not running.", 409);
    }

    /// <inheritdoc/>
    public async Task ReportReadyAsync(
        GameServerProviderHandle handle,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(handle, cancellationToken);
        if (!status.Ready)
            throw new AllocatorOperationException("GameServer process exited before Ready.", 409);
    }

    /// <inheritdoc/>
    public Task ReportUnhealthyAsync(
        GameServerProviderHandle handle,
        string reason,
        CancellationToken cancellationToken) =>
        TerminateAsync(handle, cancellationToken);

    /// <inheritdoc/>
    public async Task<GameServerProviderHandle?> RecoverAsync(
        PersistedGameServerInstance instance,
        CancellationToken cancellationToken)
    {
        if (!ports.TryReserve(instance.Port)) return null;
        if (instance.ProcessId is null || instance.ProcessStartedAtUtc is null)
        {
            ports.Release(instance.Port);
            return null;
        }
        var process = await launcher.TryAttachAsync(
            instance.ProcessId.Value,
            instance.ProcessStartedAtUtc.Value,
            cancellationToken);
        if (process is null || process.HasExited)
        {
            ports.Release(instance.Port);
            return null;
        }
        return new GameServerProviderHandle(
            Mode.ToString(),
            instance.AdvertisedIp,
            instance.Port,
            process,
            null);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> FindOrphanedAsync(
        IReadOnlySet<int> knownProcessIds,
        CancellationToken cancellationToken)
    {
        var observed = await launcher.ListManagedProcessesAsync(cancellationToken);
        var orphans = observed
            .Where(process => !knownProcessIds.Contains(process.ProcessId))
            .Select(process => process.ProcessId)
            .Order()
            .ToArray();
        foreach (var processId in orphans)
            logger.LogWarning("Detected suspected orphan GameServer ProcessId={ProcessId}", processId);
        return orphans;
    }

    /// <inheritdoc/>
    public Task<bool> CheckReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (launcher is not GameServerProcessLauncher processLauncher)
                return Task.FromResult(ports.AvailableCount > 0);
            return Task.FromResult(
                GameServerProcessLauncher.IsExecutable(
                    processLauncher.GetResolvedExecutablePath())
                && Directory.Exists(processLauncher.GetResolvedWorkingDirectory())
                && (!options.ValidateOperatingSystemPortAvailability
                    || HasBindableUdpPort()));
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>核对至少一个逻辑空闲端口未被外部进程占用；只做就绪检查，不改变端口租约。</summary>
    private bool HasBindableUdpPort()
    {
        foreach (var port in ports.GetAvailablePorts())
        {
            if (IsBindableUdpPort(port)) return true;
        }
        return false;
    }

    /// <summary>尝试临时独占绑定 UDP 端口；返回前释放探测 Socket，由紧随其后的 DS 启动完成最终绑定。</summary>
    private static bool IsBindableUdpPort(int port)
    {
        try
        {
            using var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp)
            {
                ExclusiveAddressUse = true
            };
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
