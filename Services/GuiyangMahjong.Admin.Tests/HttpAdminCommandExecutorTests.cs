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

    [Fact]
    public async Task RoomControlCallsLobbyWithApprovedStateSequence()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """{"roomId":"room-command-test","stateSequence":43,"markedAbnormal":true}"""));
        var executor = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(
            CreateCommand(
                AdminManagementActionType.MarkRoomAbnormal,
                "Room",
                "room-command-test",
                42),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "lobby-command-token-that-is-at-least-32-characters",
            request.Token);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(
            "MarkRoomAbnormal",
            body.RootElement.GetProperty("actionType").GetString());
        Assert.Equal(
            42,
            body.RootElement.GetProperty("expectedStateSequence").GetInt64());
    }

    [Fact]
    public async Task InstanceTerminationRoutesToSnapshotAllocatorWithExpectedState()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """{"instance":{"state":"Stopped"},"alreadyStopped":false}"""));
        var executor = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(
            CreateCommand(
                AdminManagementActionType.TerminateAbnormalServer,
                "DedicatedServer",
                "instance-command-test"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "allocator-command-token-that-is-at-least-32-characters",
            request.Token);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(
            "Failed",
            body.RootElement.GetProperty("expectedState").GetString());
    }

    [Fact]
    public async Task TemporaryFreezeAppliesAuthControlThenDisconnectsLobby()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/controls")
                ? JsonResponse(
                    HttpStatusCode.OK,
                    """{"afterState":{"version":8,"accountStatus":"Frozen"},"revokedSessionCount":1}""")
                : JsonResponse(
                    HttpStatusCode.OK,
                    """{"revokedBeforeUtc":"2026-07-27T08:00:00Z"}"""));
        var executor = CreateExecutor(handler);
        var command = CreateCommand(
            AdminManagementActionType.TemporaryFreezePlayer,
            "Player",
            "player-sanction-test");

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/controls", handler.Requests[0].Path);
        Assert.EndsWith("/disconnect", handler.Requests[1].Path);
        Assert.All(handler.Requests, request =>
            Assert.Equal(command.OutboxId, request.IdempotencyKey));
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal(
            "TemporaryFreezePlayer",
            body.RootElement.GetProperty("actionType").GetString());
        Assert.Equal(
            7,
            body.RootElement.GetProperty("expectedVersion").GetInt64());
        Assert.Equal(
            "player-approver",
            body.RootElement.GetProperty("approvedBy").GetString());
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-28T08:00:00Z"),
            body.RootElement.GetProperty("expiresAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task RiskMarkUsesAuthOnlyAndCarriesApprovedAuditIdentity()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """{"afterState":{"version":8,"riskLabels":["manual-review"]}}"""));
        var executor = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(
            CreateCommand(
                AdminManagementActionType.MarkRiskAccount,
                "Player",
                "player-risk-test"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/controls", request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(
            "manual-review",
            body.RootElement.GetProperty("riskLabel").GetString());
        Assert.Equal(
            "operator",
            body.RootElement.GetProperty("requestedBy").GetString());
    }

    private static HttpAdminCommandExecutor CreateExecutor(HttpMessageHandler handler) =>
        new(
            new TestHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                Auth = new AuthMonitoringOptions { BaseUrl = "http://auth.test" },
                Lobby = new LobbyMonitoringOptions { BaseUrl = "http://lobby.test" },
                Allocators =
                [
                    new AllocatorMonitoringOptions
                    {
                        Enabled = true,
                        ClusterId = "cluster-test",
                        NodeId = "node-test",
                        BaseUrl = "http://allocator.test",
                        ManagementCommandToken =
                            "allocator-command-token-that-is-at-least-32-characters"
                    }
                ],
                Management = new AdminManagementOptions
                {
                    AuthCommandToken =
                        "auth-command-token-that-is-at-least-32-characters",
                    LobbyCommandToken =
                        "lobby-command-token-that-is-at-least-32-characters",
                    CommandTimeoutSeconds = 5
                }
            }));

    private static AdminCommandOutboxRecord CreateCommand(
        AdminManagementActionType actionType =
            AdminManagementActionType.ForceLogoutPlayer,
        string targetType = "Player",
        string targetId = "player-command-test",
        long? expectedStateSequence = null)
    {
        var now = new DateTimeOffset(2026, 7, 27, 8, 0, 0, TimeSpan.Zero);
        var beforeState = targetType == "DedicatedServer"
            ? JsonSerializer.SerializeToElement(new
            {
                clusterId = "cluster-test",
                nodeId = "node-test",
                state = "Failed"
            })
            : JsonSerializer.SerializeToElement(new
            {
                activeSessionCount = 2,
                controlVersion = 7
            });
        var action = new AdminActionRecord(
            Guid.NewGuid().ToString(),
            actionType,
            targetType,
            targetId,
            "operator",
            now,
            now.AddMinutes(5),
            now.AddHours(1),
            now,
            "Security investigation forced logout",
            "TICKET-COMMAND-TEST",
            Guid.NewGuid().ToString(),
            expectedStateSequence,
            new string('a', 64),
            beforeState,
            AdminActionStatus.ApprovedAwaitingExecution,
            new AdminActionApproval(
                Guid.NewGuid().ToString(),
                "player-approver",
                now,
                ApprovalDecision.Approve,
                "Approved for test execution"),
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
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Parameter ?? string.Empty,
                request.Headers.GetValues("Idempotency-Key").Single(),
                request.Headers.GetValues("X-Trace-Id").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return responseFactory(request);
        }
    }

    private sealed record RecordedRequest(
        string Path,
        string Token,
        string IdempotencyKey,
        string TraceId,
        string Body);
}
