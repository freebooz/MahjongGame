using System.Net;
using System.Text;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Services;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Tests;

/// <summary>
/// 集中日志只读代理契约测试，确保查询范围、认证方式和标准字段解析不会被后续重构破坏。
/// </summary>
public sealed class CentralLogQueryClientTests
{
    /// <summary>
    /// Loki 请求必须携带服务端令牌并把 RoomId 固定进 LogQL；响应只映射允许导出的结构化字段。
    /// </summary>
    [Fact]
    public async Task QueryRoomUsesReadOnlyGatewayAndMapsContractFields()
    {
        const string token = "central-log-contract-token-00000001";
        var handler = new RecordingHandler(
            """
            {"status":"success","data":{"resultType":"streams","result":[
              {"stream":{"service_name":"GuiyangMahjong.Lobby","severity_text":"Information","TraceId":"trace-1","RoomId":"room-1","PlayerId":"player-1","MatchId":"match-1","ServerInstanceId":"instance-1","EventId":"event-1"},"values":[
                ["1785283200000000000","房间心跳已接收"]
              ]}
            ]}}
            """);
        var client = new LokiCentralLogQueryClient(
            new SingleClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                CentralLogs = new CentralLogOptions
                {
                    Enabled = true,
                    BaseUrl = "http://loki-query.local",
                    QueryToken = token,
                    TimeoutSeconds = 5,
                    MaxEntries = 100
                }
            }));

        var records = await client.QueryRoomAsync(
            "room-1",
            DateTimeOffset.Parse("2026-07-28T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-29T00:00:00Z"),
            CancellationToken.None);

        var record = Assert.Single(records);
        Assert.Equal("room-1", record.RoomId);
        Assert.Equal("trace-1", record.TraceId);
        Assert.Equal("GuiyangMahjong.Lobby", record.Service);
        Assert.Equal("房间心跳已接收", record.Message);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(token, handler.AuthorizationParameter);
        Assert.Contains(
            "RoomId=\"room-1\"",
            Uri.UnescapeDataString(handler.RequestUri!));
        Assert.DoesNotContain(token, handler.RequestUri);
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        /// <summary>截获只读查询请求并返回固定 Loki 流结果，不进行外部网络访问。</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        /// <summary>每次创建使用同一测试 Handler 的客户端，以便审查请求头和 URL。</summary>
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false);
    }
}
