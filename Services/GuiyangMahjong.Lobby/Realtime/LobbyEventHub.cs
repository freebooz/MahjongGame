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

public static class LobbyEventTypes
{
    public const string LobbyUpdated = "lobby.updated";
    public const string RoomUpdated = "room.updated";
    public const string ServerAssigned = "server.assigned";
    public const string RoomClosed = "room.closed";
    public const string PlayerSessionRevoked = "player.session.revoked";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        LobbyUpdated, RoomUpdated, ServerAssigned, RoomClosed, PlayerSessionRevoked
    };
}

public interface ILobbyEventPublisher
{
    Task PublishAsync(string type, object data, CancellationToken cancellationToken);
}

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

    public int ConnectedClientCount => clients.Count;

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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (useRedis)
            await redis!.GetSubscriber().UnsubscribeAsync(channel).WaitAsync(cancellationToken);
        foreach (var client in clients.Values) client.Socket.Abort();
    }

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
        public string PlayerId { get; } = playerId;
        public WebSocket Socket { get; } = socket;
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
