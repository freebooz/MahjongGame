using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Community.Domain;
using GuiyangMahjong.Community.Services;
using GuiyangMahjong.Community.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GuiyangMahjong.Community.Tests;

/// <summary>Community HTTP 测试宿主；替身只控制策略结果，不绕过真实认证和输入校验管线。</summary>
public sealed class CommunityFactory : WebApplicationFactory<Program>
{
    public const string ChatToken = "community-chat-gateway-token-long-enough-0001";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Community:ChatGatewayToken"] = ChatToken,
                ["Community:AuthMonitoringToken"] = "community-auth-monitor-token-long-enough-0002" }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatPolicyService>();
            services.AddSingleton<TestChatPolicyService>();
            services.AddSingleton<IChatPolicyService>(provider => provider.GetRequiredService<TestChatPolicyService>());
        });
    }
}

public sealed class CommunityApiTests(CommunityFactory factory) : IClassFixture<CommunityFactory>
{
    [Fact]
    public async Task AuthorizedAndMutedResultsPreserveContract()
    {
        var policy = factory.Services.GetRequiredService<TestChatPolicyService>();
        policy.Allowed = true;
        using var allowed = await Authorize();
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True((await allowed.Content.ReadFromJsonAsync<ChatPolicyResult>())!.Allowed);
        policy.Allowed = false;
        using var muted = await Authorize();
        Assert.Equal(HttpStatusCode.Locked, muted.StatusCode);
        Assert.False((await muted.Content.ReadFromJsonAsync<ChatPolicyResult>())!.Allowed);
    }

    [Fact]
    public async Task MissingCredentialAndInvalidRequestAreRejectedBeforePolicy()
    {
        using var client = factory.CreateClient();
        using var anonymous = await client.PostAsJsonAsync("/internal/chat/messages/authorize", Request());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/internal/chat/messages/authorize")
            { Content = JsonContent.Create(Request() with { MessageId = "not-a-uuid" }) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CommunityFactory.ChatToken);
        using var invalid = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private async Task<HttpResponseMessage> Authorize()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/internal/chat/messages/authorize")
            { Content = JsonContent.Create(Request()) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CommunityFactory.ChatToken);
        return await factory.CreateClient().SendAsync(message);
    }

    private static AuthorizeChatMessageRequest Request() => new(Guid.NewGuid().ToString(),
        "player-community-test", "room-community-test", DateTimeOffset.UtcNow);
}

/// <summary>Identity 依赖契约测试，验证禁言解析和上游故障失败关闭。</summary>
public sealed class AuthBackedChatPolicyServiceTests
{
    [Fact]
    public async Task MutedIdentityIsRejected()
    {
        var until = DateTimeOffset.UtcNow.AddHours(1);
        var service = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { player = new { mutedUntilUtc = until } })
        });
        var result = await service.AuthorizeAsync(Request(), default);
        Assert.False(result.Allowed);
        Assert.Equal(until, result.MutedUntilUtc);
    }

    [Fact]
    public async Task IdentityFailureFailsClosed()
    {
        var service = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var exception = await Assert.ThrowsAsync<CommunityOperationException>(
            () => service.AuthorizeAsync(Request(), default));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (HttpStatusCode)exception.StatusCode);
    }

    private static AuthBackedChatPolicyService Create(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new TestHttpClientFactory(new HttpClient(new StubHandler(response))),
            Microsoft.Extensions.Options.Options.Create(new CommunityOptions { AuthBaseUrl = "http://identity.test",
                AuthMonitoringToken = "identity-read-token-long-enough-00000001" }),
            TimeProvider.System, NullLogger<AuthBackedChatPolicyService>.Instance);

    private static AuthorizeChatMessageRequest Request() => new(Guid.NewGuid().ToString(),
        "player-policy-test", "room-policy-test", DateTimeOffset.UtcNow);

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}

/// <summary>确定性的聊天策略替身，用于验证允许与禁言响应的 HTTP 兼容性。</summary>
public sealed class TestChatPolicyService : IChatPolicyService
{
    public bool Allowed { get; set; } = true;
    public Task<ChatPolicyResult> AuthorizeAsync(AuthorizeChatMessageRequest request,
        CancellationToken cancellationToken) => Task.FromResult(new ChatPolicyResult(request.PlayerId,
            Allowed, Allowed ? null : DateTimeOffset.UtcNow.AddHours(1), Allowed ? "Allowed" : "Muted"));
}
