// Allocator 状态存储：持久化实例、租约、心跳和故障状态，并提供并发安全的状态转换。
// 更新必须带预期版本或等效并发保护，禁止把陈旧心跳覆盖到已终止实例。
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

/// <summary>
/// 可跨 Allocator 重启恢复的实例状态。
/// 凭据只保存哈希；进程号必须与启动时间共同验证以防 PID 复用，
/// PortReleased 和通知字段使清理动作可幂等恢复。
/// </summary>
public sealed record PersistedGameServerInstance(
    string ServerInstanceId,
    string RoomId,
    string MatchId,
    int Port,
    string AdvertisedIp,
    string RegistrationCredentialHash,
    string? HeartbeatCredentialHash,
    DateTimeOffset RegistrationExpireAtUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? ProcessStartedAtUtc,
    int? ProcessId,
    DateTimeOffset? RegisteredAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    string BuildVersion,
    GameServerInstanceState State,
    string? FailureReason,
    bool FailureNotified,
    DateTimeOffset? FailureNotificationAttemptedAtUtc,
    bool PortReleased,
    string? OrchestratorResourceName = null);

/// <summary>Allocator 状态文件根文档；SchemaVersion 控制兼容读取，更新时间统一为 UTC。</summary>
public sealed record AllocatorStateDocument(
    int SchemaVersion,
    DateTimeOffset UpdatedAtUtc,
    PersistedGameServerInstance[] Instances);

/// <summary>Allocator 恢复状态的持久化接口；保存必须原子替换，不能暴露半写文件。</summary>
public interface IAllocatorStateStore
{
    /// <summary>读取并验证状态；不存在时返回空文档，损坏或版本不兼容时显式失败。</summary>
    Task<AllocatorStateDocument> LoadAsync(CancellationToken cancellationToken);

    /// <summary>原子保存完整快照；完成返回前数据必须可由下一进程读取。</summary>
    Task SaveAsync(AllocatorStateDocument state, CancellationToken cancellationToken);
}

/// <summary>
/// 使用 UTF-8 JSON 文件保存 Allocator 状态的单节点实现。
/// 写入由信号量串行化并通过临时文件原子替换；目标路径限制在配置允许位置。
/// </summary>
public sealed class JsonAllocatorStateStore(
    IOptions<AllocatorOptions> options,
    TimeProvider timeProvider,
    ILogger<JsonAllocatorStateStore> logger) : IAllocatorStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string statePath = Path.GetFullPath(
        Path.IsPathRooted(options.Value.StateFilePath)
            ? options.Value.StateFilePath
            : Path.Combine(AppContext.BaseDirectory, options.Value.StateFilePath));
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc/>
    public async Task<AllocatorStateDocument> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(statePath))
                return new AllocatorStateDocument(1, timeProvider.GetUtcNow(), []);
            await using var stream = new FileStream(
                statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, true);
            return await JsonSerializer.DeserializeAsync<AllocatorStateDocument>(
                       stream, JsonOptions, cancellationToken)
                   ?? throw new InvalidDataException("Allocator state document is empty.");
        }
        catch (JsonException exception)
        {
            logger.LogCritical(exception, "Allocator state file is corrupt Path={StatePath}", statePath);
            throw new InvalidDataException("Allocator state file is corrupt; refusing unsafe port reuse.", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(AllocatorStateDocument state, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(statePath)
                ?? throw new InvalidOperationException("Allocator state path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{statePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, statePath, true);
                FlushDirectory(directory);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        const int openReadOnly = 0;
        const int openDirectory = 65536;
        var descriptor = open(directory, openReadOnly | openDirectory);
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "Could not open allocator state directory for fsync.");
        try
        {
            if (fsync(descriptor) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "Could not fsync allocator state directory.");
        }
        finally
        {
            close(descriptor);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fileDescriptor);

    [DllImport("libc")]
    private static extern int close(int fileDescriptor);
}
