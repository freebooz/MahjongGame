using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GuiyangMahjong.Lobby.Realtime;

/// <summary>Lobby 可发布的固定实时事件类型白名单，防止任意类型和指标高基数。</summary>
public static class LobbyEventTypes
{
    // 常量是 WebSocket/Redis 线协议的一部分，重命名需要兼容版本迁移。
    public const string LobbyUpdated = "lobby.updated";
    public const string RoomUpdated = "room.updated";
    public const string ServerAssigned = "server.assigned";
    public const string RoomClosed = "room.closed";
    public const string PlayerSessionRevoked = "player.session.revoked";

    /// <summary>所有允许发布的事件类型集合；发布入口在序列化前据此拒绝未知类型。</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        LobbyUpdated, RoomUpdated, ServerAssigned, RoomClosed, PlayerSessionRevoked
    };
}

/// <summary>Lobby 领域服务使用的实时事件发布边界；实现负责跨实例传播和本地扇出。</summary>
public interface ILobbyEventPublisher
{
    /// <summary>发布白名单事件；data 必须是脱敏可序列化投影，取消会传播到外部发布。</summary>
    Task PublishAsync(string type, object data, CancellationToken cancellationToken);
}

/// <summary>
/// Lobby WebSocket 连接与 Redis Pub/Sub 事件中心。
/// Redis 模式使用共享序号和频道实现跨实例传播；本地模式使用进程序号。
/// 每个客户端使用有界发送队列，慢消费者不会无限占用内存。
/// </summary>
public sealed class LobbyEventHub : ILobbyEventPublisher, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, ClientConnection> clients = new();
    private readonly bool useRedis;
    private readonly IConnectionMultiplexer? redis;
    private readonly RedisChannel channel;
    private readonly RedisKey sequenceKey;
    private readonly IOnlinePresenceService presence;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LobbyEventHub> logger;
    private long localSequence;

    /// <summary>根据持久化模式配置本地或 Redis 广播，并注入在线状态和可测试时间源。</summary>
    public LobbyEventHub(
        IOptions<LobbyOptions> options,
        LobbyPersistenceConnections connections,
        IOnlinePresenceService presence,
        TimeProvider timeProvider,
        ILogger<LobbyEventHub> logger)
    {
        var persistence = options.Value.Persistence;
        useRedis = persistence.Mode.Equals("RedisPostgres", StringComparison.OrdinalIgnoreCase);
        redis = useRedis ? connections.Redis : null;
        channel = RedisChannel.Literal($"{persistence.RedisKeyPrefix}:events");
        sequenceKey = $"{persistence.RedisKeyPrefix}:events:sequence";
        this.presence = presence;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>当前实例持有的 WebSocket 客户端数量，不代表整个 Lobby 集群在线人数。</summary>
    public int ConnectedClientCount => clients.Count;

    /// <summary>Redis 模式订阅共享频道并把合法信封分发到本地客户端；本地模式无副作用。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!useRedis) return;
        await redis!.GetSubscriber().SubscribeAsync(channel, (_, value) =>
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<LobbyEventEnvelope>((string)value!, JsonOptions);
                if (envelope is not null) Dispatch(envelope);
            }
            catch (JsonException exception)
            {
                logger.LogError(exception, "Redis lobby event payload is invalid");
            }
        }).WaitAsync(cancellationToken);
    }

    /// <summary>取消 Redis 订阅并终止本实例全部 WebSocket；不修改其他 Lobby 实例连接。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (useRedis)
            await redis!.GetSubscriber().UnsubscribeAsync(channel).WaitAsync(cancellationToken);
        foreach (var client in clients.Values) client.Socket.Abort();
    }

    /// <summary>
    /// 注册已认证玩家的 WebSocket，维护在线状态并运行有界收发循环。
    /// 退出时无论正常关闭、取消或异常都移除连接并刷新 presence。
    /// </summary>
    public async Task HandleClientAsync(
        PlayerIdentity player,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var connection = new ClientConnection(player.PlayerId, socket);
        clients[id] = connection;
        var sender = SendLoopAsync(connection, cancellationToken);
        try
        {
            var onlineCount = await presence.GetOnlineCountAsync(cancellationToken);
            Enqueue(connection, new LobbyEventEnvelope(
                LobbyEventTypes.LobbyUpdated,
                await NextSequenceAsync(cancellationToken),
                timeProvider.GetUtcNow(),
                new { onlinePlayerCount = onlineCount }));

            var buffer = new byte[512];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            clients.TryRemove(id, out _);
            connection.Outbound.Writer.TryComplete();
            try { await sender; } catch (OperationCanceledException) { } catch (WebSocketException) { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "连接结束", CancellationToken.None);
            }
            socket.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task PublishAsync(string type, object data, CancellationToken cancellationToken)
    {
        if (!LobbyEventTypes.All.Contains(type))
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown lobby event type.");
        var envelope = new LobbyEventEnvelope(
            type,
            await NextSequenceAsync(cancellationToken),
            timeProvider.GetUtcNow(),
            data);
        if (useRedis)
        {
            var payload = JsonSerializer.Serialize(envelope, JsonOptions);
            await redis!.GetSubscriber().PublishAsync(channel, payload).WaitAsync(cancellationToken);
        }
        else
        {
            Dispatch(envelope);
        }
    }

    /// <summary>发布玩家会话撤销事件，使该玩家在所有 Lobby 实例上的连接主动断开。</summary>
    public Task DisconnectPlayerAsync(
        string playerId,
        CancellationToken cancellationToken) =>
        PublishAsync(
            LobbyEventTypes.PlayerSessionRevoked,
            new PlayerSessionRevokedEvent(playerId),
            cancellationToken);

    private async Task<long> NextSequenceAsync(CancellationToken cancellationToken) => useRedis
        ? await redis!.GetDatabase().StringIncrementAsync(sequenceKey).WaitAsync(cancellationToken)
        : Interlocked.Increment(ref localSequence);

    private void Dispatch(LobbyEventEnvelope envelope)
    {
        if (envelope.Type == LobbyEventTypes.PlayerSessionRevoked
            && TryGetRevokedPlayerId(envelope.Data, out var playerId))
        {
            foreach (var client in clients.Values)
                if (client.PlayerId == playerId) client.Socket.Abort();
            return;
        }
        foreach (var client in clients.Values) Enqueue(client, envelope);
    }

    private static bool TryGetRevokedPlayerId(object data, out string playerId)
    {
        if (data is PlayerSessionRevokedEvent typed)
        {
            playerId = typed.PlayerId;
            return true;
        }
        if (data is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("playerId", out var property))
        {
            playerId = property.GetString() ?? string.Empty;
            return playerId.Length > 0;
        }
        playerId = string.Empty;
        return false;
    }

    private static void Enqueue(ClientConnection client, LobbyEventEnvelope envelope)
    {
        if (!client.Outbound.Writer.TryWrite(envelope)) client.Socket.Abort();
    }

    private static async Task SendLoopAsync(
        ClientConnection connection,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in connection.Outbound.Reader.ReadAllAsync(cancellationToken))
        {
            if (connection.Socket.State != WebSocketState.Open) break;
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            await connection.Socket.SendAsync(
                payload, WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private sealed class ClientConnection(string playerId, WebSocket socket)
    {
        /// <summary>该连接绑定的已认证玩家标识，连接存活期间不可变。</summary>
        public string PlayerId { get; } = playerId;

        /// <summary>由 Hub 拥有的 WebSocket；移除连接后不再复用。</summary>
        public WebSocket Socket { get; } = socket;

        /// <summary>容量 64 的单读多写发送队列；溢出策略避免慢客户端造成无界内存增长。</summary>
        public Channel<LobbyEventEnvelope> Outbound { get; } = Channel.CreateBounded<LobbyEventEnvelope>(
            new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
    }

    private sealed record PlayerSessionRevokedEvent(string PlayerId);
}
