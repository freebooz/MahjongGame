using GuiyangMahjong.Auth.Administration;
using GuiyangMahjong.Auth.Auth;
using GuiyangMahjong.Auth.Devices;
using GuiyangMahjong.Auth.Infrastructure;
using GuiyangMahjong.Auth.Players;
using GuiyangMahjong.Auth.Sessions;

namespace GuiyangMahjong.Auth.Storage;

/// <summary>
/// 阶段 3 之前统一存储接口的兼容聚合适配器。现有 InMemory/PostgreSQL 实现仍实现该接口，
/// 以保持测试和部署兼容；新增业务代码必须依赖 Auth、Sessions、Players、Devices、
/// Administration 或 Infrastructure 的窄端口，禁止继续扩大本接口。
/// </summary>
public interface IAuthStore :
    IIdentityRepository,
    ISessionRepository,
    IDeviceAuditWriter,
    IIdentityAdministrationStore,
    IPlayerDirectoryReader,
    IIdentityStorageLifecycle;
