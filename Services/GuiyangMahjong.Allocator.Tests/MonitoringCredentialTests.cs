using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Tests;

public sealed class MonitoringCredentialTests
{
    private const string ServiceToken = "test-primary-allocator-token-that-is-long-enough";
    private const string MonitoringToken = "test-monitoring-token-that-is-read-only-and-long";

    [Fact]
    public async Task MonitoringTokenCanReadInstances()
    {
        var reachedEndpoint = false;
        var middleware = CreateMiddleware(_ =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(HttpMethods.Get, "/internal/instances");

        await middleware.InvokeAsync(context);

        Assert.True(reachedEndpoint);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task MonitoringTokenCannotDrainInstance()
    {
        var reachedEndpoint = false;
        var middleware = CreateMiddleware(_ =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(HttpMethods.Post, "/internal/instances/instance-1/drain");

        await middleware.InvokeAsync(context);

        Assert.False(reachedEndpoint);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static AllocatorServiceAuthenticationMiddleware CreateMiddleware(
        RequestDelegate next) =>
        new(next, Microsoft.Extensions.Options.Options.Create(new AllocatorOptions
        {
            ServiceToken = ServiceToken,
            MonitoringReadOnlyToken = MonitoringToken
        }));

    private static DefaultHttpContext CreateContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Authorization = $"Bearer {MonitoringToken}";
        context.Response.Body = new MemoryStream();
        return context;
    }
}
