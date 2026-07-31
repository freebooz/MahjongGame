using GuiyangMahjong.Contracts.Common;
using Npgsql;

namespace GuiyangMahjong.BuildingBlocks.Persistence;

/// <summary>
/// 强类型 ID 与 Npgsql 标量参数之间的显式映射。
/// 映射不会调用 ToString，避免把脱敏日志文本错误写入业务列。
/// </summary>
public static class StrongIdNpgsqlMapping
{
    /// <summary>追加命名参数并写入强类型值的数据库标量。</summary>
    public static NpgsqlParameter AddStrongValue(
        this NpgsqlParameterCollection parameters,
        string parameterName,
        IStrongValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return parameters.AddWithValue(
            parameterName,
            value.ToDatabaseValue());
    }

    /// <summary>
    /// 从文本列读取并由具体强类型验证格式。
    /// reader 和 parser 均由仓储拥有，不能以反射接受外部类型名。
    /// </summary>
    public static TStrong ReadStrongString<TStrong>(
        this NpgsqlDataReader reader,
        int ordinal,
        Func<string, TStrong> parser)
        where TStrong : struct, IStrongValue =>
        parser(reader.GetString(ordinal));
}
