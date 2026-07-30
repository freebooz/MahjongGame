using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Storage;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 协调大厅房间生命周期、Dedicated Server 租约、运行遥测与权威结算；领域实现按职责拆分为 partial 模块。
/// </summary>
public sealed partial class LobbyService
{
    /// <summary>
    /// 跨平台稳定的 camelCase JSON 配置，仅用于生成与 Dedicated Server 一致的结算正文摘要。
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions TelemetryJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    // 核心依赖由容器按单例或作用域生命周期注入；服务不拥有其释放责任。
    private readonly ILobbyStore store;
    private readonly IRoomPasswordService passwordService;
    private readonly ILobbyEventPublisher events;
    private readonly IAllocatorClient allocator;
    private readonly IJoinTicketIssuer joinTicketIssuer;
    private readonly IRoomMonitoringStore monitoringStore;
    private readonly LobbyOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LobbyService> logger;

    /// <summary>
    /// 构造大厅编排服务；配置在创建时固化，时间与外部依赖保持可替换以支持确定性测试。
    /// </summary>
    public LobbyService(
        ILobbyStore store,
        IRoomPasswordService passwordService,
        ILobbyEventPublisher events,
        IAllocatorClient allocator,
        IJoinTicketIssuer joinTicketIssuer,
        IRoomMonitoringStore monitoringStore,
        IOptions<LobbyOptions> options,
        TimeProvider timeProvider,
        ILogger<LobbyService> logger)
    {
        this.store = store;
        this.passwordService = passwordService;
        this.events = events;
        this.allocator = allocator;
        this.joinTicketIssuer = joinTicketIssuer;
        this.monitoringStore = monitoringStore;
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }
}

