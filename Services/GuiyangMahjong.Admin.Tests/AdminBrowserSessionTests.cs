using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuiyangMahjong.Admin.Domain;

namespace GuiyangMahjong.Admin.Tests;

/// <summary>
/// 验证 BFF 会话、CSRF、设备绑定和 Operations 兼容入口；测试只使用进程内会话存储，
/// 不读取本机身份配置或生产凭据。
/// </summary>
public sealed class AdminBrowserSessionTests(AdminWebApplicationFactory factory)
    : IClassFixture<AdminWebApplicationFactory>
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    [Fact]
    public async Task BearerCanBeExchangedForHttpOnlySessionAndOperationsAliasWorks()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AdminWebApplicationFactory.OperatorToken);
        client.DefaultRequestHeaders.Add("X-Admin-Device-Id", "test-device-a");

        using var exchange = await client.PostAsync("/admin/bff/v1/session", null);
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        Assert.Equal("no-store", exchange.Headers.CacheControl?.ToString());
        Assert.Contains("httponly", exchange.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
        var session = await exchange.Content.ReadFromJsonAsync<BrowserSessionResponse>();
        Assert.NotNull(session);
        Assert.False(string.IsNullOrWhiteSpace(session.CsrfToken));

        // 清除 Bearer 后只能依赖 Cookie，证明 Angular 不需要持久保存企业令牌。
        client.DefaultRequestHeaders.Authorization = null;
        using var overview = await client.GetAsync("/admin/operations/v1/overview");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
    }

    [Fact]
    public async Task CookieMutationRequiresCsrfAndDeviceBinding()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AdminWebApplicationFactory.OperatorToken);
        client.DefaultRequestHeaders.Add("X-Admin-Device-Id", "test-device-b");
        var exchange = await client.PostAsync("/admin/bff/v1/session", null);
        var session = await exchange.Content.ReadFromJsonAsync<BrowserSessionResponse>();
        Assert.NotNull(session);
        client.DefaultRequestHeaders.Authorization = null;

        using var missingCsrf = await client.DeleteAsync("/admin/bff/v1/session");
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);

        client.DefaultRequestHeaders.Add("X-Admin-CSRF", session.CsrfToken);
        using var logout = await client.DeleteAsync("/admin/bff/v1/session");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // 已撤销会话即使继续携带 Cookie 和 CSRF 也不能访问只读接口。
        using var afterLogout = await client.GetAsync("/admin/v1/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task SessionRejectsChangedDeviceWithoutExposingRawIdentifier()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AdminWebApplicationFactory.OperatorToken);
        client.DefaultRequestHeaders.Add("X-Admin-Device-Id", "trusted-device");
        using var exchange = await client.PostAsync("/admin/bff/v1/session", null);
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("X-Admin-Device-Id");
        client.DefaultRequestHeaders.Add("X-Admin-Device-Id", "unexpected-device");

        using var rejected = await client.GetAsync("/admin/v1/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.DoesNotContain("trusted-device", body, StringComparison.Ordinal);
        Assert.DoesNotContain("unexpected-device", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionExchangeRejectsMissingDeviceSignal()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AdminWebApplicationFactory.OperatorToken);
        using var response = await client.PostAsync("/admin/bff/v1/session", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ADMIN_DEVICE_REQUIRED", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HighRiskActionPersistsStructuredReasonConfirmationAndIdempotency()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AdminWebApplicationFactory.OperatorToken);
        var idempotencyKey = $"stage10-{Guid.NewGuid():N}";
        using var create = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/action-requests")
        {
            Content = JsonContent.Create(new
            {
                actionType = "ForceDissolveRoom",
                targetId = AdminTestLobbyMonitoringClient.RoomId,
                reason = "房间已连续异常并完成客服工单核验",
                reasonCode = "INCIDENT_RESPONSE",
                operationDescription = "终止当前异常房间并阻止旧实例继续接受玩家连接",
                ticketId = $"INC-{Guid.NewGuid():N}",
                expectedStateSequence = 7
            })
        };
        create.Headers.Add("Idempotency-Key", idempotencyKey);
        var createdResponse = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Accepted, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<AdminActionRecord>(WebJson);
        Assert.NotNull(created);
        Assert.Equal("INCIDENT_RESPONSE", created.ReasonCode);
        Assert.Equal(idempotencyKey, created.IdempotencyKey);
        Assert.Null(created.Confirmation);

        using var confirmedResponse = await client.PostAsJsonAsync(
            $"/admin/v1/action-requests/{created.ActionRequestId}/confirm",
            new { targetConfirmation = AdminTestLobbyMonitoringClient.RoomId });
        Assert.Equal(HttpStatusCode.OK, confirmedResponse.StatusCode);
        var confirmed = await confirmedResponse.Content.ReadFromJsonAsync<AdminActionRecord>(WebJson);
        Assert.Equal(AdminTestLobbyMonitoringClient.RoomId, confirmed?.Confirmation);
    }

    [Fact]
    public async Task OrdinaryViewerReceivesRoleBasedDeviceAndIpRedaction()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AdminWebApplicationFactory.Token);
        var detail = await client.GetFromJsonAsync<PlayerMonitorDetail>(
            $"/admin/v1/players/{AdminTestPlayerDirectoryClient.PlayerId}");

        Assert.NotNull(detail);
        Assert.StartsWith("device-", detail.Summary.CurrentDeviceId, StringComparison.Ordinal);
        Assert.DoesNotContain("device-derived-123", detail.Summary.CurrentDeviceId, StringComparison.Ordinal);
        Assert.Equal("10.20.*.*", detail.Summary.CurrentMaskedIp);
    }

    [Fact]
    public async Task TrustSafetyReadModelsExposeSourceAgeAndAuthorityFieldsWithoutWriteAccess()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AdminWebApplicationFactory.OperatorToken);
        using var response = await client.GetAsync(
            $"/admin/operations/v1/trust-safety/rooms/{AdminTestLobbyMonitoringClient.RoomId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"roomEpoch\"", body, StringComparison.Ordinal);
        Assert.Contains("\"stateVersion\"", body, StringComparison.Ordinal);
        Assert.Contains("\"dataSource\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("joinTicket", body, StringComparison.OrdinalIgnoreCase);

        // 未提供并通过案件 ABAC 时，默认玩家监控不得枚举关联工单标识。
        using var playerResponse = await client.GetAsync(
            $"/admin/operations/v1/trust-safety/players/{AdminTestPlayerDirectoryClient.PlayerId}");
        Assert.Equal(HttpStatusCode.OK, playerResponse.StatusCode);
        var playerBody = await playerResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"ticketIds\":[]", playerBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OrderRefund")]
    [InlineData("RulePublish")]
    [InlineData("ConfigurationPublish")]
    [InlineData("BatchSanction")]
    public async Task MissingBusinessOwnerCapabilitiesFailClosed(string actionType)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AdminWebApplicationFactory.GovernanceToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/action-requests")
        {
            Content = JsonContent.Create(new
            {
                actionType,
                targetId = "controlled-target-001",
                reason = "业务所有者能力尚未接入时验证失败关闭边界",
                reasonCode = "GOVERNANCE_REVIEW",
                operationDescription = "本请求不得绕过业务所有者接口或直接修改任何业务表",
                ticketId = $"GOV-{Guid.NewGuid():N}"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("ADMIN_OWNER_CAPABILITY_UNAVAILABLE", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private sealed record BrowserSessionResponse(string CsrfToken, DateTimeOffset ExpiresAtUtc);
}
