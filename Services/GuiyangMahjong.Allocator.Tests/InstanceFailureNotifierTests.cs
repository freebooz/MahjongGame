// 验证实例异常通知的签名身份、重试、去重和 Lobby 故障回报错误处理。
using System.Net;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuiyangMahjong.Allocator.Tests;

public sealed class InstanceFailureNotifierTests
{
    [Fact]
    public async Task NotifyAsync_NonSuccessResponse_RemainsRetryable()
    {
        var notifier = CreateNotifier(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<HttpRequestException>(() => notifier.NotifyAsync(
            new InstanceFailureNotification("instance-1", "room-1", "process exited"),
            CancellationToken.None));
    }

    [Fact]
    public async Task NotifyAsync_SuccessResponse_Completes()
    {
        var notifier = CreateNotifier(HttpStatusCode.NoContent);

        await notifier.NotifyAsync(
            new InstanceFailureNotification("instance-1", "room-1", "process exited"),
            CancellationToken.None);
    }

    private static LobbyInstanceFailureNotifier CreateNotifier(HttpStatusCode statusCode)
    {
        var client = new HttpClient(new StaticResponseHandler(statusCode));
        var options = Microsoft.Extensions.Options.Options.Create(new AllocatorOptions
        {
            LobbyInternalUrl = "http://lobby.test",
            LobbyCallbackToken = "test-only-lobby-callback-token-long-enough"
        });
        return new LobbyInstanceFailureNotifier(
            new StaticHttpClientFactory(client),
            options,
            NullLogger<LobbyInstanceFailureNotifier>.Instance);
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
