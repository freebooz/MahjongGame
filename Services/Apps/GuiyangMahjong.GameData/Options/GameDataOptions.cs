using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.GameData.Options;

/// <summary>
/// GameData 运行配置。数据库、Lobby 权威校验和证据对象访问使用用途隔离凭据，
/// 运行身份默认不执行 DDL，生产不得使用 MetadataOnly 证据验证。
/// </summary>
public sealed class GameDataOptions
{
    public const string SectionName = "GameData";

    /// <summary>持久化模式；InMemory 只用于开发和测试，生产必须使用 Postgres。</summary>
    [Required] public string PersistenceMode { get; init; } = "InMemory";
    /// <summary>GameData 独占数据库连接；生产连接应激活 mahjong_game_data_rw 权限角色。</summary>
    public string PostgresConnectionString { get; init; } = string.Empty;
    /// <summary>仅本地开发允许自动迁移；生产迁移由独立 mahjong_migration 身份执行。</summary>
    public bool ApplyDatabaseMigrations { get; init; }
    /// <summary>Lobby 权威校验内网地址和用途隔离服务凭据。</summary>
    [Required, Url] public string LobbyBaseUrl { get; init; } = "http://127.0.0.1:18080";
    [MinLength(32)] public string LobbyAuthorityToken { get; init; } = string.Empty;
    /// <summary>Admin/GameRecords 查询专用只读凭据，不得用于结算提交。</summary>
    [MinLength(32)] public string MonitoringToken { get; init; } = string.Empty;
    /// <summary>DS 最终信封专用 HMAC 密钥；不得与 Join Ticket、Token 或数据库凭据复用。</summary>
    [MinLength(32)] public string SettlementSigningKey { get; init; } = string.Empty;
    /// <summary>Allocator 恢复遗留 DS Outbox 的专用凭据，只授权 recovery 入口。</summary>
    [MinLength(32)] public string AllocatorRecoveryToken { get; init; } = string.Empty;
    /// <summary>证据验证配置；FileSystem 用于共享恢复卷，HttpGateway 用于 MinIO/S3 对象网关。</summary>
    [Required] public EvidenceStorageOptions EvidenceStorage { get; init; } = new();
}

/// <summary>回放证据对象存在性和内容哈希验证配置。</summary>
public sealed class EvidenceStorageOptions
{
    /// <summary>MetadataOnly、FileSystem 或 HttpGateway；生产只允许后两者。</summary>
    [Required] public string Mode { get; init; } = "MetadataOnly";
    /// <summary>FileSystem 模式的只读根目录；请求对象键只能在该目录下解析。</summary>
    public string RootDirectory { get; init; } = string.Empty;
    /// <summary>HttpGateway 模式对象读取基址；对象键会作为单个转义路径段发送。</summary>
    [Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18087";
    /// <summary>对象网关只读凭据；不得与 Lobby 或 Admin 凭据复用。</summary>
    public string ReadToken { get; init; } = string.Empty;
    /// <summary>单个证据对象最大字节数，防止对象网关异常响应耗尽内存。</summary>
    [Range(1024, 1024L * 1024 * 1024)] public long MaximumObjectBytes { get; init; } = 128L * 1024 * 1024;
}
