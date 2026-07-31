using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GuiyangMahjong.Configuration.Domain;

/// <summary>配置 Schema、安全和不可变版本规则验证结果；错误码稳定供 API 与审计使用。</summary>
public sealed record ConfigurationValidationResult(bool IsValid, string[] Errors)
{
    public static ConfigurationValidationResult Success { get; } = new(true, []);
}

/// <summary>
/// 动态配置验证器。采用强类型 Schema 并递归阻断疑似敏感字段，避免普通配置中心成为密钥分发渠道；
/// 规则包与镜像摘要使用 SHA-256/OCI digest 格式，为不可覆盖检查提供稳定身份。
/// </summary>
public static partial class ConfigurationPolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SensitiveTerms =
        ["password", "secret", "token", "privatekey", "certificate", "connectionstring", "signingkey"];

    public static ConfigurationValidationResult Validate(PlatformConfigurationPayload payload)
    {
        var errors = new List<string>();
        if (!Version.TryParse(payload.Client.MinimumVersion, out var minimum))
            errors.Add("CLIENT_MINIMUM_VERSION_INVALID");
        if (!Version.TryParse(payload.Client.RecommendedVersion, out var recommended))
            errors.Add("CLIENT_RECOMMENDED_VERSION_INVALID");
        if (minimum is not null && recommended is not null && recommended < minimum)
            errors.Add("CLIENT_RECOMMENDED_BELOW_MINIMUM");
        if (payload.Client.BlockedVersions.Any(item => !Version.TryParse(item, out _)))
            errors.Add("CLIENT_BLOCKED_VERSION_INVALID");
        if (payload.Client.SupportedProtocolVersions.Length == 0
            || payload.Client.SupportedProtocolVersions.Any(item => item.Length > 32 || !item.All(char.IsAsciiDigit)))
            errors.Add("PROTOCOL_VERSION_INVALID");
        if (payload.ApiProtocolVersion < 1)
            errors.Add("API_PROTOCOL_VERSION_INVALID");
        if (payload.Rollouts.GroupBy(item => item.ExperimentId, StringComparer.Ordinal).Any(group => group.Count() > 1)
            || payload.Rollouts.Any(item => !SafeId(item.ExperimentId, 80)
                || item.PercentageBasisPoints is < 0 or > 10_000))
            errors.Add("ROLLOUT_SCHEMA_INVALID");
        if (payload.FleetRoutes.Length == 0
            || payload.FleetRoutes.GroupBy(item => item.RouteId, StringComparer.Ordinal).Any(group => group.Count() > 1)
            // Allocator 仅按已冻结的版本契约选择 Fleet；相同契约出现两条路由会导致分配歧义，必须在发布前拒绝。
            || payload.FleetRoutes.GroupBy(item => (item.ServerBuild, item.RuleSetVersion, item.ProtocolVersion, item.Region))
                .Any(group => group.Count() > 1)
            || payload.FleetRoutes.Any(item => !ValidRoute(item)))
            errors.Add("FLEET_ROUTE_INVALID");
        if (payload.RoomTemplates.GroupBy(item => item.TemplateId, StringComparer.Ordinal).Any(group => group.Count() > 1)
            || payload.RoomTemplates.Any(item => !SafeId(item.TemplateId, 80)
                || item.RoundCount is < 1 or > 64 || item.MaximumPlayers is < 1 or > 4))
            errors.Add("ROOM_TEMPLATE_INVALID");
        if (!SafeId(payload.RiskPolicyVersion, 80))
            errors.Add("RISK_POLICY_VERSION_INVALID");
        var json = JsonSerializer.SerializeToElement(payload);
        if (ContainsSensitiveProperty(json))
            errors.Add("SENSITIVE_CONFIGURATION_FORBIDDEN");
        return errors.Count == 0 ? ConfigurationValidationResult.Success : new(false, errors.Distinct().ToArray());
    }

    /// <summary>验证新版本没有原地覆盖既有 Build 或 RuleSet 内容；相同版本名只能指向完全相同摘要。</summary>
    public static ConfigurationValidationResult ValidateImmutability(
        PlatformConfigurationPayload candidate,
        IEnumerable<PublishedConfiguration> history)
    {
        var errors = new List<string>();
        var oldRoutes = history.SelectMany(item => item.Payload.FleetRoutes).ToArray();
        foreach (var route in candidate.FleetRoutes)
        {
            if (oldRoutes.Any(old => old.ServerBuild == route.ServerBuild
                && old.ServerImageDigest != route.ServerImageDigest))
                errors.Add($"SERVER_BUILD_OVERWRITE:{route.ServerBuild}");
            if (oldRoutes.Any(old => old.RuleSetVersion == route.RuleSetVersion
                && old.RuleSetPackageHash != route.RuleSetPackageHash))
                errors.Add($"RULESET_OVERWRITE:{route.RuleSetVersion}");
        }
        return errors.Count == 0 ? ConfigurationValidationResult.Success : new(false, errors.Distinct().ToArray());
    }

    /// <summary>按规范生成正文 SHA-256；强类型序列化属性顺序稳定，数组顺序属于配置语义的一部分。</summary>
    public static string HashPayload(PlatformConfigurationPayload payload) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)));

    private static bool ValidRoute(FleetRoute route) =>
        SafeId(route.RouteId, 80) && SafeId(route.Fleet, 128)
        && SafeId(route.ServerBuild, 80) && OciDigest().IsMatch(route.ServerImageDigest)
        && SafeId(route.RuleSetVersion, 80) && Sha256().IsMatch(route.RuleSetPackageHash)
        && route.ProtocolVersion.Length is > 0 and <= 32 && route.ProtocolVersion.All(char.IsAsciiDigit)
        && SafeId(route.Region, 64) && SafeId(route.Cell, 64) && SafeId(route.CanaryGroup, 64);

    private static bool SafeId(string value, int maximum) =>
        value.Length is > 0 && value.Length <= maximum && SafeIdentifier().IsMatch(value);

    private static bool ContainsSensitiveProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = property.Name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                if (SensitiveTerms.Any(normalized.Contains) || ContainsSensitiveProperty(property.Value)) return true;
            }
        }
        return element.ValueKind == JsonValueKind.Array && element.EnumerateArray().Any(ContainsSensitiveProperty);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
    [GeneratedRegex("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex OciDigest();
}

/// <summary>稳定灰度求值器；使用 SHA-256 的前 64 位映射到 0..9999，不使用运行时随机 HashCode。</summary>
public static class StableRolloutEvaluator
{
    /// <summary>按白名单、维度和稳定万分桶判断是否进入 Canary；相同主体和实验永远得到相同结果。</summary>
    public static bool IsCanary(RolloutRule rule, RolloutSubject subject)
    {
        if (!rule.Enabled) return false;
        if (subject.PlayerId is { Length: > 0 } player && rule.PlayerWhitelist.Contains(player, StringComparer.Ordinal)) return true;
        if (subject.DeviceDigest is { Length: > 0 } device && rule.DeviceWhitelist.Contains(device, StringComparer.Ordinal)) return true;
        if (subject.IsTestAccount && rule.IncludeTestAccounts) return true;
        if (!Matches(rule.Channels, subject.Channel)
            || !Matches(rule.ClientVersions, subject.ClientVersion)
            || !Matches(rule.Platforms, subject.Platform)
            || !Matches(rule.Regions, subject.Region)) return false;
        var identity = subject.PlayerId ?? subject.DeviceDigest;
        if (string.IsNullOrWhiteSpace(identity)) return false;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}{rule.ExperimentId}"));
        var bucket = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest) % 10_000UL;
        return bucket < (ulong)rule.PercentageBasisPoints;
    }

    private static bool Matches(string[] allowList, string value) =>
        allowList.Length == 0 || allowList.Contains(value, StringComparer.OrdinalIgnoreCase);
}
