using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GuiyangMahjong.Admin.Tests;

public sealed class AdminWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-only-admin-read-token-that-is-long-enough";
    public const string OperatorToken = "test-only-room-operator-token-that-is-long-enough";
    public const string ApproverToken = "test-only-room-approver-token-that-is-long-enough";
    public const string PlayerOperatorToken = "test-only-player-operator-token-that-is-long-enough";
    public const string PlayerApproverToken = "test-only-player-approver-token-that-is-long-enough";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:ReadOnlyAccessToken"] = Token,
                ["Admin:Management:Enabled"] = "true",
                ["Admin:Principals:0:OperatorId"] = "operator-one",
                ["Admin:Principals:0:AccessToken"] = OperatorToken,
                ["Admin:Principals:0:Roles:0"] = "room.viewer",
                ["Admin:Principals:0:Roles:1"] = "player.viewer",
                ["Admin:Principals:0:Roles:2"] = "room.operator",
                ["Admin:Principals:0:Roles:3"] = "room.approver",
                ["Admin:Principals:1:OperatorId"] = "approver-two",
                ["Admin:Principals:1:AccessToken"] = ApproverToken,
                ["Admin:Principals:1:Roles:0"] = "room.viewer",
                ["Admin:Principals:1:Roles:1"] = "room.approver",
                ["Admin:Principals:1:Roles:2"] = "audit.viewer",
                ["Admin:Principals:2:OperatorId"] = "player-operator",
                ["Admin:Principals:2:AccessToken"] = PlayerOperatorToken,
                ["Admin:Principals:2:Roles:0"] = "player.viewer",
                ["Admin:Principals:2:Roles:1"] = "player.operator",
                ["Admin:Principals:2:Roles:2"] = "player.approver",
                ["Admin:Principals:2:Roles:3"] = "sanction.operator",
                ["Admin:Principals:2:Roles:4"] = "risk.analyst",
                ["Admin:Principals:2:Roles:5"] = "support.operator",
                ["Admin:Principals:2:Roles:6"] = "compensation.operator",
                ["Admin:Principals:3:OperatorId"] = "player-approver",
                ["Admin:Principals:3:AccessToken"] = PlayerApproverToken,
                ["Admin:Principals:3:Roles:0"] = "player.viewer",
                ["Admin:Principals:3:Roles:1"] = "player.approver",
                ["Admin:Principals:3:Roles:2"] = "audit.viewer",
                ["Admin:Auth:Enabled"] = "false",
                ["Admin:Lobby:Enabled"] = "false",
                ["Admin:Lobby:MonitoringToken"] = "",
                ["Admin:Allocators:0:Enabled"] = "false"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILobbyMonitoringClient>();
            services.RemoveAll<IAllocatorMonitoringClient>();
            services.RemoveAll<IPlayerDirectoryClient>();
            services.AddSingleton<ILobbyMonitoringClient, AdminTestLobbyMonitoringClient>();
            services.AddSingleton<IAllocatorMonitoringClient, AdminTestAllocatorMonitoringClient>();
            services.AddSingleton<AdminTestPlayerDirectoryClient>();
            services.AddSingleton<IPlayerDirectoryClient>(provider =>
                provider.GetRequiredService<AdminTestPlayerDirectoryClient>());
        });
    }
}

