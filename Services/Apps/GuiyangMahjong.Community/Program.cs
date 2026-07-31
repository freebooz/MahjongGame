using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GuiyangMahjong.Community.Domain;
using GuiyangMahjong.Community.Options;
using GuiyangMahjong.Community.Services;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

Console.OutputEncoding = Encoding.UTF8;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.AddMahjongObservability("GuiyangMahjong.Community");
builder.Services.AddOptions<CommunityOptions>().Bind(builder.Configuration.GetSection(CommunityOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(value => !builder.Environment.IsProduction()
        || (value.ChatGatewayToken.Length >= 32
            && value.AuthMonitoringToken.Length >= 32),
        "生产 Community 必须配置聊天网关和 Identity 只读凭据。")
    .Validate(value => new[] { value.ChatGatewayToken, value.AuthMonitoringToken }
        .Where(token => token.Length > 0).Distinct(StringComparer.Ordinal).Count()
        == new[] { value.ChatGatewayToken, value.AuthMonitoringToken }.Count(token => token.Length > 0),
        "Community 聊天网关和 Identity 只读凭据不得复用。")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient(nameof(AuthBackedChatPolicyService), (provider, client) =>
    client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<CommunityOptions>>().Value.AuthTimeoutSeconds));
builder.Services.AddSingleton<IChatPolicyService, AuthBackedChatPolicyService>();

var app = builder.Build();
app.UseMahjongObservability("GuiyangMahjong.Community", app.Environment.EnvironmentName);
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    try { await next(context); }
    catch (CommunityOperationException exception)
    {
        context.Response.StatusCode = exception.StatusCode;
        await context.Response.WriteAsJsonAsync(new { code = exception.Code, message = exception.Message,
            traceId = context.TraceIdentifier }, cancellationToken: context.RequestAborted);
    }
});
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/startup", () => Results.Ok(new { status = "started" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapPost("/internal/chat/messages/authorize", async (HttpContext context,
    AuthorizeChatMessageRequest request, IOptions<CommunityOptions> options, IChatPolicyService policy,
    TimeProvider clock, CancellationToken cancellationToken) =>
{
    CommunityValidation.RequireBearer(context, [options.Value.ChatGatewayToken]);
    CommunityValidation.Validate(request, clock.GetUtcNow());
    var result = await policy.AuthorizeAsync(request, cancellationToken);
    return result.Allowed ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status423Locked);
}).DisableAntiforgery().WithRequestTimeout(TimeSpan.FromSeconds(10));
app.Run();

/// <summary>集成测试宿主可发现的 Community 应用入口。</summary>
public partial class Program;

/// <summary>聊天授权请求的无状态安全校验，拒绝正文和不可信格式进入依赖调用。</summary>
internal static partial class CommunityValidation
{
    public static void RequireBearer(HttpContext context, IReadOnlyList<string> expectedTokens)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetBytes(header[7..].Trim()) : [];
        var valid = false;
        foreach (var expected in expectedTokens)
        {
            var wanted = Encoding.UTF8.GetBytes(expected);
            valid |= expected.Length >= 32 && supplied.Length == wanted.Length
                && CryptographicOperations.FixedTimeEquals(supplied, wanted);
            CryptographicOperations.ZeroMemory(wanted);
        }
        CryptographicOperations.ZeroMemory(supplied);
        if (!valid) throw new CommunityOperationException("COMMUNITY_UNAUTHORIZED",
            "A valid chat gateway credential is required.", StatusCodes.Status401Unauthorized);
    }
    public static void Validate(AuthorizeChatMessageRequest request, DateTimeOffset now)
    {
        if (!Guid.TryParse(request.MessageId, out _)) throw Invalid("messageId must be a UUID.");
        Identifier(request.PlayerId, "playerId"); Identifier(request.RoomId, "roomId");
        if (request.RequestedAtUtc < now.AddMinutes(-5) || request.RequestedAtUtc > now.AddMinutes(1))
            throw Invalid("requestedAtUtc is outside the accepted window.");
    }
    private static void Identifier(string? value, string name)
    {
        if (value is null || value.Length is < 3 or > 128 || !Safe().IsMatch(value))
            throw Invalid($"{name} contains invalid characters or length.");
    }
    private static CommunityOperationException Invalid(string message) =>
        new("COMMUNITY_INVALID_REQUEST", message, StatusCodes.Status400BadRequest);
    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)] private static partial Regex Safe();
}
