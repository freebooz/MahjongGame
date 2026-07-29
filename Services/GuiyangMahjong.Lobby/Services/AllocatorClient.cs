using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

public sealed record AllocatorAllocation(
    string RequestId,
    string RoomId,
    string ServerInstanceId,
    int Port,
    string State);

public sealed record AllocatorRegistrationAck(
    string RequestId,
    string ServerInstanceId,
    bool Accepted,
    int HeartbeatIntervalSeconds,
    string HeartbeatCredential);

public interface IAllocatorClient
{
    bool Enabled { get; }
    Task<bool> CheckReadinessAsync(CancellationToken cancellationToken);
    Task<AllocatorAllocation> AllocateAsync(
        string requestId,
        string roomId,
        string matchId,
        CancellationToken cancellationToken);
    Task<AllocatorRegistrationAck> ConfirmRegistrationAsync(
        string requestId,
        GameServerRegistration request,
        CancellationToken cancellationToken);
    Task RecordHeartbeatAsync(
        string requestId,
        string serverInstanceId,
        GameServerHeartbeat request,
        CancellationToken cancellationToken);
    Task DrainAsync(string requestId, string serverInstanceId, CancellationToken cancellationToken);
}

public sealed class DisabledAllocatorClient : IAllocatorClient
{
    public bool Enabled => false;
    public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<AllocatorAllocation> AllocateAsync(
        string requestId, string roomId, string matchId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Allocator is disabled.");

    public Task<AllocatorRegistrationAck> ConfirmRegistrationAsync(
        string requestId, GameServerRegistration request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Allocator is disabled.");

    public Task RecordHeartbeatAsync(
        string requestId,
        string serverInstanceId,
        GameServerHeartbeat request,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DrainAsync(
        string requestId, string serverInstanceId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class HttpAllocatorClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LobbyOptions> options) : IAllocatorClient
{
    private readonly AllocatorClientOptions options = options.Value.Allocator;
    public bool Enabled => options.Enabled;

    public async Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client().GetAsync("/health/ready", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<AllocatorAllocation> AllocateAsync(
        string requestId,
        string roomId,
        string matchId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "/internal/allocations", requestId);
        request.Content = JsonContent.Create(new { roomId, matchId, buildVersion = options.GameServerBuildVersion });
        return await SendAsync<AllocatorAllocation>(request, cancellationToken);
    }

    public async Task<AllocatorRegistrationAck> ConfirmRegistrationAsync(
        string requestId,
        GameServerRegistration registration,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"/internal/instances/{registration.ServerInstanceId}/register",
            requestId);
        request.Content = JsonContent.Create(new
        {
            registration.RoomId,
            registration.ListenIp,
            registration.ListenPort,
            registration.BuildVersion,
            registration.RegistrationCredential
        });
        return await SendAsync<AllocatorRegistrationAck>(request, cancellationToken);
    }

    public async Task RecordHeartbeatAsync(
        string requestId,
        string serverInstanceId,
        GameServerHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"/internal/instances/{serverInstanceId}/heartbeat",
            requestId);
        request.Content = JsonContent.Create(heartbeat);
        using var response = await Client().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DrainAsync(
        string requestId, string serverInstanceId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"/internal/instances/{serverInstanceId}/drain",
            requestId);
        using var response = await Client().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string requestId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceToken);
        request.Headers.Add("X-Request-Id", requestId);
        request.Headers.Add(
            "X-Trace-Id",
            MahjongTelemetry.CurrentBusinessTraceId);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Client().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new HttpRequestException("Allocator returned an empty response.");
    }

    private HttpClient Client()
    {
        var client = httpClientFactory.CreateClient(nameof(HttpAllocatorClient));
        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        return client;
    }
}