public sealed class AdminApiTests(AdminWebApplicationFactory factory)
    : IClassFixture<AdminWebApplicationFactory>
{
    [Fact]
    public async Task HealthDoesNotRequireAdministratorCredential()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MonitoringApiRejectsMissingCredential()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/admin/v1/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedOverviewIsReadOnlyAndAvailable()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminWebApplicationFactory.Token);
        using var response = await client.GetAsync("/admin/v1/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"totalRooms\":1", body, StringComparison.Ordinal);
        Assert.Contains("\"activeRooms\":1", body, StringComparison.Ordinal);
        Assert.Contains("\"dedicatedServerInstances\":0", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizedPlayerDirectoryIsAvailableWhenSourcesAreDisabled()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminWebApplicationFactory.Token);
        using var response = await client.GetAsync("/admin/v1/players");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());

        using var detail = await client.GetAsync("/admin/v1/players/unknown-player");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task PlayerControlHistoryRequiresSanctionRiskApprovalOrAuditRole()
    {
        using var viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdminWebApplicationFactory.OperatorToken);
        var redacted = await viewer.GetFromJsonAsync<PlayerMonitorDetail>(
            $"/admin/v1/players/{AdminTestPlayerDirectoryClient.PlayerId}");
        Assert.NotNull(redacted);
        Assert.Empty(redacted.ControlHistory);
        Assert.Equal(
            "ReadOnlyMaskedControlHistoryRedacted",
            redacted.DataScope);

        using var sanctionOperator = factory.CreateClient();
        sanctionOperator.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdminWebApplicationFactory.PlayerOperatorToken);
        var permitted = await sanctionOperator.GetFromJsonAsync<PlayerMonitorDetail>(
            $"/admin/v1/players/{AdminTestPlayerDirectoryClient.PlayerId}");
        Assert.NotNull(permitted);
        Assert.Single(permitted.ControlHistory);
    }

    [Fact]
    public async Task RoomManagementRequiresConfirmationSeparateApprovalAndCreatesHashChainedAudit()
    {
        using var operatorClient = factory.CreateClient();
        operatorClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminWebApplicationFactory.OperatorToken);
        using var created = await operatorClient.PostAsJsonAsync(
            "/admin/v1/action-requests",
            new
            {
                actionType = "ForceDissolveRoom",
                targetId = AdminTestLobbyMonitoringClient.RoomId,
                reason = "房间心跳持续异常，申请人工解散",
                ticketId = "INC-20260727-001",
                expectedStateSequence = 7
            });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
        var actionId = createdJson.GetProperty("actionRequestId").GetString();
        Assert.NotNull(actionId);
        Assert.Equal(
            "AwaitingConfirmation",
            createdJson.GetProperty("status").GetString());

        using var wrongConfirmation = await operatorClient.PostAsJsonAsync(
            $"/admin/v1/action-requests/{actionId}/confirm",
            new { targetConfirmation = "wrong-room" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongConfirmation.StatusCode);

        using var confirmed = await operatorClient.PostAsJsonAsync(
            $"/admin/v1/action-requests/{actionId}/confirm",
            new { targetConfirmation = AdminTestLobbyMonitoringClient.RoomId });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Contains(
            "\"status\":\"PendingApproval\"",
            await confirmed.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var selfApproval = await operatorClient.PostAsJsonAsync(
            $"/admin/v1/action-requests/{actionId}/approvals",
            new { decision = "Approve", comment = "本人尝试审批" });
        Assert.Equal(HttpStatusCode.Forbidden, selfApproval.StatusCode);

        using var approverClient = factory.CreateClient();
        approverClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminWebApplicationFactory.ApproverToken);
        using var approved = await approverClient.PostAsJsonAsync(
            $"/admin/v1/action-requests/{actionId}/approvals",
            new { decision = "Approve", comment = "已核对房间状态与关联工单" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Contains(
            "\"status\":\"ApprovedAwaitingExecution\"",
            await approved.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var auditResponse = await approverClient.GetAsync("/admin/v1/audit");
        auditResponse.EnsureSuccessStatusCode();
        var allAudit = await auditResponse.Content.ReadFromJsonAsync<AdminAuditRecord[]>();
        Assert.NotNull(allAudit);
        var ordered = allAudit
            .Where(item => item.TargetId == AdminTestLobbyMonitoringClient.RoomId)
            .OrderBy(item => item.Sequence)
            .ToArray();
        Assert.Equal(3, ordered.Length);
        Assert.Equal(ordered[0].RecordHash, ordered[1].PreviousHash);
        Assert.Equal(ordered[1].RecordHash, ordered[2].PreviousHash);

        using var operatorAudit = await operatorClient.GetAsync("/admin/v1/audit");
        Assert.Equal(HttpStatusCode.Forbidden, operatorAudit.StatusCode);
    }

    [Fact]
    public async Task RoomManagementRejectsReadOnlyRoleStaleStateAndResultMutationType()
    {
        using var readOnlyClient = factory.CreateClient();
        readOnlyClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminWebApplicationFactory.Token);
        using var forbidden = await readOnlyClient.PostAsJsonAsync(
            "/admin/v1/action-requests",
            new
            {
                actionType = "MarkRoomAbnormal",
                targetId = AdminTestLobbyMonitoringClient.RoomId,
                reason = "只读人员不应能够创建管理操作",
                ticketId = "INC-READONLY-001",
                expectedStateSequence = 7
            });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var operatorClient = factory.CreateClient();
        operatorClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminWebApplicationFactory.OperatorToken);
        using var stale = await operatorClient.PostAsJsonAsync(
            "/admin/v1/action-requests",
            new
            {
                actionType = "MarkRoomAbnormal",
                targetId = AdminTestLobbyMonitoringClient.RoomId,
                reason = "使用过期房间状态序号发起管理操作",
                ticketId = "INC-STALE-001",
                expectedStateSequence = 6
            });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var resultMutation = await operatorClient.PostAsJsonAsync(
            "/admin/v1/action-requests",
            new
            {
                actionType = "ModifyMatchResult",
                targetId = AdminTestLobbyMonitoringClient.RoomId,
                reason = "该操作类型必须在模型绑定阶段被拒绝",
                ticketId = "INC-BLOCKED-001",
                expectedStateSequence = 7
            });
        Assert.Equal(HttpStatusCode.BadRequest, resultMutation.StatusCode);
    }

    [Fact]
    public async Task PlayerManagementUsesPlayerRbacSeparateApprovalAndMaskedStateSnapshot()
    {
        using var operatorClient = factory.CreateClient();
        operatorClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer", AdminWebApplicationFactory.PlayerOperatorToken);
        using var created = await operatorClient.PostAsJsonAsync(
            "/admin/v1/action-requests",
            new
            {
                actionType = "TemporaryFreezePlayer",
                targetId = AdminTestPlayerDirectoryClient.PlayerId,
                reason = "检测到异常会话，申请临时冻结账号",
                ticketId = "RISK-20260727-001",
                expectedStateSequence = (long?)null
            });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
        var actionId = createdJson.GetProperty("actionRequestId").GetString();
        Assert.NotNull(actionId);
        Assert.Equal("Player", createdJson.GetProperty("targetType").GetString());
        Assert.Equal(64, createdJson.GetProperty("expectedStateHash").GetString()?.Length);
        var body = createdJson.GetRawText();
        Assert.DoesNotContain("raw-installation-id", body, StringComparison.Ordinal);
        Assert.DoesNotContain("access-token", body, StringComparison.Ordinal);

        using var confirmed = await operatorClient.PostAsJsonAsync(
            $"/admin/v1/action-requests/{actionId}/confirm",
            new { targetConfirmation = AdminTestPlayerDirectoryClient.PlayerId });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        using var selfApproval = await operatorClient.PostAsJsonAsync(
            $"/admin/v1/action-requests/{actionId}/approvals",
            new { decision = "Approve", comment = "本人不得审批该冻结操作" });
        Assert.Equal(HttpStatusCode.Forbidden, selfApproval.StatusCode);

        using var roomApproverClient = factory.CreateClient();
        roomApproverClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer", AdminWebApplicationFactory.ApproverToken);
        using var wrongDomainApproval = await roomApproverClient.PostAsJsonAsync(
            $"/admin/v1/action-requests/{actionId}/approvals",
            new { decision = "Approve", comment = "房间审批角色不得审批玩家操作" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongDomainApproval.StatusCode);

        using var approverClient = factory.CreateClient();
        approverClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer", AdminWebApplicationFactory.PlayerApproverToken);
        using var approved = await approverClient.PostAsJsonAsync(
            $"/admin/v1/action-requests/{actionId}/approvals",
            new { decision = "Approve", comment = "已核对风险工单和脱敏账号状态" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Contains(
            "\"status\":\"ApprovedAwaitingExecution\"",
            await approved.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var outboxResponse = await approverClient.GetAsync("/admin/v1/command-outbox");
        outboxResponse.EnsureSuccessStatusCode();
        var outbox = await outboxResponse.Content.ReadFromJsonAsync<JsonElement>();
        var command = Assert.Single(outbox.EnumerateArray()
            .Where(item => item.GetProperty("actionRequestId").GetString() == actionId)
            .ToArray());
        Assert.Equal("Pending", command.GetProperty("status").GetString());
        Assert.Equal("Player", command.GetProperty("targetType").GetString());

        using var operatorOutbox = await operatorClient.GetAsync("/admin/v1/command-outbox");
        Assert.Equal(HttpStatusCode.Forbidden, operatorOutbox.StatusCode);
    }

    [Fact]
    public async Task PlayerManagementRejectsChangedSessionFingerprint()
    {
        var directory = factory.Services.GetRequiredService<AdminTestPlayerDirectoryClient>();
        directory.ActiveSessionCount = 1;
        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", AdminWebApplicationFactory.PlayerOperatorToken);
            using var created = await client.PostAsJsonAsync(
                "/admin/v1/action-requests",
                new
                {
                    actionType = "ForceLogoutPlayer",
                    targetId = AdminTestPlayerDirectoryClient.PlayerId,
                    reason = "异常会话需要强制下线并重新认证",
                    ticketId = "SEC-SESSION-001",
                    expectedStateSequence = (long?)null
                });
            created.EnsureSuccessStatusCode();
            var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
            var actionId = createdJson.GetProperty("actionRequestId").GetString();
            directory.ActiveSessionCount = 2;

            using var confirmation = await client.PostAsJsonAsync(
                $"/admin/v1/action-requests/{actionId}/confirm",
                new { targetConfirmation = AdminTestPlayerDirectoryClient.PlayerId });
            Assert.Equal(HttpStatusCode.Conflict, confirmation.StatusCode);
        }
        finally
        {
            directory.ActiveSessionCount = 1;
        }
    }
}

public sealed class AdminTestLobbyMonitoringClient : ILobbyMonitoringClient
{
    public const string RoomId = "room-management-test";

    private static readonly RoomMonitorSnapshot Room = new()
    {
        RoomId = RoomId,
        RoomCode = "880001",
        OwnerPlayerId = "guest-owner",
        RoundCount = 8,
        PublicRoom = true,
        AutoStart = true,
        MaximumPlayers = 4,
        RuleSnapshot = new Dictionary<string, JsonElement>
        {
            ["gameMode"] = JsonSerializer.SerializeToElement("Standard")
        },
        Lifecycle = "Playing",
        PlayerIds = ["guest-owner", "guest-two"],
        MatchId = "match-management-test",
        StateSequence = 7,
        CreatedAtUtc = DateTimeOffset.Parse("2026-07-27T01:00:00Z"),
        UpdatedAtUtc = DateTimeOffset.Parse("2026-07-27T01:10:00Z")
    };

    public Task<IReadOnlyList<RoomMonitorSnapshot>> ListRoomsAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RoomMonitorSnapshot>>([Room]);

    public Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId,
        CancellationToken cancellationToken) =>
        Task.FromResult<RoomRuntimeTelemetry?>(null);

    public Task<RoomTimelineEvent[]> ListEventsAsync(
        string roomId,
        CancellationToken cancellationToken) =>
        Task.FromResult(Array.Empty<RoomTimelineEvent>());

    public Task<PlayerPresenceSnapshot[]> GetPlayerPresenceAsync(
        IReadOnlyCollection<string> playerIds,
        CancellationToken cancellationToken) =>
        Task.FromResult(Array.Empty<PlayerPresenceSnapshot>());
}

public sealed class AdminTestAllocatorMonitoringClient : IAllocatorMonitoringClient
{
    public Task<IReadOnlyList<MonitoredInstance>> ListInstancesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MonitoredInstance>>([]);
}

public sealed class AdminTestPlayerDirectoryClient : IPlayerDirectoryClient
{
    public const string PlayerId = "guest-player-management-test";
    public int ActiveSessionCount { get; set; } = 1;

    public Task<AuthPlayerDirectoryItem[]> ListPlayersAsync(
        string? search,
        CancellationToken cancellationToken) =>
        Task.FromResult(Array.Empty<AuthPlayerDirectoryItem>());

    public Task<AuthPlayerDirectoryDetail?> GetPlayerAsync(
        string playerId,
        CancellationToken cancellationToken)
    {
        if (playerId != PlayerId)
            return Task.FromResult<AuthPlayerDirectoryDetail?>(null);
        var player = CreatePlayer();
        return Task.FromResult<AuthPlayerDirectoryDetail?>(new AuthPlayerDirectoryDetail(
            player,
            [
                new AuthSessionMonitor(
                    "session-a…",
                    DateTimeOffset.Parse("2026-07-27T01:00:00Z"),
                    DateTimeOffset.Parse("2026-08-27T01:00:00Z"),
                    null,
                    true)
            ],
            [
                new AuthLoginEvent(
                    "login-event-1",
                    PlayerId,
                    "device-derived-123",
                    "10.20.30.*",
                    "MahjongClient/1.0",
                    "Success",
                    DateTimeOffset.Parse("2026-07-27T01:00:00Z"))
            ],
            ["device-derived-123"],
            [
                new PlayerControlEvent(
                    "command-control-history",
                    PlayerId,
                    "MarkRiskAccount",
                    "Confirmed manual risk review",
                    "trace-control-history",
                    "RISK-HISTORY-001",
                    "risk-analyst",
                    "player-approver",
                    DateTimeOffset.Parse("2026-07-27T01:05:00Z"),
                    DateTimeOffset.Parse("2026-08-26T01:05:00Z"),
                    "manual-review",
                    0,
                    new PlayerControlState(
                        0,
                        "Active",
                        null,
                        null,
                        [],
                        null,
                        DateTimeOffset.UnixEpoch),
                    new PlayerControlState(
                        1,
                        "Active",
                        null,
                        null,
                        ["manual-review"],
                        DateTimeOffset.Parse("2026-08-26T01:05:00Z"),
                        DateTimeOffset.Parse("2026-07-27T01:05:00Z")))
            ]));
    }

    private AuthPlayerDirectoryItem CreatePlayer() =>
        new(
            PlayerId,
            "测试玩家",
            "Guest",
            "Active",
            DateTimeOffset.Parse("2026-07-20T01:00:00Z"),
            DateTimeOffset.Parse("2026-07-27T01:00:00Z"),
            DateTimeOffset.Parse("2026-07-27T01:00:00Z"),
            "device-derived-123",
            "10.20.30.*",
            ActiveSessionCount,
            0,
            null,
            null,
            []);
}
