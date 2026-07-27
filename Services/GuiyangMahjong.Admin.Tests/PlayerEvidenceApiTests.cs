using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;

namespace GuiyangMahjong.Admin.Tests;

public sealed class PlayerEvidenceApiTests(
    AdminWebApplicationFactory factory)
    : IClassFixture<AdminWebApplicationFactory>
{
    [Fact]
    public async Task EvidenceIngestionRequiresDedicatedCredentialAndRejectsSecrets()
    {
        using var client = factory.CreateClient();
        var eventId = Guid.NewGuid().ToString();
        using var unauthorized = await SendEvidenceAsync(
            client,
            eventId,
            token: null,
            new { orderId = "order-1", amountMinor = 1200 });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var rejectedId = Guid.NewGuid().ToString();
        using var rejected = await SendEvidenceAsync(
            client,
            rejectedId,
            AdminWebApplicationFactory.EvidenceToken,
            new
            {
                orderId = "order-secret",
                amountMinor = 1200,
                fullIp = "203.0.113.20"
            });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains(
            "fullIp",
            await rejected.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinancialEvidenceIsIdempotentRoleLimitedAndReadAudited()
    {
        using var ingestion = factory.CreateClient();
        var eventId = Guid.NewGuid().ToString();
        var occurredAt = DateTimeOffset.UtcNow;
        var payload = new
        {
            orderId = $"order-{eventId}",
            amountMinor = 12800,
            currency = "CNY",
            status = "Paid"
        };
        using var created = await SendEvidenceAsync(
            ingestion,
            eventId,
            AdminWebApplicationFactory.EvidenceToken,
            payload,
            occurredAt);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var duplicate = await SendEvidenceAsync(
            ingestion,
            eventId,
            AdminWebApplicationFactory.EvidenceToken,
            payload,
            occurredAt);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var duplicateBody =
            await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(duplicateBody.GetProperty("duplicate").GetBoolean());

        using var viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdminWebApplicationFactory.Token);
        using var forbidden = await viewer.GetAsync(
            $"/admin/v1/players/{AdminTestPlayerDirectoryClient.PlayerId}/payment-orders?ticketId=FIN-READ-001");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var operatorClient = factory.CreateClient();
        operatorClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdminWebApplicationFactory.PlayerOperatorToken);
        using var permitted = await operatorClient.GetAsync(
            $"/admin/v1/players/{AdminTestPlayerDirectoryClient.PlayerId}/payment-orders?ticketId=FIN-READ-001");
        Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
        var records =
            await permitted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            records.EnumerateArray(),
            item => item.GetProperty("eventId").GetString() == eventId);

        using var auditor = factory.CreateClient();
        auditor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdminWebApplicationFactory.PlayerApproverToken);
        var audits =
            await auditor.GetFromJsonAsync<AdminAuditRecord[]>(
                "/admin/v1/audit");
        Assert.NotNull(audits);
        Assert.Contains(
            audits,
            item => item.Operation == "SensitivePlayerEvidenceViewed"
                && item.TargetId == AdminTestPlayerDirectoryClient.PlayerId
                && item.TicketId == "FIN-READ-001");
    }

    [Fact]
    public async Task ChatPermissionRequiresIndependentScopedGrantAndAuditsCheck()
    {
        using var ingestion = factory.CreateClient();
        var grantId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        using var grant = await SendChatGrantAsync(
            ingestion,
            grantId,
            new
            {
                grantId,
                playerId = AdminTestPlayerDirectoryClient.PlayerId,
                ticketId = "CHAT-CASE-001",
                grantedTo = "chat-reviewer",
                approvedBy = "player-approver",
                reason = "Customer dispute requires a scoped compliance review.",
                traceId = $"trace-{Guid.NewGuid():N}",
                windowStartsAtUtc = now.AddDays(-1),
                windowEndsAtUtc = now,
                expiresAtUtc = now.AddHours(1),
                scopes = new[] { "metadata", "message-content" }
            });
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);

        using var reviewer = factory.CreateClient();
        reviewer.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdminWebApplicationFactory.ChatComplianceToken);
        var permission =
            await reviewer.GetFromJsonAsync<PlayerChatPermissionResult>(
                $"/admin/v1/players/{AdminTestPlayerDirectoryClient.PlayerId}/chat-permission?ticketId=CHAT-CASE-001");
        Assert.NotNull(permission);
        Assert.True(permission.Allowed);
        Assert.Contains("message-content", permission.Scopes);

        using var auditor = factory.CreateClient();
        auditor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdminWebApplicationFactory.PlayerApproverToken);
        var audits =
            await auditor.GetFromJsonAsync<AdminAuditRecord[]>(
                "/admin/v1/audit");
        Assert.NotNull(audits);
        Assert.Contains(
            audits,
            item => item.Operation == "PlayerChatPermissionChecked"
                && item.OperatorId == "chat-reviewer"
                && item.TicketId == "CHAT-CASE-001");
    }

    [Fact]
    public async Task ManagementRequestIdempotencyKeyReturnsTheSameRequest()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdminWebApplicationFactory.OperatorToken);
        var key = Guid.NewGuid().ToString();
        var request = new
        {
            actionType = "MarkRoomAbnormal",
            targetId = AdminTestLobbyMonitoringClient.RoomId,
            reason = "Idempotency retry must not create a second action request.",
            ticketId = "INC-IDEMPOTENCY-001",
            expectedStateSequence = 7
        };
        using var first = await SendActionAsync(client, key, request);
        using var second = await SendActionAsync(client, key, request);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstBody =
            await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody =
            await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            firstBody.GetProperty("actionRequestId").GetString(),
            secondBody.GetProperty("actionRequestId").GetString());
    }

    private static Task<HttpResponseMessage> SendEvidenceAsync(
        HttpClient client,
        string eventId,
        string? token,
        object data,
        DateTimeOffset? occurredAt = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/internal/projections/player-evidence")
        {
            Content = JsonContent.Create(new
            {
                eventId,
                playerId = AdminTestPlayerDirectoryClient.PlayerId,
                evidenceType = "PaymentOrder",
                occurredAtUtc = occurredAt ?? DateTimeOffset.UtcNow,
                sourceReference = $"payment-{eventId}",
                data,
                sensitivity = "Financial"
            })
        };
        request.Headers.Add("Idempotency-Key", eventId);
        if (token is not null)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendChatGrantAsync(
        HttpClient client,
        string grantId,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/internal/projections/player-chat-access-grants")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdminWebApplicationFactory.EvidenceToken);
        request.Headers.Add("Idempotency-Key", grantId);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendActionAsync(
        HttpClient client,
        string idempotencyKey,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/action-requests")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }
}
