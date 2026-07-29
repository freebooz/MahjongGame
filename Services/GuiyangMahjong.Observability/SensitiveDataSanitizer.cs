using System.Text.RegularExpressions;

namespace GuiyangMahjong.Observability;

/// <summary>
/// 日志和跨度共用的敏感数据过滤器；采用字段拒绝清单和保守值检测，宁可少记录也不泄露凭据。
/// </summary>
public static partial class SensitiveDataSanitizer
{
    private static readonly string[] ForbiddenKeyFragments =
    [
        "password", "passwd", "token", "authorization", "cookie",
        "secret", "connectionstring", "signingkey", "pepper",
        "cardnumber", "paymentcard", "pan", "cvv", "chatcontent",
        "messagecontent", "rawip", "ipaddress"
    ];

    /// <summary>
    /// 判断属性名是否禁止进入日志或跨度；比较忽略大小写和常见分隔符。
    /// </summary>
    public static bool IsForbiddenKey(string key)
    {
        var normalized = new string(key
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return ForbiddenKeyFragments.Any(normalized.Contains);
    }

    /// <summary>
    /// 对允许字段的值执行二次保护：Bearer/JWT/完整 IPv4/卡号样式统一替换为受控文本。
    /// </summary>
    public static object? SanitizeValue(object? value)
    {
        if (value is null) return null;
        if (value is not string text) return value;
        if (BearerPattern().IsMatch(text)
            || JwtPattern().IsMatch(text)
            || PaymentCardPattern().IsMatch(text))
        {
            return "[REDACTED]";
        }
        return Ipv4Pattern().Replace(text, match =>
        {
            var parts = match.Value.Split('.');
            return $"{parts[0]}.{parts[1]}.{parts[2]}.*";
        });
    }

    /// <summary>
    /// 过滤结构化属性集合；禁止字段完全丢弃，其余字符串仍执行值级脱敏。
    /// </summary>
    public static Dictionary<string, object?> SanitizeProperties(
        IEnumerable<KeyValuePair<string, object?>> properties)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (property.Key == "{OriginalFormat}"
                || IsForbiddenKey(property.Key))
            {
                continue;
            }
            result[property.Key] = SanitizeValue(property.Value);
        }
        return result;
    }

    [GeneratedRegex(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{12,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(
        @"\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(
        @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PaymentCardPattern();

    [GeneratedRegex(
        @"(?<!\d)(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern();
}
