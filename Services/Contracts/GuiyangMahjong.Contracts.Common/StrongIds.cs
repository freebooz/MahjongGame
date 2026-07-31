using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GuiyangMahjong.Contracts.Common;

/// <summary>
/// 强类型值的最小数据库和安全日志映射契约。
/// 数据库适配器必须显式调用 <see cref="ToDatabaseValue"/>，普通日志只能使用脱敏文本。
/// </summary>
public interface IStrongValue
{
    /// <summary>返回可交给数据库驱动的标量值；不得把该值写入无权限日志。</summary>
    object ToDatabaseValue();

    /// <summary>返回不包含原始标识的稳定日志表示。</summary>
    string ToSafeLogString();
}

/// <summary>字符串强类型 ID 的序列化、解析和数据库映射约束。</summary>
public interface IStrongStringId<TSelf> : IStrongValue
    where TSelf : struct, IStrongStringId<TSelf>
{
    /// <summary>仅用于传输和持久化的原始值；调用方负责避免日志泄漏。</summary>
    string Value { get; }

    /// <summary>解析并验证外部输入，无效输入抛出 <see cref="FormatException"/>。</summary>
    static abstract TSelf Parse(string value);

    /// <summary>尝试解析外部输入，不接受空白、控制字符或越界长度。</summary>
    static abstract bool TryParse(string? value, out TSelf result);
}

/// <summary>非负整数强类型版本/序号的序列化与数据库映射约束。</summary>
public interface IStrongInt64Value<TSelf> : IStrongValue
    where TSelf : struct, IStrongInt64Value<TSelf>
{
    /// <summary>非负原始数值。</summary>
    long Value { get; }

    /// <summary>验证并构造值；负数必须被拒绝。</summary>
    static abstract TSelf Parse(long value);
}

