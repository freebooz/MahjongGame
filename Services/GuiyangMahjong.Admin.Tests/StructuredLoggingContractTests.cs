using System.Text.Json;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Tests;

/// <summary>
/// 结构化日志 CI 契约门禁，验证必填字段、作用域关联和敏感数据脱敏。
/// </summary>
public sealed class StructuredLoggingContractTests
{
    /// <summary>
    /// 格式器必须始终输出 v1 字段；完整令牌、卡号和未脱敏 IP 不得出现在 JSON 中。
    /// </summary>
    [Fact]
    public void JsonFormatterWritesRequiredFieldsAndRedactsSensitiveValues()
    {
        var formatter = new MahjongJsonConsoleFormatter(
            new StaticOptionsMonitor<MahjongJsonConsoleFormatterOptions>(
                new MahjongJsonConsoleFormatterOptions
                {
                    ServiceName = "GuiyangMahjong.Admin",
                    EnvironmentName = "ContractTest",
                    IncludeScopes = true
                }));
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(new Dictionary<string, object?>
        {
            ["TraceId"] = "trace-contract-test",
            ["RoomId"] = "room-contract-test",
            ["PlayerId"] = "player-contract-test",
            ["Authorization"] = "Bearer complete-secret-token-value",
            ["ObservedIp"] = "10.20.30.40"
        });
        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            LogLevel.Information,
            "Contract.Category",
            new EventId(1, "ContractEvent"),
            [
                new("EventId", "event-contract-test"),
                new("PaymentCard", "4111111111111111"),
                new("{OriginalFormat}", "contract")
            ],
            null,
            (_, _) => "请求来自 10.20.30.40，Bearer abcdefghijklmnop");
        using var output = new StringWriter();

        formatter.Write(entry, scopes, output);

        using var document = JsonDocument.Parse(output.ToString());
        foreach (var field in StructuredLogContract.RequiredFields)
            Assert.True(document.RootElement.TryGetProperty(field, out _), field);
        Assert.Equal(
            "room-contract-test",
            document.RootElement.GetProperty("RoomId").GetString());
        var serialized = document.RootElement.GetRawText();
        Assert.DoesNotContain("complete-secret-token-value", serialized);
        Assert.DoesNotContain("4111111111111111", serialized);
        Assert.DoesNotContain("10.20.30.40", serialized);
        Assert.Contains("10.20.30.*", serialized);
        Assert.Contains("[REDACTED]", serialized);
    }

    /// <summary>
    /// 常见凭据和支付字段变体必须命中拒绝清单，防止调用方通过大小写或分隔符绕过。
    /// </summary>
    [Theory]
    [InlineData("Password")]
    [InlineData("access_token")]
    [InlineData("Authorization")]
    [InlineData("PostgresConnectionString")]
    [InlineData("payment-card")]
    [InlineData("ChatContent")]
    public void ForbiddenKeysAreRejected(string key) =>
        Assert.True(SensitiveDataSanitizer.IsForbiddenKey(key));

    private sealed class StaticOptionsMonitor<T>(T current) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = current;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
