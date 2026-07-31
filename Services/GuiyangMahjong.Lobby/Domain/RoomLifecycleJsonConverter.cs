using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuiyangMahjong.Lobby.Domain;

/// <summary>
/// 房间状态 JSON 兼容转换器。
/// 读取同时接受阶段 4 规范名称与旧名称；写出对三个旧状态保留原线协议值，避免旧客户端立即失效。
/// </summary>
public sealed class RoomLifecycleJsonConverter : JsonConverter<RoomLifecycle>
{
    /// <summary>读取状态字符串；未知或数字值失败关闭，防止绕过状态机白名单。</summary>
    public override RoomLifecycle Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("房间状态必须是字符串。");
        }

        var value = reader.GetString();
        if (value is not null
            && Enum.TryParse<RoomLifecycle>(
                value,
                ignoreCase: false,
                out var lifecycle)
            && Enum.IsDefined(lifecycle))
        {
            return lifecycle;
        }

        throw new JsonException($"不支持的房间状态：{value}");
    }

    /// <summary>
    /// 写出稳定线协议值。
    /// Created/Finished/Aborted 暂映射为 Creating/Closed/Failed；其他阶段 4 新状态按规范名称输出。
    /// </summary>
    public override void Write(
        Utf8JsonWriter writer,
        RoomLifecycle value,
        JsonSerializerOptions options)
    {
        var wireValue = value switch
        {
            RoomLifecycle.Created => "Creating",
            RoomLifecycle.Finished => "Closed",
            RoomLifecycle.Aborted => "Failed",
            _ => value.ToString()
        };
        writer.WriteStringValue(wireValue);
    }
}