/// <summary>
/// 字符串强类型 ID 的 System.Text.Json 转换器。
/// JSON 是授权的协议边界，因此写入原值；普通 ToString 则始终脱敏。
/// </summary>
public sealed class StrongStringIdJsonConverter<TSelf> : JsonConverter<TSelf>
    where TSelf : struct, IStrongStringId<TSelf>
{
    /// <inheritdoc/>
    public override TSelf Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"{typeof(TSelf).Name} 必须是字符串。");
        try
        {
            return TSelf.Parse(reader.GetString() ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new JsonException(
                $"{typeof(TSelf).Name} 格式无效。",
                exception);
        }
    }

    /// <inheritdoc/>
    public override void Write(
        Utf8JsonWriter writer,
        TSelf value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>非负整数强类型值的 JSON 转换器。</summary>
public sealed class StrongInt64JsonConverter<TSelf> : JsonConverter<TSelf>
    where TSelf : struct, IStrongInt64Value<TSelf>
{
    /// <inheritdoc/>
    public override TSelf Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (!reader.TryGetInt64(out var value))
            throw new JsonException($"{typeof(TSelf).Name} 必须是 64 位整数。");
        try
        {
            return TSelf.Parse(value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new JsonException(
                $"{typeof(TSelf).Name} 不允许负数。",
                exception);
        }
    }

    /// <inheritdoc/>
    public override void Write(
        Utf8JsonWriter writer,
        TSelf value,
        JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}

/// <summary>集中定义标识和版本输入格式，避免不同服务逐渐接受不同字符集合。</summary>
public static partial class StrongValueValidation
{
    /// <summary>验证普通平台标识：1 到 128 字符，只允许可安全跨 HTTP、JSON 和数据库传输的字符。</summary>
    public static bool IsIdentifier(string? value) =>
        value is not null
        && IdentifierPattern().IsMatch(value);

    /// <summary>验证关联和幂等键：至少 8 字符，降低低熵键碰撞和误复用风险。</summary>
    public static bool IsOperationKey(string? value) =>
        value is not null
        && value.Length is >= 8 and <= 128
        && IdentifierPattern().IsMatch(value);

    /// <summary>验证规则或构建版本，接受数字点段及可选 prerelease/build 后缀。</summary>
    public static bool IsVersion(string? value) =>
        value is not null
        && VersionPattern().IsMatch(value);

    [GeneratedRegex(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        "^[0-9]+(?:\\.[0-9]+){0,3}(?:[-+][A-Za-z0-9.-]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

/// <summary>只暴露类型和原值哈希前缀的日志格式器，避免 ToString 意外泄漏账号标识。</summary>
public static class StrongIdLogFormatter
{
    /// <summary>计算不可逆短指纹；短指纹仅用于关联日志，不作为业务身份或安全凭据。</summary>
    public static string Format(string typeName, string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{typeName}(sha256:{Convert.ToHexString(digest[..4]).ToLowerInvariant()})";
    }
}

/// <summary>玩家平台身份标识；不等同于登录账号或设备。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<PlayerId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct PlayerId : IStrongStringId<PlayerId>
{
    private PlayerId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static PlayerId Parse(string value) =>
        TryParse(value, out var result)
            ? result
            : throw new FormatException("PlayerId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out PlayerId result)
    {
        result = default;
        if (!StrongValueValidation.IsIdentifier(value)) return false;
        result = new PlayerId(value!);
        return true;
    }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(PlayerId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>登录主体账号标识；与 PlayerId 分离以支持身份合并和渠道账号。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<AccountId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct AccountId : IStrongStringId<AccountId>
{
    private AccountId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static AccountId Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("AccountId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out AccountId result) { result = default; if (!StrongValueValidation.IsIdentifier(value)) return false; result = new AccountId(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(AccountId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>认证会话标识；生命周期限定为一次可撤销登录会话。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<SessionId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct SessionId : IStrongStringId<SessionId>
{
    private SessionId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static SessionId Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("SessionId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out SessionId result) { result = default; if (!StrongValueValidation.IsIdentifier(value)) return false; result = new SessionId(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(SessionId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>客户端设备安装或设备指纹标识；不得保存原始硬件序列号。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<DeviceId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct DeviceId : IStrongStringId<DeviceId>
{
    private DeviceId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static DeviceId Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("DeviceId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out DeviceId result) { result = default; if (!StrongValueValidation.IsIdentifier(value)) return false; result = new DeviceId(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(DeviceId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>房间控制面聚合标识；不能使用可展示房间码代替。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<RoomId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct RoomId : IStrongStringId<RoomId>
{
    private RoomId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static RoomId Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("RoomId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out RoomId result) { result = default; if (!StrongValueValidation.IsIdentifier(value)) return false; result = new RoomId(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(RoomId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>完整牌局标识；一场 Match 可包含多个 Round。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<MatchId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct MatchId : IStrongStringId<MatchId>
{
    private MatchId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static MatchId Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("MatchId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out MatchId result) { result = default; if (!StrongValueValidation.IsIdentifier(value)) return false; result = new MatchId(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(MatchId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>单局标识；必须与所属 Match 一同解释，不能表达结算结果。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<RoundId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct RoundId : IStrongStringId<RoundId>
{
    private RoundId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static RoundId Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("RoundId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out RoundId result) { result = default; if (!StrongValueValidation.IsIdentifier(value)) return false; result = new RoundId(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(RoundId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>Dedicated Server 进程或 Agones GameServer 实例标识。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<ServerInstanceId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct ServerInstanceId : IStrongStringId<ServerInstanceId>
{
    private ServerInstanceId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static ServerInstanceId Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("ServerInstanceId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out ServerInstanceId result) { result = default; if (!StrongValueValidation.IsIdentifier(value)) return false; result = new ServerInstanceId(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(ServerInstanceId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>一次可重试服务器分配流程的稳定标识。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<AllocationId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct AllocationId : IStrongStringId<AllocationId>
{
    private AllocationId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static AllocationId Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("AllocationId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out AllocationId result) { result = default; if (!StrongValueValidation.IsIdentifier(value)) return false; result = new AllocationId(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(AllocationId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>跨服务事件唯一标识；消费端 Inbox 以此执行去重。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<EventId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct EventId : IStrongStringId<EventId>
{
    private EventId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <summary>生成不含时间和业务信息的随机事件标识。</summary>
    public static EventId New() => new(Guid.NewGuid().ToString("N"));
    /// <inheritdoc/>
    public static EventId Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("EventId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out EventId result) { result = default; if (!StrongValueValidation.IsIdentifier(value)) return false; result = new EventId(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(EventId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>跨请求和事件链的关联标识；至少 8 字符以避免低熵碰撞。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<CorrelationId>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct CorrelationId : IStrongStringId<CorrelationId>
{
    private CorrelationId(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <summary>生成新的随机关联标识。</summary>
    public static CorrelationId New() => new(Guid.NewGuid().ToString("N"));
    /// <inheritdoc/>
    public static CorrelationId Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("CorrelationId 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out CorrelationId result) { result = default; if (!StrongValueValidation.IsOperationKey(value)) return false; result = new CorrelationId(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(CorrelationId), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>调用方生成的写请求幂等键；作用域必须由服务端另行限定。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<IdempotencyKey>))]
[DebuggerDisplay("{ToSafeLogString(),nq}")]
public readonly record struct IdempotencyKey : IStrongStringId<IdempotencyKey>
{
    private IdempotencyKey(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static IdempotencyKey Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("IdempotencyKey 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out IdempotencyKey result) { result = default; if (!StrongValueValidation.IsOperationKey(value)) return false; result = new IdempotencyKey(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(IdempotencyKey), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>规则集的不可变版本标识；用于事件和审计解释，不携带规则正文。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<RuleSetVersion>))]
public readonly record struct RuleSetVersion : IStrongStringId<RuleSetVersion>
{
    private RuleSetVersion(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static RuleSetVersion Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("RuleSetVersion 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out RuleSetVersion result) { result = default; if (!StrongValueValidation.IsVersion(value)) return false; result = new RuleSetVersion(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(RuleSetVersion), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>服务或客户端构建版本；用于兼容门禁和事件生产者审计。</summary>
[JsonConverter(typeof(StrongStringIdJsonConverter<BuildVersion>))]
public readonly record struct BuildVersion : IStrongStringId<BuildVersion>
{
    private BuildVersion(string value) => Value = value;
    /// <inheritdoc/>
    public string Value { get; }
    /// <inheritdoc/>
    public static BuildVersion Parse(string value) => TryParse(value, out var result) ? result : throw new FormatException("BuildVersion 格式无效。");
    /// <inheritdoc/>
    public static bool TryParse(string? value, out BuildVersion result) { result = default; if (!StrongValueValidation.IsVersion(value)) return false; result = new BuildVersion(value!); return true; }
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => StrongIdLogFormatter.Format(nameof(BuildVersion), Value);
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>房间代际序号，用于拒绝旧服务器或旧租约对新房间实例的写入。</summary>
[JsonConverter(typeof(StrongInt64JsonConverter<RoomEpoch>))]
public readonly record struct RoomEpoch : IStrongInt64Value<RoomEpoch>
{
    private RoomEpoch(long value) => Value = value;
    /// <inheritdoc/>
    public long Value { get; }
    /// <inheritdoc/>
    public static RoomEpoch Parse(long value) => value >= 0 ? new(value) : throw new ArgumentOutOfRangeException(nameof(value));
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => $"{nameof(RoomEpoch)}({Value})";
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>玩家操作单调序号，用于检测重复、乱序和缺口。</summary>
[JsonConverter(typeof(StrongInt64JsonConverter<ActionSequence>))]
public readonly record struct ActionSequence : IStrongInt64Value<ActionSequence>
{
    private ActionSequence(long value) => Value = value;
    /// <inheritdoc/>
    public long Value { get; }
    /// <inheritdoc/>
    public static ActionSequence Parse(long value) => value >= 0 ? new(value) : throw new ArgumentOutOfRangeException(nameof(value));
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => $"{nameof(ActionSequence)}({Value})";
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}

/// <summary>聚合状态乐观并发版本；仅允许随成功事务单调递增。</summary>
[JsonConverter(typeof(StrongInt64JsonConverter<StateVersion>))]
public readonly record struct StateVersion : IStrongInt64Value<StateVersion>
{
    private StateVersion(long value) => Value = value;
    /// <inheritdoc/>
    public long Value { get; }
    /// <inheritdoc/>
    public static StateVersion Parse(long value) => value >= 0 ? new(value) : throw new ArgumentOutOfRangeException(nameof(value));
    /// <inheritdoc/>
    public object ToDatabaseValue() => Value;
    /// <inheritdoc/>
    public string ToSafeLogString() => $"{nameof(StateVersion)}({Value})";
    /// <inheritdoc/>
    public override string ToString() => ToSafeLogString();
}
