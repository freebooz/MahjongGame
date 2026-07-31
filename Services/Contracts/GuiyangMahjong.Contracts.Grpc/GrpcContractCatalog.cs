namespace GuiyangMahjong.Contracts.Grpc;

/// <summary>
/// gRPC 通用契约目录。
/// 本阶段仅发布 proto 源和版本信息，不创建服务实现或生产监听端口。
/// </summary>
public static class GrpcContractCatalog
{
    /// <summary>proto package 名称；破坏性变更必须发布新的主版本 package。</summary>
    public const string Package = "guiyang.mahjong.platform.v1";

    /// <summary>随程序集复制和 NuGet 打包的 proto 相对路径。</summary>
    public const string ProtoPath = "Protos/platform_context_v1.proto";

    /// <summary>当前通用 gRPC 契约主版本。</summary>
    public const int SchemaVersion = 1;
}
