using System.Net;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Services;

namespace GuiyangMahjong.Admin.Tests;

public sealed class HttpAdminCommandExecutorTests
{
    [Fact]
    public async Task PlayerSessionCommandCallsAuthAndLobbyWithSameIdempotencyKey()
    {
        var handler = new RecordingHandler(request =>
            JsonResponse(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.Contains("revoke")
                ? """{"revokedSessionCount":2}"""
                : """{"revokedBeforeUtc":"2026-07-27T08:00:00Z"}"""));
        var executor = CreateExecutor(handler);
        var command = CreateCommand();

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(command.OutboxId, request.IdempotencyKey);
            Assert.Equal(command.TraceId, request.TraceId);
        });
        Assert.Equal("auth-command-token-that-is-at-least-32-characters", handler.Requests[0].Token);
        Assert.Equal("lobby-command-token-that-is-at-least-32-characters", handler.Requests[1].Token);
    }

    [Fact]
    public async Task RetryableLobbyFailurePreservesSuccessfulAuthState()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("revoke")
                ? JsonResponse(HttpStatusCode.OK, """{"revokedSessionCount":1}""")
                : JsonResponse(HttpStatusCode.ServiceUnavailable, """{"code":"UNAVAILABLE"}"""));
        var executor = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(
            CreateCommand(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        Assert.Equal("LobbyCommandFailed", result.AfterState.GetProperty("status").GetString());
        Assert.Equal(
            1,
            result.AfterState.GetProperty("auth")
                .GetProperty("revokedSessionCount")
                .GetInt32());
    }

    private static HttpAdminCommandExecutor CreateExecutor(HttpMessageHandler handler) =>
        new(
            new TestHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                Auth = new AuthMonitoringOptions { BaseUrl = "http://auth.test" },
                Lobby = new LobbyMonitoringOptions { BaseUrl = "http://lobby.test" },
                Management = new AdminManagementOptions
                {
                    AuthCommandToken =
                        "auth-command-token-that-is-at-least-32-characters",
                    LobbyCommandToken =
                        "lobby-command-token-that-is-at-least-32-characters",
                    CommandTimeoutSeconds = 5
                }
            }));

    private static AdminCommandOutboxRecord CreateCommand()
    {
        var now = new DateTimeOffset(2026, 7, 27, 8, 0, 0, TimeSpan.Zero);
        var action = new AdminActionRecord(
            Guid.NewGuid().ToString(),
            AdminManagementActionType.ForceLogoutPlayer,
            "Player",
            "player-command-test",
            "operator",
            now,
            now.AddMinutes(5),
            now.AddHours(1),
            now,
            "Security investigation forced logout",
            "TICKET-COMMAND-TEST",
            Guid.NewGuid().ToString(),
            null,
            new string('a', 64),
            JsonSerializer.SerializeToElement(new { activeSessionCount = 2 }),
            AdminActionStatus.ApprovedAwaitingExecution,
            null,
            2);
        return new AdminCommandOutboxRecord(
            Guid.NewGuid().ToString(),
            action.ActionRequestId,
            action.ActionType,
            action.TargetType,
            action.TargetId,
            JsonSerializer.SerializeToElement(
                action,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            action.TraceId,
            "Processing",
            1,
            now,
            now,
            now,
            "worker-test",
            now.AddSeconds(30),
            null,
            null);
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class TestHttpClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Headers.Authorization?.Parameter ?? string.Empty,
                request.Headers.GetValues("Idempotency-Key").Single(),
                request.Headers.GetValues("X-Trace-Id").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return responseFactory(request);
        }
    }

    private sealed record RecordedRequest(
        string Token,
        string IdempotencyKey,
        string TraceId,
        string Body);
}
